using System.Threading;
using System.Windows;

namespace Engram;

/// <summary>
/// Interaction logic for App.xaml. Enforces a single running instance — engram
/// owns one SQLite index and one WebView2 profile, so a second instance would
/// clash. Supports a headless --selftest mode for smoke-testing the pipeline.
/// </summary>
public partial class App : System.Windows.Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--selftest"))
        {
            SelfTest.Run();
            Shutdown();
            return;
        }

        // Diagnostic: --extract <path> dumps extracted text + chunk count.
        var ei = Array.IndexOf(e.Args, "--extract");
        if (ei >= 0 && ei + 1 < e.Args.Length)
        {
            SelfTest.ExtractOne(e.Args[ei + 1]);
            Shutdown();
            return;
        }

        _single = new Mutex(initiallyOwned: true, "Engram.SingleInstance.4f1a", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_single is not null)
        {
            try { _single.ReleaseMutex(); } catch { }
            _single.Dispose();
        }
        base.OnExit(e);
    }
}
