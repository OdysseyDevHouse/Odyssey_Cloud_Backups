using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace MariaDBBackupTray;

/// <summary>
/// System tray application: icon + context menu. Scheduled backups are
/// handled by Windows Task Scheduler (see SchedulerService); the tray is for
/// manual runs, settings, and quick access to backups and the log.
/// </summary>
public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private AppConfig _cfg;
    private DashboardForm? _dashboard;
    private readonly System.Windows.Forms.Timer _updateTimer;

    public TrayApplicationContext(AppConfig cfg, bool showDashboard = true)
    {
        _cfg = cfg;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Dashboard", null, OnDashboard);
        menu.Items.Add("Run backup now", null, OnRunNow);
        menu.Items.Add("Settings...", null, OnSettings);
        menu.Items.Add("Open backup folder", null, OnOpenFolder);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, OnExit);

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Odyssey Cloud Backups",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += OnDashboard;
        // Remove the tray icon whenever the app exits, including exits not
        // routed through the Exit menu item (e.g. Reset settings restart).
        Application.ApplicationExit += (_, _) => _icon.Visible = false;

        // The tray can run for weeks between logins - re-check for updates
        // every 12 hours; a staged update installs on the next app start.
        _updateTimer = new System.Windows.Forms.Timer
            { Interval = 12 * 60 * 60 * 1000 };
        _updateTimer.Tick += (_, _) =>
            _ = Task.Run(UpdateService.CheckAndDownloadAsync);
        _updateTimer.Start();

        if (showDashboard) ShowDashboard();
    }

    private void OnDashboard(object? sender, EventArgs e) => ShowDashboard();

    private void ShowDashboard()
    {
        if (_dashboard is { IsDisposed: false })
        {
            if (_dashboard.WindowState == FormWindowState.Minimized)
                _dashboard.WindowState = FormWindowState.Normal;
            _dashboard.Activate();
            return;
        }
        _dashboard = new DashboardForm(_cfg);
        _dashboard.Show();
    }

    /// <summary>App icon (embedded app.ico), falling back to a drawn one.</summary>
    private static Icon CreateIcon()
    {
        try
        {
            using var stream = typeof(TrayApplicationContext).Assembly
                .GetManifestResourceStream("app.ico");
            if (stream != null) return new Icon(stream);
        }
        catch
        {
            // Fall through to the drawn icon.
        }
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(74, 144, 217));
            using var pen = new Pen(Color.White, 2f);
            g.FillEllipse(brush, 5, 2, 22, 8);
            g.FillRectangle(brush, 5, 6, 22, 20);
            g.FillEllipse(brush, 5, 22, 22, 8);
            g.DrawArc(pen, 5, 9, 22, 8, 0, 180);
            g.DrawArc(pen, 5, 16, 22, 8, 0, 180);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void OnRunNow(object? sender, EventArgs e)
    {
        if (BackupEngine.IsRunning)
        {
            _icon.ShowBalloonTip(3000, "Odyssey Cloud Backups",
                "A backup is already running.", ToolTipIcon.Info);
            return;
        }
        _icon.Text = "Odyssey Cloud Backups - running...";

        Task.Run(() =>
        {
            var result = BackupEngine.Run(_cfg, "Manual");
            _icon.Text = "Odyssey Cloud Backups";
            if (result.Success)
                _icon.ShowBalloonTip(3000, "Odyssey Cloud Backups",
                    result.Warning
                        ? "Backup completed with warnings - see the dashboard."
                        : "Backup completed successfully.", ToolTipIcon.Info);
            else
                MessageBox.Show(result.Summary, "Backup failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        });
    }

    private void OnSettings(object? sender, EventArgs e)
    {
        var cfg = AppConfig.Load();
        using var wizard = new SetupWizardForm(cfg);
        if (wizard.ShowDialog() == DialogResult.OK)
        {
            _cfg = AppConfig.Load();
            Log.Info("Settings updated.");
        }
    }

    private void OnOpenFolder(object? sender, EventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _cfg.ResolvedOutputDir,
            UseShellExecute = true,
        });
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }
}
