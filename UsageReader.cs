using System.IO;
using System.Text.Json;

namespace ClaudeUsageTray;

public sealed record UsageSnapshot(
    int SessionPct,
    int WeekPct,
    DateTime SampledAt,
    DateTime? SessionReset,
    DateTime? WeeklyReset);

/// <summary>
/// Reads the rolling usage history the Claude desktop app maintains at
/// %APPDATA%\Claude\plan-usage-history.json. Samples look like:
///   { "t": 1786054150069, "org": "...", "u": { "fh": 16, "sd": 27 } }
/// where "fh" is five-hour session utilisation and "sd" is seven-day, both percent.
/// The app appends a sample roughly every five minutes while it is running, and
/// keeps about fourteen days of history.
/// </summary>
public static class UsageReader
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claude", "plan-usage-history.json");

    /// <summary>
    /// Weekly windows are fixed-anchor, and they reset overnight -- inside the gap
    /// where the desktop app is closed and not sampling. That makes the weekly reset
    /// impossible to derive from history, and it differs per account, so it cannot be
    /// a build-time constant either. The user seeds it once from /usage; until then
    /// the weekly reset time is reported as unknown rather than guessed.
    /// </summary>
    public static DateTime? WeeklyAnchor { get; set; } = Settings.LoadWeeklyAnchor();

    public static UsageSnapshot? Read(string? path = null)
    {
        var json = ReadWithRetry(path ?? DefaultPath);
        if (json is null) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("samples", out var samples) ||
                samples.ValueKind != JsonValueKind.Array ||
                samples.GetArrayLength() == 0)
                return null;

            var list = samples.EnumerateArray().ToArray();
            var last = list[^1];

            return new UsageSnapshot(
                SessionPct: Pct(last, "fh"),
                WeekPct: Pct(last, "sd"),
                SampledAt: SampleTime(last),
                SessionReset: FindSessionReset(list),
                WeeklyReset: NextWeeklyReset());
        }
    }

    /// <summary>
    /// The five-hour window recurs during waking hours, so unlike the weekly one its
    /// boundary is usually observable. Two signals mark it, and they are not equally
    /// good:
    ///
    /// A drop in utilisation is the reset itself, so it is accurate to within one
    /// sampling interval. A climb off zero only bounds it -- utilisation rounds to a
    /// whole percent, so it reads 0 for the first several minutes of a window and the
    /// climb lands late (observed ~14 minutes late against the built-in panel).
    ///
    /// So prefer the drop, and fall back to the climb only when no drop belongs to the
    /// current window -- which happens when the window rolled over while idle.
    /// </summary>
    static DateTime? FindSessionReset(JsonElement[] list)
    {
        return FindBoundary(list, static (cur, prev) => cur < prev)
            ?? FindBoundary(list, static (cur, prev) => prev == 0 && cur > 0);
    }

    static DateTime? FindBoundary(JsonElement[] list, Func<int, int, bool> isBoundary)
    {
        for (int i = list.Length - 1; i > 0; i--)
        {
            if (isBoundary(Pct(list[i], "fh"), Pct(list[i - 1], "fh")))
            {
                var reset = SampleTime(list[i]).AddHours(5);
                return reset > DateTime.Now ? reset : null;
            }
        }
        return null;
    }

    static DateTime? NextWeeklyReset()
    {
        if (WeeklyAnchor is not { } anchor) return null;
        var now = DateTime.Now;
        while (anchor < now) anchor = anchor.AddDays(7);
        return anchor;
    }

    static int Pct(JsonElement sample, string key) =>
        sample.TryGetProperty("u", out var u) && u.TryGetProperty(key, out var v) &&
        v.TryGetInt32(out var pct) ? pct : 0;

    static DateTime SampleTime(JsonElement sample) =>
        DateTimeOffset.FromUnixTimeMilliseconds(sample.GetProperty("t").GetInt64()).LocalDateTime;

    /// The desktop app rewrites this file while we may be reading it, so share
    /// aggressively and retry rather than failing a refresh over a transient lock.
    static string? ReadWithRetry(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            // Missing entirely is a permanent condition; a lock is transient.
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (IOException) { Thread.Sleep(120); }
        }
        return null;
    }
}
