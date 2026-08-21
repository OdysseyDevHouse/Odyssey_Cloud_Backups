using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MariaDBBackupTray;

/// <summary>
/// Application configuration. Stored as JSON in
/// %APPDATA%\MariaDBBackupTray\config.json. Password fields are encrypted
/// with Windows DPAPI (per-user) before being written to disk.
/// </summary>
public class AppConfig
{
    public SiteSettings Site { get; set; } = new();
    public DbSettings Db { get; set; } = new();
    public List<string> Databases { get; set; } = new();
    public ScheduleSettings Schedule { get; set; } = new();
    public EmailSettings Email { get; set; } = new();
    public BackupSettings Backup { get; set; } = new();

    public class SiteSettings
    {
        /// <summary>The Odyssey site key, e.g. "ODY9710".</summary>
        public string OdyKey { get; set; } = "";
        public string SiteName { get; set; } = "";
        /// <summary>True once the key passed Cloud Backups verification.</summary>
        public bool CloudBackupVerified { get; set; }
    }

    public class DbSettings
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 3306;
        public string User { get; set; } = "root";
        [JsonConverter(typeof(ProtectedStringConverter))]
        public string Password { get; set; } = "";
    }

    public class ScheduleSettings
    {
        /// <summary>"Every day" or "Monday".."Sunday"</summary>
        public string Day { get; set; } = "Every day";
        /// <summary>24h "HH:mm"</summary>
        public string Time { get; set; } = "22:00";
    }

    public class EmailSettings
    {
        // Only the recipients are configurable per install; the SMTP account
        // used to send is compiled in (EmailDefaults.cs).
        public List<string> ClientEmails { get; set; } = new();
        public List<string> DealerEmails { get; set; } = new();
    }

    public class BackupSettings
    {
        [JsonConverter(typeof(ProtectedStringConverter))]
        public string ZipPassword { get; set; } = "";
        /// <summary>Path to mariadb-dump.exe / mysqldump.exe. Empty = auto-detect.</summary>
        public string DumpExePath { get; set; } = "";
        /// <summary>Empty = %APPDATA%\MariaDBBackupTray\backups</summary>
        public string OutputDir { get; set; } = "";
        /// <summary>
        /// Archives to keep locally. Each archive holds every selected
        /// database, so this equals backups kept per database. Kept small
        /// (3) because backups will also be shipped to AWS S3.
        /// </summary>
        public int KeepLast { get; set; } = 3;
    }

    // ------------------------------------------------------------------ IO

    public static string AppDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MariaDBBackupTray");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string ConfigPath => Path.Combine(AppDir, "config.json");
    public static string LogPath => Path.Combine(AppDir, "backup.log");

    public string ResolvedOutputDir
    {
        get
        {
            var dir = string.IsNullOrWhiteSpace(Backup.OutputDir)
                ? Path.Combine(AppDir, "backups")
                : Backup.OutputDir;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public static bool Exists() => File.Exists(ConfigPath);

    public static AppConfig Load()
    {
        if (!Exists()) return new AppConfig();
        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load config, using defaults: {ex.Message}");
            return new AppConfig();
        }
    }

    public void Save()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
    }
}

/// <summary>
/// JSON converter that transparently DPAPI-encrypts a string on write and
/// decrypts on read, so secrets never sit in plain text in config.json.
/// </summary>
public class ProtectedStringConverter : JsonConverter<string>
{
    private const string Prefix = "dpapi:";

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert,
                                JsonSerializerOptions options)
    {
        var value = reader.GetString() ?? "";
        if (!value.StartsWith(Prefix)) return value; // legacy plain text
        try
        {
            var cipher = Convert.FromBase64String(value[Prefix.Length..]);
            var plain = ProtectedData.Unprotect(cipher, null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return "";
        }
    }

    public override void Write(Utf8JsonWriter writer, string value,
                               JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.WriteStringValue("");
            return;
        }
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null,
            DataProtectionScope.CurrentUser);
        writer.WriteStringValue(Prefix + Convert.ToBase64String(cipher));
    }
}

/// <summary>Tiny append-only file logger with size-based trim.</summary>
public static class Log
{
    private static readonly object Sync = new();

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Warn(string msg) => Write("WARN", msg);

    private static void Write(string level, string msg)
    {
        lock (Sync)
        {
            try
            {
                var path = AppConfig.LogPath;
                // Keep the log under ~2 MB by rotating once.
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                {
                    var old = path + ".1";
                    File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never crash the app.
            }
        }
    }
}
