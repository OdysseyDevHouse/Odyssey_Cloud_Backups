namespace MariaDBBackupTray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // ------------------------------------------------------------------
        // Headless mode: run by Windows Task Scheduler at the configured
        // time ("MariaDBBackupTray.exe --backup"). No UI, exit code reflects
        // success so Task Scheduler history shows failures.
        // ------------------------------------------------------------------
        if (args.Contains("--backup", StringComparer.OrdinalIgnoreCase))
        {
            if (!AppConfig.Exists())
            {
                Log.Error("--backup requested but no configuration exists.");
                return 2;
            }
            var result = BackupEngine.Run(AppConfig.Load(), "Scheduled");
            return result.Success ? 0 : 1;
        }

        // ------------------------------------------------------------------
        // Interactive mode.
        // ------------------------------------------------------------------
        // Apply a previously downloaded auto-update: swaps the exe and
        // relaunches, in which case this instance exits immediately.
        if (UpdateService.ApplyPendingUpdate()) return 0;

        ApplicationConfiguration.Initialize();

        // Single instance guard for the tray app. Wait briefly instead of
        // failing outright so a restart (e.g. after Reset settings) can
        // start the new instance while the old one is still shutting down.
        using var mutex = new Mutex(false, "MariaDBBackupTray_Instance");
        bool isNew;
        try
        {
            isNew = mutex.WaitOne(TimeSpan.FromSeconds(3));
        }
        catch (AbandonedMutexException)
        {
            isNew = true; // previous instance died without releasing
        }
        if (!isNew)
        {
            MessageBox.Show("MariaDB Backup is already running in the " +
                            "system tray.", "MariaDB Backup",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Log.Info($"Application starting (version {UpdateService.CurrentVersion}).");
        _ = Task.Run(UpdateService.CheckAndDownloadAsync);
        var firstRun = !AppConfig.Exists();

        if (firstRun)
        {
            var cfg = AppConfig.Load();
            using (var wizard = new SetupWizardForm(cfg))
            {
                if (wizard.ShowDialog() != DialogResult.OK)
                {
                    Log.Info("Setup cancelled; exiting.");
                    return 0;
                }
            }
            cfg = AppConfig.Load();

            // --- Immediate test backup after setup completes --------------
            // No popups: the outcome goes to the log and shows on the
            // dashboard (history + Last backup stat) that opens next.
            BackupResult result;
            using (var wait = new BackupProgressForm())
            {
                var task = Task.Run(() => BackupEngine.Run(cfg, "Setup test"));
                task.ContinueWith(_ => wait.Invoke(wait.Close));
                wait.ShowDialog();
                result = task.Result;
            }
            Log.Info("Setup test backup finished: " +
                     (result.Success
                         ? result.Warning ? "completed with warnings" : "success"
                         : "FAILED"));
        }

        // Autostart at login passes --tray so only the tray icon appears;
        // launching the exe by hand (or finishing setup) opens the dashboard.
        var trayOnly = args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        Application.Run(new TrayApplicationContext(AppConfig.Load(),
            showDashboard: !trayOnly));
        Log.Info("Application exited.");
        return 0;
    }
}

/// <summary>Small borderless "backup in progress" window for the setup test.</summary>
public class BackupProgressForm : Form
{
    public BackupProgressForm()
    {
        Text = "Odyssey Cloud Backups";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 100);
        Font = new Font("Segoe UI", 9F);
        BackColor = Theme.PageBg;

        Controls.Add(new Label
        {
            Text = "Running test backup, please wait...",
            ForeColor = Theme.Navy,
            AutoSize = true,
            Location = new Point(20, 18),
        });
        var bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Location = new Point(20, 50),
            Size = new Size(320, 22),
        };
        Controls.Add(bar);
    }
}
