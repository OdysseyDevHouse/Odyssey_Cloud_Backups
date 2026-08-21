namespace MariaDBBackupTray;

/// <summary>
/// Flat "bootstrap-like" styling matching the Odyssey Back Office look:
/// white surfaces, navy headings, blue primary buttons, muted gray hints.
/// </summary>
public static class Theme
{
    public static readonly Color Primary = Color.FromArgb(17, 120, 212);      // login button blue
    public static readonly Color PrimaryHover = Color.FromArgb(12, 98, 176);
    public static readonly Color Navy = Color.FromArgb(27, 58, 92);           // "ODYSSEY" navy
    public static readonly Color PageBg = Color.White;
    public static readonly Color BarBg = Color.FromArgb(244, 246, 249);       // nav strip
    public static readonly Color Border = Color.FromArgb(206, 212, 218);
    public static readonly Color TextMuted = Color.FromArgb(108, 117, 125);
    public static readonly Color Success = Color.FromArgb(25, 135, 84);
    public static readonly Color Warning = Color.FromArgb(253, 126, 20);
    public static readonly Color Danger = Color.FromArgb(220, 53, 69);
    public static readonly Color SecondaryHover = Color.FromArgb(233, 240, 248);

    /// <summary>Solid blue call-to-action button (Next / Finish).</summary>
    public static void StylePrimary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = PrimaryHover;
        b.BackColor = Primary;
        b.ForeColor = Color.White;
        b.Font = new Font("Segoe UI Semibold", 9.75F);
        b.Cursor = Cursors.Hand;
    }

    /// <summary>White outlined button (Back, Browse, test buttons).</summary>
    public static void StyleSecondary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.MouseOverBackColor = SecondaryHover;
        b.BackColor = Color.White;
        b.ForeColor = Navy;
        b.Cursor = Cursors.Hand;
        b.Padding = new Padding(6, 2, 6, 2);
    }

    /// <summary>The embedded application icon, or null if unavailable.</summary>
    public static Icon? AppIcon()
    {
        try
        {
            using var stream = typeof(Theme).Assembly
                .GetManifestResourceStream("app.ico");
            return stream == null ? null : new Icon(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// White banner with the app icon and "ODYSSEY / CLOUD BACKUPS"
    /// wordmark, used at the top of the wizard and dashboard.
    /// </summary>
    public static Panel BrandHeader()
    {
        var brand = new Panel
            { Dock = DockStyle.Top, Height = 64, BackColor = Color.White };
        var icon = AppIcon();
        if (icon != null)
        {
            brand.Controls.Add(new PictureBox
            {
                Image = new Icon(icon, 32, 32).ToBitmap(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(32, 32),
                Location = new Point(18, 16),
            });
        }
        brand.Controls.Add(new Label
        {
            Text = "ODYSSEY",
            Font = new Font("Segoe UI", 15F, FontStyle.Bold),
            ForeColor = Navy,
            AutoSize = true,
            Location = new Point(58, 8),
        });
        brand.Controls.Add(new Label
        {
            Text = "CLOUD BACKUPS",
            Font = new Font("Segoe UI", 8.25F, FontStyle.Bold),
            ForeColor = Primary,
            AutoSize = true,
            Location = new Point(61, 38),
        });
        brand.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Border),
            0, brand.Height - 1, brand.Width, brand.Height - 1);
        return brand;
    }

    /// <summary>
    /// Recursively apply the flat look: single-line borders on inputs,
    /// outlined secondary style on every button.
    /// </summary>
    public static void Apply(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBox tb:
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case CheckedListBox clb:
                    clb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case Button b:
                    StyleSecondary(b);
                    break;
                case NumericUpDown num:
                    num.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ListView lv:
                    lv.BorderStyle = BorderStyle.FixedSingle;
                    break;
            }
            Apply(c);
        }
    }
}
