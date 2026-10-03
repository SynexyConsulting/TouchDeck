using System.ComponentModel;
using System.Windows;
using Forms = System.Windows.Forms;

namespace TouchDeck.App;

/// <summary>Notification-area icon: link colour, quick menu, balloons.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppController app;
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ToolStripMenuItem dryRun;
    private readonly Dictionary<Health, System.Drawing.Icon> icons;
    private bool disposed;

    public TrayIcon(AppController app, Action open, Action quit)
    {
        this.app = app;
        icons = new()
        {
            [Health.Ok] = Load("tray-ok.ico"),
            [Health.Idle] = Load("tray-idle.ico"),
            [Health.Bad] = Load("tray-bad.ico"),
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Touch Deck", null, (_, _) => open()).Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add("Send selection  (Ctrl+Alt+C)", null, (_, _) => app.SendSelection());
        dryRun = new Forms.ToolStripMenuItem("Dry run", null, (_, _) => app.DryRun = !app.DryRun);
        menu.Items.Add(dryRun);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());
        menu.Opening += (_, _) => dryRun.Checked = app.DryRun;

        icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => open();
        icon.BalloonTipClicked += (_, _) => open();
        app.PropertyChanged += OnChanged;
        app.Notify += (title, text) =>
        {
            if (!disposed) icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.None);
        };
        Refresh();
    }

    private static System.Drawing.Icon Load(string name)
    {
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"))!;
        using var s = info.Stream;
        return new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppController.Health) or nameof(AppController.Summary))
            Refresh();
    }

    private void Refresh()
    {
        icon.Icon = icons[app.Health];
        var tip = $"Touch Deck: {app.Summary}";
        icon.Text = tip.Length > 127 ? tip[..127] : tip;        // NotifyIcon's limit
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        app.PropertyChanged -= OnChanged;
        icon.Visible = false;
        icon.Dispose();
        foreach (var i in icons.Values) i.Dispose();
    }
}
