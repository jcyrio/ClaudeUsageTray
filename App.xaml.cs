using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ClaudeUsageTray;

public partial class App : System.Windows.Application
{
    NotifyIconHost _tray = null!;
    UsagePopup _popup = null!;
    DispatcherTimer _poll = null!;
    DispatcherTimer _debounce = null!;
    FileSystemWatcher? _watcher;

    // Held for the process lifetime. The scheduled task relaunches this exe periodically
    // to self-heal, so a second copy must exit quietly rather than add a second tray icon.
    static Mutex? _instanceLock;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceLock = new Mutex(initiallyOwned: true, @"Local\ClaudeUsageTray", out var isOnlyInstance);
        if (!isOnlyInstance) { Shutdown(); return; }

        _popup = new UsagePopup();
        _tray = new NotifyIconHost();
        _tray.Clicked += (_, _) => TogglePopup();
        _tray.RefreshRequested += (_, _) => Refresh();
        _tray.ExitRequested += (_, _) => Shutdown();

        // The file gains a sample roughly every five minutes, but only while the
        // desktop app runs. The watcher gives a near-instant update when it does;
        // the timer keeps relative times ("in 3h 8m") honest when it does not.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();

        StartWatching();
        Refresh();

        // Lets the popup be opened without hitting the tray, e.g. from a shortcut key.
        if (e.Args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase)))
            _popup.ShowNearTray();
    }

    void StartWatching()
    {
        var resolved = UsageReader.ResolvePath();
        var dir = resolved is null ? null : Path.GetDirectoryName(resolved);
        if (dir is null || !Directory.Exists(dir)) return;

        try
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(resolved!))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            // Writes arrive as a burst of events; collapse them into one refresh.
            FileSystemEventHandler onChange = (_, _) =>
                Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Renamed += (_, _) =>
                Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
        }
        catch (Exception)
        {
            // Watching is an optimisation; the poll timer already guarantees refreshes.
            _watcher = null;
        }
    }

    UsageSnapshot? _lastGood;

    void Refresh()
    {
        // Even with retries a read can come back empty. Keep showing the last good
        // sample rather than flashing "no data" -- its displayed age already tells the
        // user how current it is, which is the honest signal. Only a read that has
        // never succeeded shows nothing.
        if (UsageReader.Read() is { } snapshot) _lastGood = snapshot;
        _popup.Apply(_lastGood);
        _tray.Update(_lastGood);
    }

    void TogglePopup()
    {
        if (_popup.IsVisible) { _popup.Hide(); return; }
        Refresh();
        _popup.ShowNearTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watcher?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}

/// Wraps the WinForms tray icon so the WPF side never touches Forms types directly.
public sealed class NotifyIconHost : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    Icon? _current;

    public event EventHandler? Clicked;
    public event EventHandler? RefreshRequested;
    public event EventHandler? ExitRequested;

    public NotifyIconHost()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Refresh now", null, (s, e) => RefreshRequested?.Invoke(s, e));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => ExitRequested?.Invoke(s, e));

        _icon = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "Claude usage",
            ContextMenuStrip = menu,
            Icon = TrayIconRenderer.Render(0)
        };
        _current = _icon.Icon;

        _icon.MouseClick += (s, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Clicked?.Invoke(s, EventArgs.Empty);
        };
    }

    public void Update(UsageSnapshot? snapshot)
    {
        var pct = snapshot?.SessionPct ?? 0;

        var previous = _current;
        _current = TrayIconRenderer.Render(pct);
        _icon.Icon = _current;
        previous?.Dispose();

        // NotifyIcon.Text is capped at 63 characters by the shell.
        _icon.Text = snapshot is null
            ? "Claude usage - no data"
            : $"Session {snapshot.SessionPct}%  |  Week {snapshot.WeekPct}%";
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
