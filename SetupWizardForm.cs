using System.Text.RegularExpressions;
using MySqlConnector;

namespace MariaDBBackupTray;

/// <summary>
/// Multi-step setup wizard (code-defined UI, no designer needed):
///   1. Database connection (+ test)
///   2. Database selection (fetched live from the server)
///   3. Schedule (day of week / every day + time)
///   4. Client & dealer emails + SMTP settings (+ test email)
///   5. Backup options (zip password, dump exe, folder, retention)
/// Returns DialogResult.OK when saved.
/// </summary>
public class SetupWizardForm : Form
{
    private static readonly string[] SystemDbs =
        { "information_schema", "performance_schema", "mysql", "sys" };
    private static readonly string[] Days =
        { "Every day", "Monday", "Tuesday", "Wednesday", "Thursday",
          "Friday", "Saturday", "Sunday" };
    private static readonly Regex EmailRegex =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    private readonly AppConfig _cfg;
    private readonly List<(Panel Panel, string Title)> _steps = new();
    private int _current;

    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly Panel _nav = new() { Dock = DockStyle.Bottom, Height = 52,
                                          Padding = new Padding(14, 8, 14, 12) };
    private readonly Button _btnBack = new() { Text = "< Back", Width = 90 };
    private readonly Button _btnNext = new() { Text = "Next >", Width = 90 };
    private readonly Label _stepLabel = new()
        { AutoSize = true, ForeColor = Theme.TextMuted };

    // Step 0 (site verification)
    private TextBox _txtOdyKey = null!;
    private Label _lblOdyStatus = null!;
    private string _verifiedOdyKey = "";
    private string _verifiedSiteName = "";
    // Step 1
    private TextBox _txtHost = null!, _txtPort = null!, _txtUser = null!,
                    _txtPass = null!;
    private Label _lblConnStatus = null!;
    // Step 2
    private CheckedListBox _dbList = null!;
    // Step 3
    private ComboBox _cmbDay = null!;
    private NumericUpDown _numHour = null!, _numMin = null!;
    // Step 4
    private TextBox _txtClientEmails = null!, _txtDealerEmails = null!;
    private Label _lblMailStatus = null!;
    // Step 5
    private TextBox _txtZipPass = null!, _txtDumpExe = null!, _txtOutDir = null!;
    private NumericUpDown _numKeep = null!;
    private CheckBox _chkTrayAutostart = null!;

    public SetupWizardForm(AppConfig cfg)
    {
        _cfg = cfg;

        Text = "Odyssey Cloud Backups - Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(600, 604);
        Font = new Font("Segoe UI", 9F);
        BackColor = Theme.PageBg;
        Icon = Theme.AppIcon();
        ShowIcon = Icon != null;

        var brand = Theme.BrandHeader();

        _nav.BackColor = Theme.BarBg;
        _nav.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Theme.Border),
            0, 0, _nav.Width, 0);
        _btnBack.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _btnBack.Location = new Point(14, 9);
        _btnBack.Size = new Size(100, 34);
        _btnNext.Size = new Size(110, 34);
        _btnNext.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _stepLabel.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _nav.Controls.Add(_btnBack);
        _nav.Controls.Add(_btnNext);
        _nav.Controls.Add(_stepLabel);
        _nav.Resize += (_, _) => LayoutNav();

        _content.Padding = new Padding(16);
        Controls.Add(_content);
        Controls.Add(brand);
        Controls.Add(_nav);

        _btnBack.Click += (_, _) => ShowStep(_current - 1);
        _btnNext.Click += async (_, _) => await NextAsync();

        if (cfg.Site.CloudBackupVerified && cfg.Site.OdyKey != "")
        {
            _verifiedOdyKey = cfg.Site.OdyKey;
            _verifiedSiteName = cfg.Site.SiteName;
        }

        _steps.Add((BuildStepSite(), "Site"));
        _steps.Add((BuildStepConnection(), "Connection"));
        _steps.Add((BuildStepDatabases(), "Databases"));
        _steps.Add((BuildStepSchedule(), "Schedule"));
        _steps.Add((BuildStepEmail(), "Email"));
        _steps.Add((BuildStepOptions(), "Options"));

        foreach (var (panel, _) in _steps)
        {
            panel.Dock = DockStyle.Fill;
            panel.Visible = false;
            _content.Controls.Add(panel);
        }

        Theme.Apply(this);              // flat inputs + outlined buttons
        Theme.StylePrimary(_btnNext);   // Next/Finish is the blue CTA
        ShowStep(0);
    }

    // ----------------------------------------------------------- helpers --

    private static Label Header(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", 12F, FontStyle.Bold),
        ForeColor = Theme.Navy,
        AutoSize = true,
        Location = new Point(0, 0),
    };

    private static Label Hint(string text, int x, int y, int width = 540) => new()
    {
        Text = text,
        ForeColor = Theme.TextMuted,
        Location = new Point(x, y),
        Size = new Size(width, 44),
    };

    private void ShowStep(int index)
    {
        if (index < 0 || index >= _steps.Count) return;
        foreach (var (panel, _) in _steps) panel.Visible = false;
        _steps[index].Panel.Visible = true;
        _current = index;
        _stepLabel.Text = $"Step {index + 1} of {_steps.Count}: {_steps[index].Title}";
        _btnBack.Enabled = index > 0;
        _btnNext.Text = index == _steps.Count - 1 ? "Finish" : "Next >";
        LayoutNav();
    }

    /// <summary>
    /// Right-align the Next button and keep the step label to its left.
    /// Must run whenever the nav resizes OR the label text (and therefore
    /// its auto-sized width) changes.
    /// </summary>
    private void LayoutNav()
    {
        _btnNext.Left = _nav.ClientSize.Width - _btnNext.Width - 14;
        _btnNext.Top = 9;
        _stepLabel.Left = _btnNext.Left - _stepLabel.Width - 12;
        _stepLabel.Top = 18;
    }

    private async Task NextAsync()
    {
        var ok = _current switch
        {
            0 => await VerifyOdyKeyAsync(),
            1 => await ValidateConnectionAsync(),
            2 => ValidateDatabases(),
            3 => ValidateSchedule(),
            4 => ValidateEmail(),
            5 => ValidateOptions(),
            _ => true,
        };
        if (!ok) return;

        if (_current == _steps.Count - 1)
            Finish();
        else
            ShowStep(_current + 1);
    }

    // ----------------------------------------------- step 0: site key -----

    private Panel BuildStepSite()
    {
        var p = new Panel();
        p.Controls.Add(Header("Site verification"));
        p.Controls.Add(Hint(
            "Enter this site's ODY key supplied by Odyssey (e.g. ODY9710). " +
            "Cloud backups can only be set up for sites that are registered " +
            "for Cloud Backups.", 0, 40, 560));

        p.Controls.Add(new Label
            { Text = "ODY key:", AutoSize = true, Location = new Point(0, 104) });
        _txtOdyKey = new TextBox
        {
            Location = new Point(120, 100), Width = 180,
            CharacterCasing = CharacterCasing.Upper,
            Text = _cfg.Site.OdyKey,
        };
        p.Controls.Add(_txtOdyKey);

        var btnVerify = new Button
            { Text = "Verify", Location = new Point(312, 99), AutoSize = true };
        btnVerify.Click += async (_, _) => await VerifyOdyKeyAsync();
        p.Controls.Add(btnVerify);

        _lblOdyStatus = new Label
        {
            Location = new Point(0, 148), Size = new Size(560, 60),
            ForeColor = Theme.TextMuted,
        };
        if (_verifiedOdyKey != "")
        {
            _lblOdyStatus.Text =
                $"Verified: {_verifiedSiteName} ({_verifiedOdyKey})";
            _lblOdyStatus.ForeColor = Theme.Success;
        }
        p.Controls.Add(_lblOdyStatus);
        return p;
    }

    private async Task<bool> VerifyOdyKeyAsync()
    {
        var key = _txtOdyKey.Text.Trim();
        if (key.Length == 0)
        {
            _lblOdyStatus.Text = "The ODY key is required.";
            _lblOdyStatus.ForeColor = Theme.Danger;
            return false;
        }
        if (key.Equals(_verifiedOdyKey, StringComparison.OrdinalIgnoreCase))
            return true; // unchanged since the last successful verification

        _lblOdyStatus.Text = "Verifying ODY key with Odyssey...";
        _lblOdyStatus.ForeColor = Theme.TextMuted;
        _btnNext.Enabled = false;
        try
        {
            var details = await OdysseyService.VerifyOdyKeyAsync(key);
            if (!details.IsSuccess)
            {
                _lblOdyStatus.Text = "Could not verify the ODY key: " +
                    (details.ResponseMessage == ""
                        ? "no response from the service."
                        : details.ResponseMessage);
                _lblOdyStatus.ForeColor = Theme.Danger;
                return false;
            }
            // The service answers bIsSuccess=true with empty fields for an
            // unknown key, so an empty site id also means "not found".
            if (details.OdySiteId == "" && details.SiteName == "")
            {
                _lblOdyStatus.Text =
                    $"ODY key '{key}' was not found. Check the key and try again.";
                _lblOdyStatus.ForeColor = Theme.Danger;
                return false;
            }
            var (allowed, reasonText) = OdysseyService.CheckEligibility(details);
            if (!allowed)
            {
                _lblOdyStatus.Text =
                    $"{details.SiteName} ({key}): {reasonText} " +
                    "Contact Odyssey to enable the Cloud Backups module " +
                    "for this site.";
                _lblOdyStatus.ForeColor = Theme.Danger;
                return false;
            }

            _verifiedOdyKey = details.OdySiteId != "" ? details.OdySiteId : key;
            _verifiedSiteName = details.SiteName;
            _txtOdyKey.Text = _verifiedOdyKey;
            _lblOdyStatus.Text =
                $"Verified: {_verifiedSiteName} ({_verifiedOdyKey})";
            _lblOdyStatus.ForeColor = Theme.Success;
            Log.Info($"ODY key verified: {_verifiedOdyKey} ({_verifiedSiteName})");
            return true;
        }
        finally
        {
            _btnNext.Enabled = true;
        }
    }

    // ----------------------------------------------- step 1: connection ---

    private Panel BuildStepConnection()
    {
        var p = new Panel();
        p.Controls.Add(Header("Database server connection"));

        var labels = new[] { "Server / host:", "Port:", "Username:", "Password:" };
        var boxes = new TextBox[4];
        for (var i = 0; i < 4; i++)
        {
            p.Controls.Add(new Label
            {
                Text = labels[i], AutoSize = true,
                Location = new Point(0, 48 + i * 34),
            });
            boxes[i] = new TextBox
            {
                Location = new Point(120, 44 + i * 34), Width = 240,
            };
            p.Controls.Add(boxes[i]);
        }
        (_txtHost, _txtPort, _txtUser, _txtPass) =
            (boxes[0], boxes[1], boxes[2], boxes[3]);
        _txtPass.UseSystemPasswordChar = true;

        _txtHost.Text = _cfg.Db.Host;
        _txtPort.Text = _cfg.Db.Port.ToString();
        _txtUser.Text = _cfg.Db.User;
        _txtPass.Text = _cfg.Db.Password;

        var btnTest = new Button
        {
            Text = "Test connection", Location = new Point(120, 190),
            AutoSize = true,
        };
        btnTest.Click += async (_, _) => await TestConnectionAsync();
        p.Controls.Add(btnTest);

        _lblConnStatus = new Label
        {
            Location = new Point(0, 230), Size = new Size(560, 60),
            ForeColor = Theme.TextMuted,
        };
        p.Controls.Add(_lblConnStatus);
        return p;
    }

    private string ConnectionString(bool withTimeout = true) =>
        new MySqlConnectionStringBuilder
        {
            Server = _txtHost.Text.Trim(),
            Port = uint.TryParse(_txtPort.Text.Trim(), out var port) ? port : 3306,
            UserID = _txtUser.Text.Trim(),
            Password = _txtPass.Text,
            ConnectionTimeout = withTimeout ? 8u : 15u,
        }.ConnectionString;

    private async Task TestConnectionAsync()
    {
        _lblConnStatus.Text = "Connecting...";
        _lblConnStatus.ForeColor = Theme.TextMuted;
        try
        {
            await using var conn = new MySqlConnection(ConnectionString());
            await conn.OpenAsync();
            _lblConnStatus.Text =
                $"Connection successful (server version {conn.ServerVersion}).";
            _lblConnStatus.ForeColor = Theme.Success;
        }
        catch (Exception ex)
        {
            _lblConnStatus.Text = $"Connection failed: {ex.Message}";
            _lblConnStatus.ForeColor = Theme.Danger;
        }
    }

    private async Task<bool> ValidateConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(_txtHost.Text) ||
            string.IsNullOrWhiteSpace(_txtUser.Text))
        {
            MessageBox.Show(this, "Server and username are required.",
                "Missing details", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        if (!uint.TryParse(_txtPort.Text.Trim(), out _))
        {
            MessageBox.Show(this, "Port must be a number.", "Invalid port",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        _btnNext.Enabled = false;
        try
        {
            await using var conn = new MySqlConnection(ConnectionString());
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand("SHOW DATABASES", conn);
            var names = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                if (!SystemDbs.Contains(name.ToLowerInvariant()))
                    names.Add(name);
            }
            PopulateDatabases(names.OrderBy(n => n).ToList());
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Could not connect to the server:\r\n\r\n{ex.Message}",
                "Connection failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            _btnNext.Enabled = true;
        }
    }

    // ------------------------------------------------ step 2: databases ---

    private Panel BuildStepDatabases()
    {
        var p = new Panel();
        p.Controls.Add(Header("Select databases to back up"));

        var btnAll = new Button
            { Text = "Select all", Location = new Point(0, 40), AutoSize = true };
        var btnNone = new Button
            { Text = "Select none", Location = new Point(90, 40), AutoSize = true };
        btnAll.Click += (_, _) => SetAllDatabases(true);
        btnNone.Click += (_, _) => SetAllDatabases(false);
        p.Controls.Add(btnAll);
        p.Controls.Add(btnNone);

        _dbList = new CheckedListBox
        {
            Location = new Point(0, 78),
            Size = new Size(560, 320),
            CheckOnClick = true,
            IntegralHeight = false,
        };
        p.Controls.Add(_dbList);
        return p;
    }

    private void PopulateDatabases(List<string> names)
    {
        var previously = new HashSet<string>(_cfg.Databases,
            StringComparer.OrdinalIgnoreCase);
        _dbList.Items.Clear();
        foreach (var name in names)
        {
            // First run: everything checked by default. Later: keep choices.
            var check = previously.Count == 0 || previously.Contains(name);
            _dbList.Items.Add(name, check);
        }
    }

    private void SetAllDatabases(bool value)
    {
        for (var i = 0; i < _dbList.Items.Count; i++)
            _dbList.SetItemChecked(i, value);
    }

    private bool ValidateDatabases()
    {
        if (_dbList.CheckedItems.Count == 0)
        {
            MessageBox.Show(this, "Select at least one database to back up.",
                "No databases selected", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
        return true;
    }

    // ------------------------------------------------- step 3: schedule ---

    private Panel BuildStepSchedule()
    {
        var p = new Panel();
        p.Controls.Add(Header("Backup schedule"));

        p.Controls.Add(new Label
            { Text = "Run on:", AutoSize = true, Location = new Point(0, 50) });
        _cmbDay = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(100, 46), Width = 140,
        };
        _cmbDay.Items.AddRange(Days.Cast<object>().ToArray());
        _cmbDay.SelectedItem = Days.Contains(_cfg.Schedule.Day)
            ? _cfg.Schedule.Day : "Every day";
        p.Controls.Add(_cmbDay);

        p.Controls.Add(new Label
            { Text = "At time:", AutoSize = true, Location = new Point(0, 90) });
        var parts = _cfg.Schedule.Time.Split(':');
        int.TryParse(parts.ElementAtOrDefault(0), out var hh);
        int.TryParse(parts.ElementAtOrDefault(1), out var mm);

        _numHour = new NumericUpDown
        {
            Minimum = 0, Maximum = 23, Value = Math.Clamp(hh, 0, 23),
            Location = new Point(100, 86), Width = 52,
        };
        _numMin = new NumericUpDown
        {
            Minimum = 0, Maximum = 59, Value = Math.Clamp(mm, 0, 59),
            Location = new Point(170, 86), Width = 52,
        };
        p.Controls.Add(_numHour);
        p.Controls.Add(new Label
            { Text = ":", AutoSize = true, Location = new Point(157, 90) });
        p.Controls.Add(_numMin);
        p.Controls.Add(new Label
        {
            Text = "(24-hour clock)", AutoSize = true,
            ForeColor = Theme.TextMuted, Location = new Point(235, 90),
        });

        p.Controls.Add(new Label
        {
            Text = "The backup is registered with Windows Task Scheduler, so " +
                   "it runs at this time even when the tray app is closed " +
                   "(the PC must be on and you must be logged in).",
            ForeColor = Theme.TextMuted,
            Location = new Point(0, 140), Size = new Size(560, 60),
        });
        return p;
    }

    private bool ValidateSchedule() => true; // NumericUpDown enforces ranges.

    // ---------------------------------------------------- step 4: email ---

    private Panel BuildStepEmail()
    {
        var p = new Panel();
        p.Controls.Add(Header("Email notifications"));

        p.Controls.Add(new Label
        {
            Text = "Client email(s):", AutoSize = true,
            Location = new Point(0, 42),
        });
        _txtClientEmails = new TextBox
        {
            Multiline = true, Location = new Point(130, 38),
            Size = new Size(430, 44),
            Text = string.Join(", ", _cfg.Email.ClientEmails),
        };
        p.Controls.Add(_txtClientEmails);

        p.Controls.Add(new Label
        {
            Text = "Dealer email(s):", AutoSize = true,
            Location = new Point(0, 94),
        });
        _txtDealerEmails = new TextBox
        {
            Multiline = true, Location = new Point(130, 90),
            Size = new Size(430, 44),
            Text = string.Join(", ", _cfg.Email.DealerEmails),
        };
        p.Controls.Add(_txtDealerEmails);

        p.Controls.Add(new Label
        {
            Text = "Separate multiple addresses with commas.",
            ForeColor = Theme.TextMuted, AutoSize = true,
            Location = new Point(130, 138),
        });

        p.Controls.Add(new Label
        {
            Text = "Emails are sent automatically via the built-in Odyssey " +
                   "mail account — no mail server setup is needed.",
            ForeColor = Theme.TextMuted,
            Location = new Point(0, 172), Size = new Size(560, 34),
        });

        var btnTest = new Button
        {
            Text = "Send test email", Location = new Point(0, 214),
            AutoSize = true,
        };
        btnTest.Click += async (_, _) => await SendTestEmailAsync();
        p.Controls.Add(btnTest);

        _lblMailStatus = new Label
        {
            Location = new Point(0, 252), Size = new Size(560, 40),
            ForeColor = Theme.TextMuted,
        };
        p.Controls.Add(_lblMailStatus);
        return p;
    }

    private static (List<string> Valid, List<string> Invalid) ParseEmails(string raw)
    {
        var parts = Regex.Split(raw, @"[,;\r\n]+")
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        return (parts.Where(p => EmailRegex.IsMatch(p)).ToList(),
                parts.Where(p => !EmailRegex.IsMatch(p)).ToList());
    }

    private AppConfig.EmailSettings CollectEmailSettings()
    {
        var (client, _) = ParseEmails(_txtClientEmails.Text);
        var (dealer, _) = ParseEmails(_txtDealerEmails.Text);
        return new AppConfig.EmailSettings
        {
            ClientEmails = client,
            DealerEmails = dealer,
        };
    }

    private async Task SendTestEmailAsync()
    {
        var (_, bad1) = ParseEmails(_txtClientEmails.Text);
        var (_, bad2) = ParseEmails(_txtDealerEmails.Text);
        var bad = bad1.Concat(bad2).ToList();
        if (bad.Count > 0)
        {
            _lblMailStatus.Text = $"Invalid address(es): {string.Join(", ", bad)}";
            _lblMailStatus.ForeColor = Theme.Danger;
            return;
        }

        _lblMailStatus.Text = "Sending test email...";
        _lblMailStatus.ForeColor = Theme.TextMuted;
        var tmp = new AppConfig { Email = CollectEmailSettings() };
        try
        {
            await Task.Run(() => EmailService.Send(tmp, "[Backup] Test email",
                "This is a test email from MariaDB Backup Tray. If you " +
                "received this, notifications are working."));
            _lblMailStatus.Text = "Test email sent successfully.";
            _lblMailStatus.ForeColor = Theme.Success;
        }
        catch (Exception ex)
        {
            _lblMailStatus.Text = $"Test email failed: {ex.Message}";
            _lblMailStatus.ForeColor = Theme.Danger;
        }
    }

    private bool ValidateEmail()
    {
        var (_, bad1) = ParseEmails(_txtClientEmails.Text);
        var (_, bad2) = ParseEmails(_txtDealerEmails.Text);
        var bad = bad1.Concat(bad2).ToList();
        if (bad.Count > 0)
        {
            MessageBox.Show(this,
                "Invalid address(es):\r\n" + string.Join("\r\n", bad),
                "Invalid email", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        return true;
    }

    // -------------------------------------------------- step 5: options ---

    private Panel BuildStepOptions()
    {
        var p = new Panel();
        p.Controls.Add(Header("Backup options"));

        p.Controls.Add(new Label
            { Text = "Zip password:", AutoSize = true, Location = new Point(0, 46) });
        _txtZipPass = new TextBox
        {
            Location = new Point(120, 42), Width = 200,
            UseSystemPasswordChar = true, Text = _cfg.Backup.ZipPassword,
        };
        p.Controls.Add(_txtZipPass);
        p.Controls.Add(new Label
        {
            Text = "(leave blank for none)", ForeColor = Theme.TextMuted,
            AutoSize = true, Location = new Point(330, 46),
        });

        p.Controls.Add(new Label
            { Text = "Dump tool (exe):", AutoSize = true, Location = new Point(0, 84) });
        _txtDumpExe = new TextBox
        {
            Location = new Point(120, 80), Width = 350,
            Text = string.IsNullOrWhiteSpace(_cfg.Backup.DumpExePath)
                ? BackupEngine.FindDumpExe()
                : _cfg.Backup.DumpExePath,
        };
        p.Controls.Add(_txtDumpExe);
        var btnBrowseExe = new Button
            { Text = "Browse...", Location = new Point(478, 79), AutoSize = true };
        btnBrowseExe.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select dump executable",
                Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtDumpExe.Text = dlg.FileName;
        };
        p.Controls.Add(btnBrowseExe);
        p.Controls.Add(Hint(
            "mariadb-dump.exe (or mysqldump.exe) creates the SQL dumps. It " +
            "was auto-detected if a path is shown; adjust only if your " +
            "installation is in an unusual location.", 120, 110, 440));

        p.Controls.Add(new Label
            { Text = "Backup folder:", AutoSize = true, Location = new Point(0, 168) });
        _txtOutDir = new TextBox
        {
            Location = new Point(120, 164), Width = 350,
            Text = _cfg.Backup.OutputDir,
        };
        p.Controls.Add(_txtOutDir);
        var btnBrowseDir = new Button
            { Text = "Browse...", Location = new Point(478, 163), AutoSize = true };
        btnBrowseDir.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
                { Description = "Select backup folder" };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtOutDir.Text = dlg.SelectedPath;
        };
        p.Controls.Add(btnBrowseDir);
        p.Controls.Add(new Label
        {
            Text = "(blank = app data folder)", ForeColor = Theme.TextMuted,
            AutoSize = true, Location = new Point(120, 192),
        });

        p.Controls.Add(new Label
            { Text = "Keep last:", AutoSize = true, Location = new Point(0, 228) });
        _numKeep = new NumericUpDown
        {
            Minimum = 1, Maximum = 365,
            Value = Math.Clamp(_cfg.Backup.KeepLast, 1, 365),
            Location = new Point(120, 224), Width = 60,
        };
        p.Controls.Add(_numKeep);
        p.Controls.Add(new Label
        {
            Text = "most recent backups of each database", AutoSize = true,
            Location = new Point(188, 228),
        });

        _chkTrayAutostart = new CheckBox
        {
            Text = "Start the tray app with Windows (recommended)",
            AutoSize = true, Checked = true, Location = new Point(0, 270),
        };
        p.Controls.Add(_chkTrayAutostart);

        p.Controls.Add(Hint(
            "On Finish, the backup is registered with Windows Task Scheduler " +
            "and a test backup runs immediately to verify everything works.",
            0, 306));
        return p;
    }

    private bool ValidateOptions()
    {
        var exe = _txtDumpExe.Text.Trim();
        if (exe.Length > 0 && !File.Exists(exe))
        {
            MessageBox.Show(this, $"Dump executable not found:\r\n{exe}",
                "Not found", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        if (exe.Length == 0 && BackupEngine.FindDumpExe().Length == 0)
        {
            MessageBox.Show(this,
                "Could not auto-detect mariadb-dump.exe / mysqldump.exe.\r\n" +
                "Browse to it manually (usually in your MariaDB bin folder).",
                "Dump tool missing", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        return true;
    }

    // ----------------------------------------------------------- finish ---

    private void Finish()
    {
        _cfg.Site = new AppConfig.SiteSettings
        {
            OdyKey = _verifiedOdyKey,
            SiteName = _verifiedSiteName,
            CloudBackupVerified = true,
        };
        _cfg.Db = new AppConfig.DbSettings
        {
            Host = _txtHost.Text.Trim(),
            Port = int.TryParse(_txtPort.Text.Trim(), out var port) ? port : 3306,
            User = _txtUser.Text.Trim(),
            Password = _txtPass.Text,
        };
        _cfg.Databases = _dbList.CheckedItems.Cast<object>()
            .Select(o => o.ToString()!).ToList();
        _cfg.Schedule = new AppConfig.ScheduleSettings
        {
            Day = _cmbDay.SelectedItem?.ToString() ?? "Every day",
            Time = $"{(int)_numHour.Value:D2}:{(int)_numMin.Value:D2}",
        };
        _cfg.Email = CollectEmailSettings();
        _cfg.Backup = new AppConfig.BackupSettings
        {
            ZipPassword = _txtZipPass.Text,
            DumpExePath = _txtDumpExe.Text.Trim(),
            OutputDir = _txtOutDir.Text.Trim(),
            KeepLast = (int)_numKeep.Value,
        };

        _cfg.Save();

        try
        {
            SchedulerService.Register(_cfg);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Settings were saved, but the Windows scheduled task could " +
                $"not be created:\r\n\r\n{ex.Message}\r\n\r\nYou can still " +
                "run backups manually from the tray menu.",
                "Task Scheduler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        try
        {
            SchedulerService.SetTrayAutostart(_chkTrayAutostart.Checked);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not set tray autostart: {ex.Message}");
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
