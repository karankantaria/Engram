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
    private const string ShowSignalName = "Engram.Show.4f1a";

    private Mutex? _single;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showRegistration;

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

        // Diagnostic: --ask "question" runs one real RAG query against seeded notes.
        var ai = Array.IndexOf(e.Args, "--ask");
        if (ai >= 0 && ai + 1 < e.Args.Length)
        {
            SelfTest.AskOne(e.Args[ai + 1]);
            Shutdown();
            return;
        }

        _single = new Mutex(initiallyOwned: true, "Engram.SingleInstance.4f1a", out bool isNew);
        if (!isNew)
        {
            // Already running (possibly hidden in the tray) — ask that instance
            // to surface its window, then exit quietly.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var ev))
                {
                    ev.Set();
                    ev.Dispose();
                }
            }
            catch { /* best effort */ }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();

        // Wait for a second instance to signal us; surface the window when it does.
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showSignal,
            (_, _) => window.Dispatcher.BeginInvoke(new Action(window.SurfaceFromAnotherInstance)),
            state: null, millisecondsTimeOutInterval: Timeout.Infinite, executeOnlyOnce: false);

        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showRegistration?.Unregister(null);
        _showSignal?.Dispose();
        if (_single is not null)
        {
            try { _single.ReleaseMutex(); } catch { }
            _single.Dispose();
        }
        base.OnExit(e);
    }
}
