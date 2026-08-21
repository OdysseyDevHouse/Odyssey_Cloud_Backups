using System.Text.Json;

namespace MariaDBBackupTray;

/// <summary>One recorded backup run (success or failure).</summary>
public class BackupHistoryEntry
{
    public DateTime Started { get; set; }
    public double DurationSeconds { get; set; }
    public bool Success { get; set; }
    /// <summary>"Success" | "Warning" | "Failed"; empty on legacy entries.</summary>
    public string Status { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Archive { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Error { get; set; } = "";
    public List<string> Databases { get; set; } = new();
}

/// <summary>
/// Append-only run history (one JSON object per line) in
/// %APPDATA%\MariaDBBackupTray\history.jsonl, capped to the newest 200
/// entries. This is what the dashboard's history list is built from.
/// </summary>
public static class BackupHistory
{
    private const int MaxEntries = 200;
    private static readonly object Sync = new();

    public static string FilePath =>
        Path.Combine(AppConfig.AppDir, "history.jsonl");

    public static void Append(BackupHistoryEntry entry)
    {
        lock (Sync)
        {
            try
            {
                var lines = File.Exists(FilePath)
                    ? File.ReadAllLines(FilePath).ToList()
                    : new List<string>();
                lines.Add(JsonSerializer.Serialize(entry));
                if (lines.Count > MaxEntries)
                    lines.RemoveRange(0, lines.Count - MaxEntries);
                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not record backup history: {ex.Message}");
            }
        }
    }

    /// <summary>Load all entries, newest first. Never throws.</summary>
    public static List<BackupHistoryEntry> Load()
    {
        lock (Sync)
        {
            var list = new List<BackupHistoryEntry>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                foreach (var line in File.ReadLines(FilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var e = JsonSerializer
                            .Deserialize<BackupHistoryEntry>(line);
                        if (e != null) list.Add(e);
                    }
                    catch
                    {
                        // Skip corrupt lines.
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read backup history: {ex.Message}");
            }
            list.Reverse();
            return list;
        }
    }
}
