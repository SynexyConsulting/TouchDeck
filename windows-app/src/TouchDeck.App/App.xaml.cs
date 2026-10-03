using System.IO;
using System.Threading;
using System.Windows;

namespace TouchDeck.App;

public partial class App : Application
{
    private const string MutexName = @"Local\TouchDeck.App";
    private const string ShowEventName = @"Local\TouchDeck.App.Show";
    private const string QuitEventName = @"Local\TouchDeck.App.Quit";

    private Mutex? single;
    private bool ownsMutex;
    private EventWaitHandle? showRequest;
    private EventWaitHandle? quitRequest;
    private AppController? controller;
    private TrayIcon? tray;
    private MainWindow? window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Installer hooks: --quit closes a running instance (before files are replaced);
        // --cleanup also removes the autostart entry (uninstall).
        if (e.Args.Contains("--quit") || e.Args.Contains("--cleanup"))
        {
            QuitRunningInstance();
            if (e.Args.Contains("--cleanup")) new Core.App.Autostart().Remove();
            Shutdown();
            return;
        }

        // One instance owns the serial port; a second launch just brings the first forward.
        single = new Mutex(true, MutexName, out bool first);
        ownsMutex = first;
        showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            showRequest.Set();
            Shutdown();
            return;
        }

        // Backstop: once running, log and keep going rather than vanish from the tray (the
        // session's own finally has already released any held input). A failure during
        // startup is not swallowed: an app with no window would look alive but do nothing.
        DispatcherUnhandledException += (_, ex) =>
        {
            LogError(ex.Exception);
            if (window is null) return;
            ex.Handled = true;
            // The backstop must not throw: if showing the error in the activity log fails too, the file has it.
            try { controller?.AddLog($"Unexpected error: {ex.Exception.GetType().Name}: {ex.Exception.Message}"); }
            catch (Exception again) { LogError(again); }
        };

        Core.Updates.UpdateSource? feed = null;
#if UPDATE_TEST_HOOKS
        // Test builds only (build.ps1 -UpdateTestHooks): a loopback feed signed with a test key.
        int f = Array.IndexOf(e.Args, "--update-feed"), k = Array.IndexOf(e.Args, "--update-key");
        if (f >= 0 && f + 1 < e.Args.Length && k >= 0 && k + 1 < e.Args.Length &&
            Uri.TryCreate(e.Args[f + 1], UriKind.Absolute, out var feedUri))
            feed = Core.Updates.UpdateSource.ForTest(feedUri, e.Args[k + 1]);
#endif

        controller = new AppController(Dispatcher, null, feed);
        controller.QuitForUpdate += Quit;
        window = new MainWindow(controller);
        window.Attach();
        tray = new TrayIcon(controller, ShowWindow, Quit);

        ThreadPool.RegisterWaitForSingleObject(showRequest, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, -1, executeOnlyOnce: false);
        quitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        ThreadPool.RegisterWaitForSingleObject(quitRequest, (_, _) => Dispatcher.BeginInvoke(Quit), null, -1, executeOnlyOnce: true);

        controller.Start();
        if (!e.Args.Contains("--minimized")) ShowWindow();

        int smoke = Array.IndexOf(e.Args, "--smoke"), smokeBoard = Array.IndexOf(e.Args, "--smoke-board");
        if (smoke >= 0 && smoke + 1 < e.Args.Length)
            _ = RunSmokeAsync(e.Args[smoke + 1], e.Args.Contains("--smoke-steps"),
                              smokeBoard >= 0 && smokeBoard + 1 < e.Args.Length ? e.Args[smokeBoard + 1] : null);
#if UPDATE_TEST_HOOKS
        int smokeUpdate = Array.IndexOf(e.Args, "--smoke-update");
        if (smokeUpdate >= 0 && smokeUpdate + 1 < e.Args.Length) _ = RunSmokeUpdateAsync(e.Args[smokeUpdate + 1]);
#endif
    }

#if UPDATE_TEST_HOOKS
    /// <summary>
    /// --smoke-update DIR: check the feed, write DIR\update.txt, and install an app update if one
    /// is offered (the installer restarts the app). Used by the end-to-end update test.
    /// </summary>
    private async Task RunSmokeUpdateAsync(string dir)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!controller!.AnyConnected && DateTime.UtcNow < deadline) await Task.Delay(200);
        var outcome = await controller.CheckForUpdatesAsync(manual: true);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "update.txt"),
        [
            $"app={AppController.AppVersion}",
            $"feed={(controller.UpdateSourceIsTest ? "test" : "official")}",
            $"error={outcome.Error}",
            $"nothing={outcome.NothingPublished}",
            $"offer_app={outcome.Choice?.App?.Version.ToString(3)}",
            $"offer_fw={outcome.Choice?.Firmware?.Version.ToString(3)}",
            $"app_text={controller.AppUpdateText}",
        ]);
        if (controller.CanInstallApp) await controller.InstallAppUpdateAsync();   // quits for the installer
        else Quit();
    }
#endif

    /// <summary>
    /// --smoke DIR: wait for a board (up to 10 s), then write DIR\smoke.png (the window),
    /// DIR\mirror.png (the selected board's device view at 1:1, firmware 1.7.0+) and
    /// DIR\smoke.txt (what the app detected), and quit. Used by build.ps1 and the installer check.
    /// --smoke-board PORT selects that board first (for --smoke-steps with several attached).
    /// </summary>
    private async Task RunSmokeAsync(string dir, bool steps, string? port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!controller!.AnyConnected && DateTime.UtcNow < deadline) await Task.Delay(200);
        await Task.Delay(2500);                     // one heartbeat: the first diagnostics arrive, and the other boards
#if DEBUG
        if (Environment.GetCommandLineArgs().Contains("--smoke-demo-boards"))
        {
            Directory.CreateDirectory(dir);
            controller.AddDemoBoards();
            await Task.Delay(300);
            window!.SaveSnapshot(Path.Combine(dir, "smoke-tabs.png"));
            controller.Show(controller.Boards[^1]);      // the busy port's tab
            controller.OnlySelectedLog = true;
            await Task.Delay(300);
            window.SaveSnapshot(Path.Combine(dir, "smoke-tab-busy.png"));
            controller.Show(controller.Boards[1]);       // the bootloader's tab
            await Task.Delay(300);
            window.SaveSnapshot(Path.Combine(dir, "smoke-tab-new.png"));
            controller.OnlySelectedLog = false;
            controller.Show(controller.Boards[0]);
            await Task.Delay(300);
        }
#endif
        if (port is not null && controller.Find(port) is { } chosen) controller.Show(chosen);
        var b = controller.Selected;
        Directory.CreateDirectory(dir);
        if (steps) await RunSmokeStepsAsync(dir);
        window!.SaveSnapshot(Path.Combine(dir, "smoke.png"));
        bool mirrorSaved = window.SaveMirror(Path.Combine(dir, "mirror.png"));
        var settingsWin = new SettingsWindow(controller, window);   // the dialog too, for the design check
        settingsWin.Show();
        await Task.Delay(400);
        SettingsWindow.SaveCardSnapshot(settingsWin, Path.Combine(dir, "settings.png"));
        settingsWin.Close();
        File.WriteAllLines(Path.Combine(dir, "smoke.txt"),
        [
            $"app={AppController.AppVersion}",
            $"boards={controller.Boards.Count}",
            $"board_tabs={string.Join(", ", controller.Boards.Select(x => x.Label))}",
            $"status={b.State.Status}",
            $"board={b.State.Firmware?.Board}",
            $"port={b.Port}",
            $"firmware={b.State.Firmware?.Version}",
            $"mirror={b.MirrorAvailable}",
            $"fullmirror={b.FullMirror}",
            $"mirrorpng={mirrorSaved}",
            $"jig={b.JigOn}",
            $"letter={b.JigLetter}",
            $"boardclip={b.BoardClipText}",
            $"bundled={controller.BundledSummary}",
            $"newboard={b.NewBoard?.Describe()}",
            $"offer={b.UpdateText}",
            $"exe={Environment.ProcessPath}",
        ]);
        Quit();
    }

    /// <summary>
    /// --smoke-steps (with --smoke): drive the board through the app and save the device view
    /// after each step: mirror-jig.png, mirror-anim1/2.png (ANIM 1: the dot moves, no HID) and
    /// mirror-clip.png (a clip sent from the app). It ends on the clipboard page, so
    /// tools/mirror_check.py can compare mirror-clip.png with the board's framebuffer.
    /// </summary>
    private async Task RunSmokeStepsAsync(string dir)
    {
        var c = controller!.Selected;
        int clipPage = Core.Mirror.UiModels.ClipPage(c.MirrorModel);
        async Task Go(int page)
        {
            for (int i = 0; i < 3; i++) c.Swipe(left: false);
            for (int i = 0; i < page; i++) c.Swipe(left: true);
            await Task.Delay(1500);
        }
        await Go(clipPage + 1);
        window!.SaveMirror(Path.Combine(dir, "mirror-jig.png"));
        c.Animate(true);
        await Task.Delay(800);
        window.SaveMirror(Path.Combine(dir, "mirror-anim1.png"));
        await Task.Delay(400);
        window.SaveMirror(Path.Combine(dir, "mirror-anim2.png"));
        c.Animate(false);
        await Go(clipPage);
        c.SendText("Smoke test: sent from the app,\nshown by the board and its mirror.");
        await Task.Delay(3300);                     // the board's "Copied" message lasts 2.5 s
        window.SaveMirror(Path.Combine(dir, "mirror-clip.png"));
    }

    private void ShowWindow() => window?.ShowFromTray();

    /// <summary>Unexpected errors go to %APPDATA%\TouchDeck\errors.log (kept small) for bug reports.</summary>
    private static void LogError(Exception e)
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(Core.App.AppSettings.DefaultPath)!, "errors.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Core.App.LogRedaction.Redact(e.ToString())}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Asks a running instance to quit and waits (up to 5 s) until it has.</summary>
    private static void QuitRunningInstance()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var running)) return;
        using (running)
        using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName))
        {
            quit.Set();
            try
            {
                if (running.WaitOne(TimeSpan.FromSeconds(5))) running.ReleaseMutex();
            }
            catch (AbandonedMutexException) { }   // it exited without releasing: also gone
        }
    }

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
        if (ownsMutex) single?.ReleaseMutex();     // lets a waiting --quit return at once
        single?.Dispose();
        base.OnExit(e);
    }
}
