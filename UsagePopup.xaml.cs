using System.Windows;
using System.Windows.Controls;

namespace ClaudeUsageTray;

public partial class UsagePopup : Window
{
    public UsagePopup()
    {
        InitializeComponent();
        Deactivated += (_, _) => Hide();
    }

    public void Apply(UsageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            SessionPctText.Text = WeekPctText.Text = "--";
            SessionResetText.Text = WeekResetText.Text = string.Empty;
            SetBar(SessionFill, SessionRest, 0);
            SetBar(WeekFill, WeekRest, 0);
            FreshnessText.Text = "No usage history found. Is the Claude desktop app installed?";
            return;
        }

        SessionPctText.Text = $"{snapshot.SessionPct}% used";
        WeekPctText.Text = $"{snapshot.WeekPct}% used";
        SetBar(SessionFill, SessionRest, snapshot.SessionPct);
        SetBar(WeekFill, WeekRest, snapshot.WeekPct);

        SessionResetText.Text = snapshot.SessionReset is { } sr
            ? $"Resets {Format(sr)}"
            : "Reset time unknown";

        // Better to admit the anchor is unset than to show a confidently wrong time.
        WeekResetText.Text = snapshot.WeeklyReset is { } wr
            ? $"Resets {Format(wr)}"
            : "Reset time not set — see README";

        FreshnessText.Text = snapshot.IsStale
            ? $"Stale: last sample {Humanise(snapshot.Age)} ago. Start the Claude desktop app, " +
              "or open its usage panel from Claude's own tray icon to resume sampling."
            : $"Updated {Humanise(snapshot.Age)} ago";
        FreshnessText.Foreground = snapshot.IsStale ? StaleBrush : (System.Windows.Media.Brush)FindResource("Muted");
    }

    static readonly System.Windows.Media.Brush StaleBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(233, 168, 96));

    public static string HumaniseAge(TimeSpan span) => Humanise(span);

    static void SetBar(ColumnDefinition fill, ColumnDefinition rest, int pct)
    {
        pct = Math.Clamp(pct, 0, 100);
        fill.Width = new GridLength(pct, GridUnitType.Star);
        rest.Width = new GridLength(100 - pct, GridUnitType.Star);
    }

    static string Format(DateTime reset)
    {
        var left = reset - DateTime.Now;
        var when = reset.Date == DateTime.Today
            ? reset.ToString("h:mm tt")
            : reset.ToString("ddd MMM d, h:mm tt");
        return $"{when}  ({Humanise(left)})";
    }

    static string Humanise(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "moments";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{(int)span.TotalDays}d {span.Hours}h";
    }

    /// Anchor to the bottom-right of the work area, just above the tray.
    public void ShowNearTray()
    {
        Show();
        UpdateLayout();
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 12;
        Top = work.Bottom - ActualHeight - 12;
        Activate();
    }
}
