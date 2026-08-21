using System.Diagnostics;

namespace MariaDBBackupTray;

/// <summary>
/// Registers the backup with Windows Task Scheduler via schtasks.exe, so it
/// runs at the configured time even when the tray app isn't open.
/// The task simply runs this exe with the --backup switch.
/// </summary>
public static class SchedulerService
{
    public const string TaskName = "MariaDBBackupTray";

    private static readonly Dictionary<string, string> DayCodes = new()
    {
        ["Monday"] = "MON", ["Tuesday"] = "TUE", ["Wednesday"] = "WED",
        ["Thursday"] = "THU", ["Friday"] = "FRI", ["Saturday"] = "SAT",
        ["Sunday"] = "SUN",
    };

    public static string ExePath =>
        Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "Odyssey Cloud Backups.exe");

    /// <summary>Create or replace the scheduled task. Throws on failure.</summary>
    public static void Register(AppConfig cfg)
    {
        var day = cfg.Schedule.Day;
        var time = cfg.Schedule.Time; // HH:mm

        var args = new List<string>
        {
            "/Create", "/F",
            "/TN", TaskName,
            "/TR", $"\"{ExePath}\" --backup",
            "/ST", time,
        };
        if (day == "Every day")
        {
            args.AddRange(new[] { "/SC", "DAILY" });
        }
        else
        {
            args.AddRange(new[] { "/SC", "WEEKLY", "/D", DayCodes[day] });
        }

        var output = RunSchtasks(args, out var exitCode);
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"Could not create the scheduled task: {output.Trim()}");
        Log.Info($"Scheduled task registered: {day} at {time}");
    }

    public static void Unregister()
    {
        RunSchtasks(new List<string> { "/Delete", "/F", "/TN", TaskName },
                    out _);
    }

    public static bool IsRegistered()
    {
        RunSchtasks(new List<string> { "/Query", "/TN", TaskName },
                    out var exitCode);
        return exitCode == 0;
    }

    private static string RunSchtasks(List<string> args, out int exitCode)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd() +
                     proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        exitCode = proc.ExitCode;
        return output;
    }

    // ------------------------------------------------- tray autostart -----

    private const string RunKey =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Start the tray app at login (separate from the backup task).</summary>
    public static void SetTrayAutostart(bool enable)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser
            .OpenSubKey(RunKey, writable: true);
        if (key == null) return;
        if (enable)
            key.SetValue(TaskName, $"\"{ExePath}\" --tray");
        else
            key.DeleteValue(TaskName, throwOnMissingValue: false);
    }
}
