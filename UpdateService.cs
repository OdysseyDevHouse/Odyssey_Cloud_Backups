using System.Diagnostics;
using System.Text.Json;

namespace MariaDBBackupTray;

/// <summary>
/// Auto-update via GitHub Releases.
///
/// How it works:
///  - In the background (startup + every 12h) the app asks GitHub for the
///    latest release of <see cref="GitHubRepo"/>. If its tag (vX.Y.Z) is
///    newer than this exe's file version, the release's exe asset is
///    downloaded to %APPDATA%\MariaDBBackupTray\update\pending.exe.
///  - On the next interactive start, the pending exe replaces the running
///    one (rename self to .old, move pending into place, relaunch).
///
/// Publishing a release:
///  1. Bump &lt;Version&gt; in the csproj (e.g. 1.0.1) and publish the exe.
///  2. Create a GitHub release tagged "v1.0.1" and attach the exe as an
///     asset (GitHub turns spaces in file names into dots - that's fine,
///     any asset ending in .exe that doesn't contain "Setup" is used).
/// The releases repo must be public (the API is called unauthenticated).
/// </summary>
public static class UpdateService
{
    /// <summary>
    /// "owner/repo" on GitHub that hosts the releases, e.g.
    /// "odyssey-software/cloud-backups". Leave empty to disable auto-update.
    /// </summary>
    public const string GitHubRepo = "OdysseyDevHouse/Odyssey_Cloud_Backups";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("OdysseyCloudBackups-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    private static string UpdateDir
    {
        get
        {
            var d = Path.Combine(AppConfig.AppDir, "update");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string PendingExePath => Path.Combine(UpdateDir, "pending.exe");

    public static Version CurrentVersion
    {
        get
        {
            try
            {
                var v = FileVersionInfo
                    .GetVersionInfo(Environment.ProcessPath!).FileVersion;
                if (Version.TryParse(v, out var parsed)) return parsed;
            }
            catch { /* fall through */ }
            return new Version(0, 0, 0, 0);
        }
    }

    // ------------------------------------------- apply (on startup) -------

    /// <summary>
    /// If an update was downloaded earlier, swap it in and relaunch.
    /// Returns true when the caller must exit immediately (new exe started).
    /// </summary>
    public static bool ApplyPendingUpdate()
    {
        try
        {
            CleanupOldExe();
            if (!File.Exists(PendingExePath)) return false;

            var pendingVer = GetFileVersion(PendingExePath);
            if (pendingVer == null || pendingVer <= CurrentVersion)
            {
                File.Delete(PendingExePath); // stale or invalid download
                return false;
            }

            var exe = Environment.ProcessPath!;
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old); // renaming a running exe is allowed
            try
            {
                File.Move(PendingExePath, exe);
            }
            catch
            {
                File.Move(old, exe); // roll back so the app still runs
                throw;
            }

            Log.Info($"Auto-update applied: {pendingVer}. Restarting.");
            Process.Start(new ProcessStartInfo
                { FileName = exe, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not apply pending update: {ex.Message}");
            return false;
        }
    }

    private static void CleanupOldExe()
    {
        try
        {
            var old = Environment.ProcessPath + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch
        {
            // Previous instance may still be exiting; cleaned next start.
        }
    }

    private static Version? GetFileVersion(string path)
    {
        try
        {
            return Version.TryParse(
                FileVersionInfo.GetVersionInfo(path).FileVersion, out var v)
                ? v : null;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------- check + download (background) --

    /// <summary>Check GitHub for a newer release and stage it. Never throws.</summary>
    public static async Task CheckAndDownloadAsync()
    {
        try
        {
            if (GitHubRepo.Length == 0)
            {
                Log.Info("Auto-update disabled (no GitHub repository configured).");
                return;
            }

            var json = await Http.GetStringAsync(
                $"https://api.github.com/repos/{GitHubRepo}/releases/latest");
            using var doc = JsonDocument.Parse(json);

            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var tagVer = ParseTag(tag);
            if (tagVer == null || tagVer <= CurrentVersion) return;

            if (File.Exists(PendingExePath))
            {
                var staged = GetFileVersion(PendingExePath);
                if (staged != null && staged >= tagVer) return; // already staged
            }

            string? url = null;
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (url == null)
            {
                Log.Warn($"Auto-update: release {tag} has no exe asset.");
                return;
            }

            Log.Info($"Auto-update: downloading {tag} ...");
            var tmp = PendingExePath + ".tmp";
            await using (var stream = await Http.GetStreamAsync(url))
            await using (var fs = File.Create(tmp))
                await stream.CopyToAsync(fs);

            var newVer = GetFileVersion(tmp);
            if (newVer == null || newVer <= CurrentVersion)
            {
                File.Delete(tmp);
                Log.Warn($"Auto-update: downloaded {tag} rejected " +
                         "(file version not newer).");
                return;
            }

            if (File.Exists(PendingExePath)) File.Delete(PendingExePath);
            File.Move(tmp, PendingExePath);
            Log.Info($"Auto-update: {tag} downloaded; " +
                     "it will be installed on the next start.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Auto-update check failed: {ex.Message}");
        }
    }

    private static Version? ParseTag(string tag)
    {
        tag = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(tag, out var v) ? v : null;
    }
}
