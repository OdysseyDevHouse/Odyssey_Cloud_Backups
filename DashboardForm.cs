using System.Diagnostics;

namespace MariaDBBackupTray;

/// <summary>
/// Main window: backup status at a glance (last run, next scheduled run),
/// full run history, and a log viewer. Opens after setup and whenever the
/// tray icon is double-clicked; closing it leaves the tray app running.
/// </summary>
public class DashboardForm : Form
{
    private AppConfig _cfg;

    private readonly Label _valLast = StatValue();
    private readonly Label _valNext = StatValue();
    private readonly Label _valDbs = StatValue();
    private readonly Label _valKeep = StatValue();

    private readonly Button _btnRun = new()
        { Text = "Run backup now", Size = new Size(140, 34) };
    private readonly Button _btnSettings = new()
        { Text = "Settings...", Size = new Size(110, 34) };
    private readonly Button _btnFolder = new()
        { Text = "Open backup folder", Size = new Size(160, 34) };
    private readonly Button _btnRefresh = new()
        { Text = "Refresh", Size = new Size(90, 34) };
    private readonly Button _btnReset = new()
        { Text = "Reset settings...", Size = new Size(130, 34) };

    private readonly ListView _history = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 9F),
        BackColor = Color.White,
    };
    private readonly System.Windows.Forms.Timer _timer = new()
        { Interval = 15_000 };

    public DashboardForm(AppConfig cfg)
    {
        _cfg = cfg;

        Text = "Odyssey Cloud Backups - Dashboard";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(800, 600);
        MinimumSize = new Size(720, 500);
        Font = new Font("Segoe UI", 9F);
        BackColor = Theme.PageBg;
        Icon = Theme.AppIcon();
        ShowIcon = Icon != null;

        var brand = Theme.BrandHeader();

        // --- Status strip ---------------------------------------------------
        var stats = new Panel { Dock = DockStyle.Top, Height = 66 };
        stats.Controls.Add(StatBlock("LAST BACKUP", _valLast, 16, 230));
        stats.Controls.Add(StatBlock("NEXT SCHEDULED", _valNext, 256, 230));
        stats.Controls.Add(StatBlock("DATABASES", _valDbs, 496, 110));
        stats.Controls.Add(StatBlock("KEEP LOCALLY", _valKeep, 616, 160));

        // --- Action buttons -------------------------------------------------
        var actions = new Panel { Dock = DockStyle.Top, Height = 48 };
        _btnRun.Location = new Point(16, 6);
        _btnSettings.Location = new Point(166, 6);
        _btnFolder.Location = new Point(286, 6);
        _btnRefresh.Location = new Point(456, 6);
        actions.Controls.AddRange(new Control[]
            { _btnRun, _btnSettings, _btnFolder, _btnRefresh, _btnReset });
        // Keep the reset button pinned to the panel's right edge; position
        // in Resize because the panel has no real width until layout runs.
        actions.Resize += (_, _) =>
        {
            _btnReset.Left = actions.ClientSize.Width - _btnReset.Width - 16;
            _btnReset.Top = 6;
        };

        // --- Tabs: history + logs -------------------------------------------
        _history.Columns.Add("Date", 135);
        _history.Columns.Add("Result", 65);
        _history.Columns.Add("Trigger", 80);
        _history.Columns.Add("Databases", 160);
        _history.Columns.Add("Size", 75);
        _history.Columns.Add("Duration", 65);
        _history.Columns.Add("Archive / error", 260);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var tabHistory = new TabPage("Backup history")
            { BackColor = Color.White, Padding = new Padding(4) };
        tabHistory.Controls.Add(_history);
        var tabLogs = new TabPage("Logs")
            { BackColor = Color.White, Padding = new Padding(4) };
        tabLogs.Controls.Add(_log);
        tabs.TabPages.Add(tabHistory);
        tabs.TabPages.Add(tabLogs);

        var body = new Panel
            { Dock = DockStyle.Fill, Padding = new Padding(16, 4, 16, 16) };
        body.Controls.Add(tabs);

        Controls.Add(body);
        Controls.Add(actions);
        Controls.Add(stats);
        Controls.Add(brand);

        Theme.Apply(this);
        Theme.StylePrimary(_btnRun);
        _btnReset.ForeColor = Theme.Danger;

        _btnRun.Click += (_, _) => RunNow();
        _btnReset.Click += (_, _) => ResetSettings();
        _btnSettings.Click += (_, _) => OpenSettings();
        _btnFolder.Click += (_, _) => Process.Start(new ProcessStartInfo
            { FileName = _cfg.ResolvedOutputDir, UseShellExecute = true });
        _btnRefresh.Click += (_, _) => RefreshData();
        _timer.Tick += (_, _) => RefreshData();
        FormClosed += (_, _) => _timer.Stop();

        RefreshData();
        _timer.Start();
    }

    // ------------------------------------------------------------- actions --

    private void RunNow()
    {
        if (BackupEngine.IsRunning)
        {
            MessageBox.Show(this, "A backup is already running.",
                "Odyssey Cloud Backups", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        _btnRun.Enabled = false;
        _btnRun.Text = "Backing up...";
        Task.Run(() =>
        {
            var result = BackupEngine.Run(AppConfig.Load(), "Manual");
            try
            {
                if (IsDisposed) return;
                BeginInvoke(() =>
                {
                    _btnRun.Enabled = true;
                    _btnRun.Text = "Run backup now";
                    RefreshData();
                    // Success and warnings are visible in the stats/history
                    // and the log; only a full failure interrupts the user.
                    if (!result.Success)
                        MessageBox.Show(this, result.Summary, "Backup failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
            }
            catch
            {
                // Dashboard was closed while the backup ran; nothing to do.
            }
        });
    }

    private void ResetSettings()
    {
        if (BackupEngine.IsRunning)
        {
            MessageBox.Show(this,
                "A backup is currently running. Wait for it to finish " +
                "before resetting.", "Reset settings",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            "This will erase ALL settings for this machine:\r\n\r\n" +
            "  - Site (ODY) verification\r\n" +
            "  - Database connection and selected databases\r\n" +
            "  - Schedule and the Windows scheduled task\r\n" +
            "  - Email recipients\r\n" +
            "  - Backup history list\r\n\r\n" +
            "Backups will NOT run again until setup is completed.\r\n" +
            "Existing backup archives are kept.\r\n\r\n" +
            "Reset all settings and run setup again?",
            "Reset settings", MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        try
        {
            SchedulerService.Unregister();
            SchedulerService.SetTrayAutostart(false);
            if (File.Exists(AppConfig.ConfigPath))
                File.Delete(AppConfig.ConfigPath);
            if (File.Exists(BackupHistory.FilePath))
                File.Delete(BackupHistory.FilePath);
            Log.Info("Settings reset by user; setup required on next start.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Reset failed: {ex.Message}",
                "Reset settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        MessageBox.Show(this,
            "Settings have been reset. The application will now restart " +
            "and run the setup wizard.", "Reset settings",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        Process.Start(new ProcessStartInfo
            { FileName = SchedulerService.ExePath, UseShellExecute = true });
        Application.Exit();
    }

    private void OpenSettings()
    {
        using var wizard = new SetupWizardForm(AppConfig.Load());
        if (wizard.ShowDialog(this) == DialogResult.OK)
        {
            _cfg = AppConfig.Load();
            Log.Info("Settings updated.");
        }
        RefreshData();
    }

    // -------------------------------------------------------------- data ----

    private void RefreshData()
    {
        _cfg = AppConfig.Load();
        var history = BackupHistory.Load();

        var last = history.FirstOrDefault();
        if (last != null)
        {
            var status = StatusOf(last);
            _valLast.Text = $"{last.Started:yyyy-MM-dd HH:mm}  " +
                            status.ToUpperInvariant();
            _valLast.ForeColor = StatusColor(status);
        }
        else
        {
            _valLast.Text = "Never";
            _valLast.ForeColor = Theme.TextMuted;
        }

        var next = NextRun(_cfg.Schedule);
        _valNext.Text = next?.ToString("ddd dd MMM, HH:mm") ?? "-";
        _valDbs.Text = _cfg.Databases.Count.ToString();
        _valKeep.Text = $"{_cfg.Backup.KeepLast} backups";

        _history.BeginUpdate();
        _history.Items.Clear();
        foreach (var e in history)
        {
            var status = StatusOf(e);
            var item = new ListViewItem(e.Started.ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(status);
            item.SubItems.Add(e.Trigger);
            item.SubItems.Add(string.Join(", ", e.Databases));
            item.SubItems.Add(e.Success ? BackupEngine.HumanSize(e.SizeBytes) : "-");
            item.SubItems.Add($"{e.DurationSeconds:F0}s");
            item.SubItems.Add(!e.Success ? e.Error
                : e.Error != "" ? $"{Path.GetFileName(e.Archive)} — {e.Error}"
                : Path.GetFileName(e.Archive));
            if (status != "Success") item.ForeColor = StatusColor(status);
            _history.Items.Add(item);
        }
        _history.EndUpdate();

        RefreshLog();
    }

    private void RefreshLog()
    {
        try
        {
            string text;
            if (File.Exists(AppConfig.LogPath))
            {
                // Share ReadWrite so reading works while a backup is logging.
                using var fs = new FileStream(AppConfig.LogPath, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                text = sr.ReadToEnd();
            }
            else
            {
                text = "(no log entries yet)";
            }

            if (_log.Text != text)
            {
                _log.Text = text;
                _log.SelectionStart = _log.TextLength;
                _log.ScrollToCaret();
            }
        }
        catch
        {
            // Log being rotated; try again on the next refresh.
        }
    }

    private static string StatusOf(BackupHistoryEntry e) =>
        e.Status != "" ? e.Status : e.Success ? "Success" : "Failed";

    private static Color StatusColor(string status) => status switch
    {
        "Success" => Theme.Success,
        "Warning" => Theme.Warning,
        _ => Theme.Danger,
    };

    /// <summary>Next scheduled run derived from the configured day + time.</summary>
    private static DateTime? NextRun(AppConfig.ScheduleSettings s)
    {
        var parts = s.Time.Split(':');
        int.TryParse(parts.ElementAtOrDefault(0), out var hh);
        int.TryParse(parts.ElementAtOrDefault(1), out var mm);
        var now = DateTime.Now;
        var candidate = now.Date.AddHours(hh).AddMinutes(mm);

        if (s.Day == "Every day")
            return candidate > now ? candidate : candidate.AddDays(1);

        if (!Enum.TryParse<DayOfWeek>(s.Day, out var dow)) return null;
        candidate = candidate.AddDays(((int)dow - (int)now.DayOfWeek + 7) % 7);
        return candidate > now ? candidate : candidate.AddDays(7);
    }

    // ----------------------------------------------------------- UI bits ----

    private static Label StatValue() => new()
    {
        Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
        ForeColor = Theme.Navy,
        AutoSize = true,
        Location = new Point(0, 20),
    };

    private static Panel StatBlock(string title, Label value, int x, int width)
    {
        var p = new Panel
            { Location = new Point(x, 10), Size = new Size(width, 48) };
        p.Controls.Add(new Label
        {
            Text = title,
            Font = new Font("Segoe UI", 7.5F, FontStyle.Bold),
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Location = new Point(0, 2),
        });
        p.Controls.Add(value);
        return p;
    }
}
