using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace Engram;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private Database _db = null!;
    private NoteService _notes = null!;
    private Librarian _librarian = null!;
    private IEmbedder _embedder = null!;
    private GlobalHotkey? _hotkey;
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Paths.EnsureCreated();
        AssetManager.ExtractWebAssets();

        _db = new Database(Paths.DbPath);
        _embedder = EmbedderFactory.Create();
        _notes = new NoteService(_db, _embedder);
        _librarian = new Librarian(_db);

        SetupTray();
        _hotkey = new GlobalHotkey(this,
            GlobalHotkey.MOD_CONTROL | GlobalHotkey.MOD_ALT, GlobalHotkey.VK_SPACE, OnHotkey);

        await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebViewProfileDir);
        await Web.EnsureCoreWebView2Async(env);

        Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "engram.local", Paths.WebRootDir, CoreWebView2HostResourceAccessKind.Allow);
        Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        // Let the WPF host handle file drops (the web layer can't see file paths).
        Web.AllowExternalDrop = false;
        Web.CoreWebView2.WebMessageReceived += OnWebMessage;
        Web.CoreWebView2.Navigate("https://engram.local/index.html");
    }

    // ---- JS <-> C# bridge -------------------------------------------------

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string reqId = "";
        string action = "";
        JsonElement payload = default;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl)) reqId = idEl.GetString() ?? "";
            action = root.GetProperty("action").GetString() ?? "";
            if (root.TryGetProperty("payload", out var p)) payload = p.Clone();
        }
        catch { return; }

        try
        {
            var result = await DispatchAsync(action, payload);
            PostResponse(reqId, true, result, null);
        }
        catch (Exception ex)
        {
            PostResponse(reqId, false, null, ex.Message);
        }
    }

    private async Task<object?> DispatchAsync(string action, JsonElement payload)
    {
        switch (action)
        {
            case "init":
                return new
                {
                    backend = _notes.EmbedderBackend,
                    graph = _notes.GetGraph(),
                    suggestions = _db.GetPendingSuggestions(),
                };

            case "graph":
                return _notes.GetGraph();

            case "capture":
            {
                var text = payload.GetProperty("text").GetString() ?? "";
                var note = await Task.Run(() => _notes.Capture(text));
                return new { note, graph = _notes.GetGraph() };
            }

            case "update":
            {
                var id = payload.GetProperty("id").GetInt64();
                var text = payload.GetProperty("text").GetString() ?? "";
                var note = await Task.Run(() => _notes.Update(id, text));
                return new { note, graph = _notes.GetGraph() };
            }

            case "note":
                return _notes.GetNote(payload.GetProperty("id").GetInt64());

            case "delete":
            {
                var id = payload.GetProperty("id").GetInt64();
                await Task.Run(() => _notes.Delete(id));
                return new { graph = _notes.GetGraph() };
            }

            case "search":
            {
                var query = payload.GetProperty("query").GetString() ?? "";
                return await Task.Run(() => _notes.Search(query));
            }

            case "librarian":
            {
                var res = await _librarian.RunAsync();
                return new
                {
                    result = res,
                    graph = _notes.GetGraph(),
                    suggestions = _db.GetPendingSuggestions(),
                };
            }

            case "suggestions":
                return _db.GetPendingSuggestions();

            case "resolveSuggestion":
                return await ResolveSuggestionAsync(payload);

            case "renameCluster":
            {
                var id = payload.GetProperty("id").GetInt64();
                var name = payload.GetProperty("name").GetString() ?? "";
                _db.RenameCluster(id, name);
                return new { graph = _notes.GetGraph() };
            }

            case "export":
                return await ExportAsync();

            case "import":
                return await ImportDialogAsync();

            default:
                throw new InvalidOperationException($"unknown action '{action}'");
        }
    }

    private async Task<object?> ResolveSuggestionAsync(JsonElement payload)
    {
        var id = payload.GetProperty("id").GetInt64();
        var accept = payload.TryGetProperty("accept", out var a) && a.GetBoolean();
        var sug = _db.GetSuggestion(id);

        if (sug is not null && accept)
        {
            if (sug.kind == "merge")
            {
                using var doc = JsonDocument.Parse(sug.payload);
                var from = doc.RootElement.GetProperty("from").GetInt64();
                var to = doc.RootElement.GetProperty("to").GetInt64();
                await Task.Run(() => _notes.Merge(from, to));
            }
            _db.SetSuggestionStatus(id, "accepted");
            if (sug.kind == "link") await Task.Run(() => _notes.Reindex());
        }
        else
        {
            _db.SetSuggestionStatus(id, "dismissed");
        }

        return new { graph = _notes.GetGraph(), suggestions = _db.GetPendingSuggestions() };
    }

    private async Task<object?> ExportAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"engram-export-{DateTime.Now:yyyyMMdd}.zip",
            Filter = "Zip archive (*.zip)|*.zip",
            DefaultExt = ".zip",
        };
        if (dlg.ShowDialog(this) == true)
        {
            var path = await Task.Run(() => _notes.ExportZip(dlg.FileName));
            return new { path };
        }
        return new { path = (string?)null };
    }

    private async Task<object?> ImportDialogAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Import files into engram",
            Filter = "Notes (*.md;*.txt;*.pdf)|*.md;*.markdown;*.txt;*.pdf|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            var res = await Task.Run(() => _notes.ImportFiles(dlg.FileNames));
            return new { result = res, graph = _notes.GetGraph() };
        }
        return new { result = (object?)null, graph = _notes.GetGraph() };
    }

    // ---- drag & drop import (handled at the WPF layer) --------------------

    private void WebHost_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void WebHost_DragLeave(object sender, System.Windows.DragEventArgs e)
        => DropOverlay.Visibility = Visibility.Collapsed;

    private async void WebHost_Drop(object sender, System.Windows.DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);

        PostEvent("importing");
        var res = await Task.Run(() => _notes.ImportFiles(paths));
        PostEvent("imported", new { result = res, graph = _notes.GetGraph() });
    }

    private void PostResponse(string id, bool ok, object? result, string? error)
    {
        var msg = JsonSerializer.Serialize(new { id, ok, result, error }, JsonOpts);
        Web.CoreWebView2?.PostWebMessageAsJson(msg);
    }

    private void PostEvent(string evt, object? payload = null)
    {
        var msg = JsonSerializer.Serialize(new { @event = evt, payload }, JsonOpts);
        Web.CoreWebView2?.PostWebMessageAsJson(msg);
    }

    // ---- window chrome ----------------------------------------------------

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Hide(); // to tray

    // ---- tray + hotkey ----------------------------------------------------

    private void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "engram — Ctrl+Alt+Space",
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Capture (Ctrl+Alt+Space)", null, (_, _) => OnHotkey());
        menu.Items.Add("Show engram", null, (_, _) => ShowFromTray());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
    }

    private void OnHotkey()
    {
        ShowFromTray();
        PostEvent("focusCapture");
    }

    private void ExitApp()
    {
        _reallyExit = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _hotkey?.Dispose();
        _tray?.Dispose();
        _db?.Dispose();
    }
}
