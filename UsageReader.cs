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
    const string FileName = "plan-usage-history.json";

    /// <summary>
    /// Where the desktop app keeps its usage history. There are two candidates, because
    /// the Store build of Claude is an MSIX package. Inside that package's container
    /// %APPDATA% resolves normally, but for any process outside it -- which is what this
    /// app is -- those writes are redirected into the package's LocalCache. Checking only
    /// %APPDATA% makes the app work when launched from inside Claude and report "no data"
    /// everywhere else, which is exactly backwards from what users hit.
    /// </summary>
    public static IEnumerable<string> CandidatePaths()
    {
        // Plain installs, and the view from inside the MSIX container.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude", FileName);

        // Store install, seen from outside the container. The publisher id can change, so
        // match on the package name rather than hardcoding Claude_pzs8sxrjxfjjc.
        var packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");

        string[] claudePackages;
        try { claudePackages = Directory.GetDirectories(packages, "Claude_*"); }
        catch (Exception) { yield break; }

        foreach (var dir in claudePackages)
            yield return Path.Combine(dir, "LocalCache", "Roaming", "Claude", FileName);
    }

    /// <summary>The most recently written candidate, or null if the app is not installed.</summary>
    public static string? ResolvePath()
    {
        string? best = null;
        var bestWrite = DateTime.MinValue;
        foreach (var candidate in CandidatePaths())
        {
            try
            {
                var info = new FileInfo(candidate);
                if (info.Exists && info.LastWriteTimeUtc > bestWrite)
                {
                    best = candidate;
                    bestWrite = info.LastWriteTimeUtc;
                }
            }
            catch (Exception) { /* unreadable candidate is simply not a candidate */ }
        }
        return best;
    }

    /// <summary>
    /// Weekly windows are fixed-anchor, and they reset overnight -- inside the gap
    /// where the desktop app is closed and not sampling. That makes the weekly reset
    /// impossible to derive from history, and it differs per account, so it cannot be
    /// a build-time constant either. The user seeds it once from /usage; until then
    /// the weekly reset time is reported as unknown rather than guessed.
    /// </summary>
    public static DateTime? WeeklyAnchor { get; set; } = Settings.LoadWeeklyAnchor();

    /// <summary>
    /// The desktop app rewrites this file whole every few minutes, and our file watcher
    /// fires while that is happening -- so a read can catch it truncated or half-written.
    /// That surfaces as a parse failure, not an IO error, so both have to be retried.
    /// </summary>
    public static UsageSnapshot? Read(string? path = null)
    {
        path ??= ResolvePath();
        if (path is null) return null;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var json = ReadOnce(path);
            if (json is not null && Parse(json) is { } snapshot) return snapshot;
            Thread.Sleep(150);
        }
        return null;
    }

    static UsageSnapshot? Parse(string json)
    {
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

    /// Share aggressively -- the desktop app holds this file open while rewriting it.
    static string? ReadOnce(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        // FileNotFound and DirectoryNotFound both derive from IOException.
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
