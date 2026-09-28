using System.IO;
using System.Threading;
using System.Windows;

namespace TouchDeck.App;

public partial class App : Application
{
    private const string MutexName = @"Local\TouchDeck.App";
    private const string ShowEventName = @"Local\TouchDeck.App.Show";

    private Mutex? single;
    private EventWaitHandle? showRequest;
    private AppController? controller;
    private TrayIcon? tray;
    private MainWindow? window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance owns the serial port; a second launch just brings the first forward.
        single = new Mutex(true, MutexName, out bool first);
        showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            showRequest.Set();
            Shutdown();
            return;
        }

        controller = new AppController(Dispatcher);
        window = new MainWindow(controller);
        window.Attach();
        tray = new TrayIcon(controller, ShowWindow, Quit);

        ThreadPool.RegisterWaitForSingleObject(showRequest, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, -1, executeOnlyOnce: false);

        controller.Start();
        if (!e.Args.Contains("--minimized")) ShowWindow();

        int smoke = Array.IndexOf(e.Args, "--smoke");
        if (smoke >= 0 && smoke + 1 < e.Args.Length) _ = RunSmokeAsync(e.Args[smoke + 1]);
    }

    /// <summary>
    /// --smoke DIR: wait for a board (up to 10 s), then write DIR\smoke.png (the window) and
    /// DIR\smoke.txt (what the app detected), and quit. Used by build.ps1 and the installer check.
    /// </summary>
    private async Task RunSmokeAsync(string dir)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!controller!.IsConnected && DateTime.UtcNow < deadline) await Task.Delay(200);
        await Task.Delay(2500);                     // one heartbeat: the first diagnostics arrive
        Directory.CreateDirectory(dir);
        window!.SaveSnapshot(Path.Combine(dir, "smoke.png"));
        File.WriteAllLines(Path.Combine(dir, "smoke.txt"),
        [
            $"app={AppController.AppVersion}",
            $"status={controller.State.Status}",
            $"board={controller.State.Firmware?.Board}",
            $"port={controller.Port}",
            $"firmware={controller.State.Firmware?.Version}",
            $"bundled={controller.BundledSummary}",
            $"exe={Environment.ProcessPath}",
        ]);
        Quit();
    }

    private void ShowWindow() => window?.ShowFromTray();

    private void Quit()
    {
        controller?.Dispose();                   // releases any key or button held for the board
        tray?.Dispose();
        if (window is not null)
        {
            window.AllowClose = true;
            window.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        controller?.Dispose();
        tray?.Dispose();
        single?.Dispose();
        base.OnExit(e);
    }
}
