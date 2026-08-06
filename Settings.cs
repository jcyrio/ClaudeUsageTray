using System.IO;
using System.Text.Json;

namespace ClaudeUsageTray;

/// <summary>
/// Optional user settings, read from %APPDATA%\ClaudeUsageTray\settings.json:
///
///   { "weeklyAnchor": "2026-08-11T01:00:00" }
///
/// Only the weekly reset anchor lives here. It cannot be derived from the usage
/// history and differs per account, so it has to be supplied once by the user.
/// </summary>
public static class Settings
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeUsageTray", "settings.json");

    public static DateTime? LoadWeeklyAnchor()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(Path));
            if (doc.RootElement.TryGetProperty("weeklyAnchor", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(value.GetString(), out var anchor))
                return anchor;
        }
        catch (Exception)
        {
            // A malformed settings file should degrade to "unknown", not crash the app.
        }
        return null;
    }
}
