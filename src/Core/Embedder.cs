using System.IO;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Engram;

/// <summary>Turns text into a unit-length semantic vector.</summary>
public interface IEmbedder
{
    int Dim { get; }
    string Backend { get; }
    float[] Embed(string text);
}

internal static class EmbedderFactory
{
    /// <summary>Prefer the real ONNX model; fall back to a deterministic hash
    /// embedding so the app still runs (and the graph still forms by lexical
    /// overlap) before the model has been downloaded.</summary>
    public static IEmbedder Create()
    {
        if (File.Exists(Paths.ModelPath) && File.Exists(Paths.VocabPath))
        {
            try { return new OnnxEmbedder(Paths.ModelPath, Paths.VocabPath); }
            catch { /* fall through to hash */ }
        }
        return new HashEmbedder();
    }
}

/// <summary>
/// Deterministic fallback: feature-hashing bag-of-words. Not semantic, but
/// shared vocabulary still produces non-trivial cosine similarity, so the
/// auto-linking pipeline is demonstrable without the 90MB model.
/// </summary>
internal sealed class HashEmbedder : IEmbedder
{
    public int Dim => 384;
    public string Backend => "hash (no model)";

    public float[] Embed(string text)
    {
        var v = new float[Dim];
        foreach (var tok in Tokenize(text))
        {
            uint h = Fnv1a(tok);
            int idx = (int)(h % (uint)Dim);
            float sign = ((h >> 31) & 1) == 0 ? 1f : -1f;
            v[idx] += sign;
        }
        Normalize(v);
        return v;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static uint Fnv1a(string s)
    {
        uint hash = 2166136261;
        foreach (var c in s) { hash ^= c; hash *= 16777619; }
        return hash;
    }

    internal static void Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += (double)x * x;
        var norm = (float)Math.Sqrt(sum);
        if (norm < 1e-8f) return;
        for (int i = 0; i < v.Length; i++) v[i] /= norm;
    }
}

/// <summary>
/// Local, offline sentence embedder using all-MiniLM-L6-v2 via ONNX Runtime.
/// Mean-pools the token embeddings (masked) and L2-normalizes, matching the
/// sentence-transformers reference pipeline.
/// </summary>
internal sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private const int MaxTokens = 256;
    private readonly InferenceSession _session;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly bool _needsTokenType;
    private int _dim = 384;

    public int Dim => _dim;
    public string Backend => "onnx all-MiniLM-L6-v2";

    public OnnxEmbedder(string modelPath, string vocabPath)
    {
        _session = new InferenceSession(modelPath);
        _tokenizer = new WordPieceTokenizer(vocabPath);
        _needsTokenType = _session.InputMetadata.ContainsKey("token_type_ids");
    }

    public float[] Embed(string text)
    {
        var ids = _tokenizer.Encode(text, MaxTokens);
        int n = ids.Count;

        var inputIds = new DenseTensor<long>(new[] { 1, n });
        var mask = new DenseTensor<long>(new[] { 1, n });
        for (int i = 0; i < n; i++) { inputIds[0, i] = ids[i]; mask[0, i] = 1; }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };
        if (_needsTokenType)
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new[] { 1, n })));

        using var results = _session.Run(inputs);
        var hidden = PickHidden(results);
        var dims = hidden.Dimensions; // [1, seq, hidden]
        int seq = dims[1];
        int h = dims[2];
        _dim = h;

        // Masked mean pooling (mask is all 1 here, but keep it general).
        var pooled = new float[h];
        for (int t = 0; t < seq; t++)
            for (int k = 0; k < h; k++)
                pooled[k] += hidden[0, t, k];
        for (int k = 0; k < h; k++) pooled[k] /= seq;

        HashEmbedder.Normalize(pooled);
        return pooled;
    }

    private static Tensor<float> PickHidden(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        foreach (var r in results)
            if (r.Name.Contains("last_hidden", StringComparison.OrdinalIgnoreCase))
                return r.AsTensor<float>();
        return results.First().AsTensor<float>();
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Minimal BERT WordPiece tokenizer (uncased) for all-MiniLM-L6-v2. Reads the
/// standard vocab.txt (line index = token id). Lowercases, splits on whitespace
/// and punctuation, then greedy longest-match into wordpieces.
/// </summary>
internal sealed class WordPieceTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly int _cls, _sep, _unk;

    public WordPieceTokenizer(string vocabPath)
    {
        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        int i = 0;
        foreach (var line in File.ReadLines(vocabPath))
            _vocab[line.Trim()] = i++;
        _cls = _vocab.GetValueOrDefault("[CLS]", 101);
        _sep = _vocab.GetValueOrDefault("[SEP]", 102);
        _unk = _vocab.GetValueOrDefault("[UNK]", 100);
    }

    public IReadOnlyList<long> Encode(string text, int maxTokens)
    {
        var ids = new List<long> { _cls };
        foreach (var word in BasicSplit(text))
        {
            foreach (var piece in WordPiece(word))
            {
                if (ids.Count >= maxTokens - 1) break;
                ids.Add(piece);
            }
        }
        ids.Add(_sep);
        return ids;
    }

    private static IEnumerable<string> BasicSplit(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else if (char.IsPunctuation(ch) || char.IsSymbol(ch))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                yield return ch.ToString();
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private IEnumerable<long> WordPiece(string word)
    {
        int start = 0;
        var pieces = new List<long>();
        while (start < word.Length)
        {
            int end = word.Length;
            int found = -1;
            while (end > start)
            {
                var sub = (start == 0 ? "" : "##") + word.Substring(start, end - start);
                if (_vocab.TryGetValue(sub, out var id)) { found = id; break; }
                end--;
            }
            if (found == -1) { yield return _unk; yield break; }
            pieces.Add(found);
            start = end;
        }
        foreach (var p in pieces) yield return p;
    }
}
