using System.Diagnostics;
using System.IO;

namespace Engram;

/// <summary>
/// Shared wrapper around the local `claude` CLI in non-interactive print mode.
/// Used by both the librarian (cluster naming / suggestions) and the ask
/// assistant (RAG Q&A). The prompt is piped via stdin so length/escaping is a
/// non-issue. Uses the user's existing claude auth (subscription, typically).
/// </summary>
internal static class ClaudeCli
{
    public static async Task<string> RunAsync(string prompt, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveExe(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Paths.DataDir,
        };
        psi.ArgumentList.Add("-p");                 // print mode (non-interactive)
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("text");

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        await proc.StandardInput.WriteAsync(prompt);
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException($"claude exited {proc.ExitCode}: {stderr}");
        return stdout;
    }

    private static string ResolveExe()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        return File.Exists(local) ? local : "claude";
    }
}
