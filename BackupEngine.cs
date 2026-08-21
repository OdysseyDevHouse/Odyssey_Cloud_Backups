using System.Diagnostics;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;

namespace MariaDBBackupTray;

public class BackupResult
{
    public bool Success { get; init; }
    /// <summary>True when the archive was created but some databases failed.</summary>
    public bool Warning { get; init; }
    public string Summary { get; init; } = "";
}

/// <summary>
/// The site's Cloud Backups module is disabled or expired; the dedicated
/// module-alert email has already been sent when this is thrown.
/// </summary>
public class CloudBackupModuleException : InvalidOperationException
{
    public CloudBackupModuleException(string message) : base(message) { }
}

/// <summary>Outcome of dumping one database.</summary>
public class DbDumpResult
{
    public string Database { get; init; } = "";
    public bool Success { get; init; }
    public string Error { get; init; } = "";
}

/// <summary>Everything the notification email needs about one backup run.</summary>
public class BackupReport
{
    /// <summary>"SUCCESS" | "WARNING" | "FAILED"</summary>
    public string Status { get; init; } = "SUCCESS";
    public DateTime Started { get; init; }
    public double DurationSeconds { get; init; }
    public string Trigger { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string Server { get; init; } = "";
    public string OdyKey { get; init; } = "";
    public string SiteName { get; init; } = "";
    public List<DbDumpResult> Databases { get; init; } = new();
    public string Archive { get; init; } = "";
    public long SizeBytes { get; init; }
    public string GeneralError { get; init; } = "";
}

public static class BackupEngine
{
    /// <summary>
    /// Common install locations for mariadb-dump.exe / mysqldump.exe.
    /// A '*' segment matches any directory name (e.g. versioned folders).
    /// </summary>
    private static readonly string[] SearchPatterns =
    {
        @"C:\Program Files\MariaDB*\bin\mariadb-dump.exe",
        @"C:\Program Files\MariaDB*\bin\mysqldump.exe",
        @"C:\Program Files (x86)\MariaDB*\bin\mariadb-dump.exe",
        @"C:\Program Files (x86)\MariaDB*\bin\mysqldump.exe",
        @"C:\Program Files\MySQL\MySQL Server*\bin\mysqldump.exe",
        @"C:\xampp\mysql\bin\mysqldump.exe",
        @"C:\wamp64\bin\mysql\mysql*\bin\mysqldump.exe",
        @"C:\laragon\bin\mysql\*\bin\mysqldump.exe",
    };

    /// <summary>Find a dump executable; returns "" if none found.</summary>
    public static string FindDumpExe(string userPath = "")
    {
        if (!string.IsNullOrWhiteSpace(userPath) && File.Exists(userPath))
            return userPath;

        foreach (var pattern in SearchPatterns)
        {
            foreach (var match in ExpandPattern(pattern)
                         .OrderByDescending(p => p)) // newest version first
                return match;
        }
        return "";
    }

    /// <summary>Expand a path pattern where directory segments may contain '*'.</summary>
    private static IEnumerable<string> ExpandPattern(string pattern)
    {
        var parts = pattern.Split(Path.DirectorySeparatorChar);
        IEnumerable<string> current = new[] { parts[0] + Path.DirectorySeparatorChar };

        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            var isLast = i == parts.Length - 1;
            var next = new List<string>();
            foreach (var baseDir in current)
            {
                if (!Directory.Exists(baseDir)) continue;
                try
                {
                    if (isLast)
                        next.AddRange(Directory.EnumerateFiles(baseDir, part));
                    else if (part.Contains('*'))
                        next.AddRange(Directory.EnumerateDirectories(baseDir, part));
                    else
                        next.Add(Path.Combine(baseDir, part));
                }
                catch
                {
                    // Access denied etc. - skip.
                }
            }
            current = next;
        }
        return current.Where(p => File.Exists(p) || Directory.Exists(p));
    }

    // ------------------------------------------------------------ backup --

    private static int _running;

    /// <summary>True while a backup is in progress (any trigger).</summary>
    public static bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// Run a full backup: dump each database, zip (AES-256 if password set),
    /// apply retention, email the outcome. Never throws. Concurrent calls
    /// (e.g. manual run while the scheduled task fires) are rejected.
    /// </summary>
    public static BackupResult Run(AppConfig cfg, string reason)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 1)
        {
            Log.Warn($"{reason} backup requested while another backup is " +
                     "running; skipped.");
            return new BackupResult
                { Success = false, Summary = "A backup is already running." };
        }
        try
        {
            return RunCore(cfg, reason);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static BackupResult RunCore(AppConfig cfg, string reason)
    {
        var started = DateTime.Now;
        var stamp = started.ToString("yyyyMMdd_HHmmss");
        var outDir = cfg.ResolvedOutputDir;
        var zipPath = Path.Combine(outDir, $"backup_{stamp}.zip");
        var host = Environment.MachineName;

        Log.Info($"=== {reason} backup started ({cfg.Databases.Count} database(s)) ===");
        var dbResults = new List<DbDumpResult>();
        string? tempDir = null;
        try
        {
            VerifyCloudBackupRegistration(cfg);

            if (cfg.Databases.Count == 0)
                throw new InvalidOperationException("No databases selected for backup.");

            var dumpExe = FindDumpExe(cfg.Backup.DumpExePath);
            if (string.IsNullOrEmpty(dumpExe))
                throw new InvalidOperationException(
                    "Could not find mariadb-dump.exe / mysqldump.exe. " +
                    "Set the path manually in Settings.");
            Log.Info($"Using dump tool: {dumpExe}");

            tempDir = Path.Combine(Path.GetTempPath(), "mbtray_" + stamp);
            Directory.CreateDirectory(tempDir);

            // Credentials via a defaults file so the password never appears
            // on the command line / in the process list.
            var cnfPath = Path.Combine(tempDir, "auth.cnf");
            File.WriteAllText(cnfPath,
                "[client]\n" +
                $"host={cfg.Db.Host}\n" +
                $"port={cfg.Db.Port}\n" +
                $"user={cfg.Db.User}\n" +
                $"password=\"{cfg.Db.Password}\"\n");

            var sqlFiles = new List<string>();
            foreach (var db in cfg.Databases)
            {
                var outFile = Path.Combine(tempDir, $"{db}_{stamp}.sql");
                Log.Info($"Dumping {db} ...");
                try
                {
                    DumpDatabase(dumpExe, cnfPath, db, outFile);
                    sqlFiles.Add(outFile);
                    dbResults.Add(new DbDumpResult
                        { Database = db, Success = true });
                }
                catch (Exception dumpEx)
                {
                    // One bad database (e.g. deleted on the server) must not
                    // stop the remaining databases from being backed up.
                    dbResults.Add(new DbDumpResult
                    {
                        Database = db,
                        Success = false,
                        Error = dumpEx.Message,
                    });
                    Log.Error($"Dump of {db} failed: {dumpEx.Message}");
                }
            }
            if (sqlFiles.Count == 0)
                throw new InvalidOperationException(
                    "All database dumps failed; nothing to archive.");

            Log.Info($"Compressing {sqlFiles.Count} dump(s) ...");
            ZipDumps(sqlFiles, zipPath, cfg.Backup.ZipPassword);

            var sizeBytes = new FileInfo(zipPath).Length;
            ApplyRetention(outDir, cfg.Backup.KeepLast);

            var failCount = dbResults.Count(r => !r.Success);
            var elapsed = (DateTime.Now - started).TotalSeconds;
            var report = new BackupReport
            {
                Status = failCount > 0 ? "WARNING" : "SUCCESS",
                Started = started,
                DurationSeconds = elapsed,
                Trigger = reason,
                MachineName = host,
                Server = $"{cfg.Db.Host}:{cfg.Db.Port}",
                OdyKey = cfg.Site.OdyKey,
                SiteName = cfg.Site.SiteName,
                Databases = dbResults,
                Archive = zipPath,
                SizeBytes = sizeBytes,
            };
            var summary = BuildSummary(report);

            BackupHistory.Append(new BackupHistoryEntry
            {
                Started = started,
                DurationSeconds = elapsed,
                Success = true,
                Status = failCount > 0 ? "Warning" : "Success",
                Trigger = reason,
                Archive = zipPath,
                SizeBytes = sizeBytes,
                Error = string.Join("; ", dbResults.Where(r => !r.Success)
                    .Select(r => $"{r.Database}: {r.Error}")),
                Databases = cfg.Databases.ToList(),
            });

            if (failCount > 0)
                Log.Warn($"Backup completed with warnings: {failCount} of " +
                         $"{dbResults.Count} database dump(s) failed.");
            Log.Info($"Backup OK: {zipPath} ({HumanSize(sizeBytes)})");

            try
            {
                EmailService.SendReport(cfg, report);
            }
            catch (Exception mailEx)
            {
                Log.Error($"Backup succeeded but email failed: {mailEx.Message}");
                summary += $"\r\n\r\nWARNING: notification email failed: {mailEx.Message}";
            }
            return new BackupResult
                { Success = true, Warning = failCount > 0, Summary = summary };
        }
        catch (Exception ex)
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }

            var elapsed = (DateTime.Now - started).TotalSeconds;
            var report = new BackupReport
            {
                Status = "FAILED",
                Started = started,
                DurationSeconds = elapsed,
                Trigger = reason,
                MachineName = host,
                Server = $"{cfg.Db.Host}:{cfg.Db.Port}",
                OdyKey = cfg.Site.OdyKey,
                SiteName = cfg.Site.SiteName,
                Databases = dbResults,
                GeneralError = ex.Message,
            };
            var summary = BuildSummary(report);

            BackupHistory.Append(new BackupHistoryEntry
            {
                Started = started,
                DurationSeconds = elapsed,
                Success = false,
                Status = "Failed",
                Trigger = reason,
                Error = ex.Message,
                Databases = cfg.Databases.ToList(),
            });
            Log.Error($"Backup FAILED: {ex.Message}");

            try
            {
                // The module alert path already emailed the recipients.
                if (ex is not CloudBackupModuleException)
                    EmailService.SendReport(cfg, report);
            }
            catch (Exception mailEx)
            {
                Log.Error($"Failure email could not be sent: {mailEx.Message}");
            }
            return new BackupResult { Success = false, Summary = summary };
        }
        finally
        {
            try { if (tempDir != null) Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Re-check the site's Cloud Backups registration against the Odyssey
    /// Control Panel webservice before every backup. Throws (aborting the
    /// backup) when the service explicitly reports the site as unknown or
    /// not registered. If the service cannot be reached, the backup goes
    /// ahead anyway — a network outage must not stop local backups.
    /// </summary>
    private static void VerifyCloudBackupRegistration(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Site.OdyKey))
        {
            Log.Warn("No ODY key configured; skipping registration check.");
            return;
        }

        Log.Info($"Verifying Cloud Backups registration for {cfg.Site.OdyKey} ...");
        var details = OdysseyService.VerifyOdyKeyAsync(cfg.Site.OdyKey)
            .GetAwaiter().GetResult();

        if (!details.IsSuccess)
        {
            Log.Warn("Could not verify Cloud Backups registration (" +
                     $"{details.ResponseMessage}); continuing with the backup.");
            return;
        }

        var found = details.OdySiteId != "" || details.SiteName != "";
        if (!found)
            throw new InvalidOperationException(
                $"ODY key '{cfg.Site.OdyKey}' is not known to Odyssey. " +
                "Backup aborted - re-run Settings to verify the site.");

        var (allowed, reason) = OdysseyService.CheckEligibility(details);
        if (!allowed)
        {
            // Tell the recipients their module must be (re-)enabled, then
            // abort. CloudBackupModuleException marks that this dedicated
            // alert was already sent, so no second failure email goes out.
            try
            {
                EmailService.SendModuleAlert(cfg, reason);
            }
            catch (Exception mailEx)
            {
                Log.Error($"Module alert email could not be sent: {mailEx.Message}");
            }
            throw new CloudBackupModuleException(
                $"Cloud backups cannot run: {reason} Contact Odyssey to " +
                "enable the Cloud Backups module for this site.");
        }

        Log.Info($"Registration confirmed: {details.SiteName} ({cfg.Site.OdyKey}).");
    }

    /// <summary>
    /// Plain-text version of the report, used for popups and as the email's
    /// text-only fallback.
    /// </summary>
    public static string BuildSummary(BackupReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Status switch
        {
            "SUCCESS" => "Backup completed successfully.",
            "WARNING" => "Backup completed with warnings.",
            _ => "Backup FAILED.",
        });
        sb.AppendLine();
        if (r.OdyKey != "")
            sb.AppendLine($"Site:       {r.OdyKey} - {r.SiteName}");
        sb.AppendLine($"Server:     {r.Server} ({r.MachineName})");
        foreach (var d in r.Databases)
            sb.AppendLine($"  {(d.Success ? "[OK]     " : "[FAILED] ")}{d.Database}" +
                          (d.Success ? "" : $" - {d.Error}"));
        if (r.Archive != "")
        {
            sb.AppendLine($"Archive:    {r.Archive}");
            sb.AppendLine($"Size:       {HumanSize(r.SizeBytes)}");
        }
        sb.AppendLine($"Started:    {r.Started:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Duration:   {r.DurationSeconds:F0} seconds");
        sb.AppendLine($"Trigger:    {r.Trigger}");
        if (r.GeneralError != "")
        {
            sb.AppendLine();
            sb.AppendLine($"Error: {r.GeneralError}");
        }
        return sb.ToString().TrimEnd();
    }

    private static void DumpDatabase(string dumpExe, string cnfPath,
                                     string database, string outFile)
    {
        var psi = new ProcessStartInfo
        {
            FileName = dumpExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        // --defaults-extra-file MUST be the first argument.
        psi.ArgumentList.Add($"--defaults-extra-file={cnfPath}");
        psi.ArgumentList.Add("--single-transaction");
        psi.ArgumentList.Add("--routines");
        psi.ArgumentList.Add("--events");
        psi.ArgumentList.Add("--triggers");
        psi.ArgumentList.Add("--databases");
        psi.ArgumentList.Add(database);
        psi.ArgumentList.Add($"--result-file={outFile}");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dump process.");
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"Dump of '{database}' failed (exit {proc.ExitCode}): {stderr.Trim()}");
        if (!File.Exists(outFile) || new FileInfo(outFile).Length == 0)
            throw new InvalidOperationException(
                $"Dump of '{database}' produced an empty file.");
    }

    private static void ZipDumps(List<string> sqlFiles, string zipPath,
                                 string password)
    {
        using var zipStream = new ZipOutputStream(File.Create(zipPath));
        zipStream.SetLevel(6);
        var hasPassword = !string.IsNullOrEmpty(password);
        if (hasPassword) zipStream.Password = password;

        foreach (var file in sqlFiles)
        {
            var entry = new ZipEntry(Path.GetFileName(file))
            {
                DateTime = DateTime.Now,
                AESKeySize = hasPassword ? 256 : 0, // AES-256 when protected
            };
            zipStream.PutNextEntry(entry);
            using var fs = File.OpenRead(file);
            fs.CopyTo(zipStream);
            zipStream.CloseEntry();
        }
        zipStream.Finish();
    }

    private static void ApplyRetention(string outDir, int keepLast)
    {
        if (keepLast <= 0) return;
        var zips = Directory.EnumerateFiles(outDir, "backup_*.zip")
            .OrderBy(p => p)
            .ToList();
        foreach (var old in zips.Take(Math.Max(0, zips.Count - keepLast)))
        {
            try
            {
                File.Delete(old);
                Log.Info($"Retention: removed old backup {old}");
            }
            catch (Exception ex)
            {
                Log.Warn($"Retention: could not remove {old}: {ex.Message}");
            }
        }
    }

    public static string HumanSize(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        foreach (var unit in units)
        {
            if (bytes < 1024) return $"{bytes:F1} {unit}";
            bytes /= 1024;
        }
        return $"{bytes:F1} PB";
    }
}
