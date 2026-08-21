using System.Net;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace MariaDBBackupTray;

public static class EmailService
{
    /// <summary>
    /// Send the styled backup report email (HTML with a per-database status
    /// table, plain-text fallback). Throws on failure.
    /// </summary>
    public static void SendReport(AppConfig cfg, BackupReport report)
    {
        // Identify the site by its ODY key; machine name only as fallback
        // for configs saved before site verification existed.
        var site = report.OdyKey != "" ? report.OdyKey : report.MachineName;
        var subject = $"Odyssey Cloud Backups - {site} - " +
                      $"{report.Started:yyyy-MM-dd HH:mm}";
        Send(cfg, subject, BackupEngine.BuildSummary(report),
            BuildHtml(report));
    }

    /// <summary>
    /// Alert that the site's Cloud Backups module is disabled or expired and
    /// backups are therefore NOT running. Throws on failure.
    /// </summary>
    public static void SendModuleAlert(AppConfig cfg, string reason)
    {
        var site = cfg.Site.OdyKey != ""
            ? cfg.Site.OdyKey : Environment.MachineName;
        var subject = $"Odyssey Cloud Backups - {site} - Action required";
        var text =
            $"Cloud backups are NOT running for " +
            $"{cfg.Site.SiteName} ({cfg.Site.OdyKey}).\r\n\r\n" +
            $"{reason}\r\n\r\n" +
            "Please contact Odyssey Software to enable the Cloud Backups " +
            "module for this site. Backups will resume automatically once " +
            "the module is active.";
        Send(cfg, subject, text, BuildModuleAlertHtml(cfg, reason));
    }

    /// <summary>
    /// Send a notification to all client + dealer addresses (deduplicated)
    /// using the compiled-in SMTP account (EmailDefaults.cs).
    /// Throws on failure so callers can report the error.
    /// </summary>
    public static void Send(AppConfig cfg, string subject, string body,
                            string? htmlBody = null)
    {
        var em = cfg.Email;
        var recipients = em.ClientEmails.Concat(em.DealerEmails)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (recipients.Count == 0)
        {
            Log.Warn("No email recipients configured; skipping notification.");
            return;
        }

        var from = !string.IsNullOrWhiteSpace(EmailDefaults.FromAddress)
            ? EmailDefaults.FromAddress
            : EmailDefaults.SmtpUser;

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        foreach (var addr in recipients)
            message.To.Add(MailboxAddress.Parse(addr));
        message.Subject = subject;
        var builder = new BodyBuilder { TextBody = body };
        if (htmlBody != null) builder.HtmlBody = htmlBody;
        message.Body = builder.ToMessageBody();

        var security = EmailDefaults.SmtpSecurity switch
        {
            "SSL" => SecureSocketOptions.SslOnConnect,
            "None" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };

        using var client = new SmtpClient();
        client.Timeout = 30_000;
        client.Connect(EmailDefaults.SmtpHost, EmailDefaults.SmtpPort, security);
        try
        {
            if (!string.IsNullOrWhiteSpace(EmailDefaults.SmtpUser))
                client.Authenticate(EmailDefaults.SmtpUser,
                    EmailDefaults.SmtpPassword);
            client.Send(message);
            Log.Info($"Notification email sent to {string.Join(", ", recipients)}");
        }
        finally
        {
            client.Disconnect(true);
        }
    }

    // -------------------------------------------------- HTML report ------

    /// <summary>
    /// Branded HTML report matching the Odyssey theme. Table-based layout
    /// with inline styles only, so it renders in Outlook/Gmail/webmail.
    /// </summary>
    private static string BuildHtml(BackupReport r)
    {
        var (statusColor, statusText) = r.Status switch
        {
            "SUCCESS" => ("#198754", "Backup completed successfully"),
            "WARNING" => ("#fd7e14", "Backup completed with warnings"),
            _ => ("#dc3545", "Backup FAILED"),
        };

        var sb = new StringBuilder();
        sb.Append($@"
<div style=""background:#f4f6f9;padding:24px 12px;font-family:'Segoe UI',Arial,sans-serif;color:#212529;"">
 <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0""
        style=""max-width:640px;margin:0 auto;background:#ffffff;border:1px solid #dee2e6;"">
  <tr><td style=""padding:18px 24px;border-bottom:1px solid #dee2e6;"">
    <div style=""font-size:22px;font-weight:bold;color:#1b3a5c;letter-spacing:1px;"">ODYSSEY</div>
    <div style=""font-size:11px;font-weight:bold;color:#1178d4;letter-spacing:3px;"">CLOUD BACKUPS</div>
  </td></tr>
  <tr><td style=""background:{statusColor};color:#ffffff;padding:12px 24px;font-size:16px;font-weight:bold;"">
    {statusText}
  </td></tr>
  <tr><td style=""padding:16px 24px 4px;"">
    <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" style=""font-size:13px;color:#212529;"">
      {(r.OdyKey != "" ? Row("Site", H($"{r.OdyKey} - {r.SiteName}")) : "")}
      {Row("Computer", H(r.MachineName))}
      {Row("Server", H(r.Server))}
      {Row("Started", r.Started.ToString("yyyy-MM-dd HH:mm:ss"))}
      {Row("Duration", $"{r.DurationSeconds:F0} seconds")}
      {Row("Trigger", H(r.Trigger))}");
        if (r.Archive != "")
        {
            sb.Append($@"
      {Row("Archive", H(Path.GetFileName(r.Archive)))}
      {Row("Size", BackupEngine.HumanSize(r.SizeBytes))}");
        }
        sb.Append(@"
    </table>
  </td></tr>");

        if (r.Databases.Count > 0)
        {
            sb.Append(@"
  <tr><td style=""padding:12px 24px 20px;"">
    <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0""
           style=""font-size:13px;border-collapse:collapse;"">
      <tr>
        <th align=""left"" style=""padding:8px 10px;background:#f4f6f9;border:1px solid #dee2e6;color:#1b3a5c;"">Database</th>
        <th align=""left"" style=""padding:8px 10px;background:#f4f6f9;border:1px solid #dee2e6;color:#1b3a5c;"">Status</th>
      </tr>");
            foreach (var d in r.Databases)
            {
                var badge = d.Success
                    ? @"<span style=""color:#198754;font-weight:bold;"">&#10004; Success</span>"
                    : @"<span style=""color:#dc3545;font-weight:bold;"">&#10008; Failed</span>";
                var error = d.Success ? "" :
                    $@"<div style=""color:#dc3545;font-size:12px;margin-top:2px;"">{H(d.Error)}</div>";
                sb.Append($@"
      <tr>
        <td style=""padding:8px 10px;border:1px solid #dee2e6;"">{H(d.Database)}</td>
        <td style=""padding:8px 10px;border:1px solid #dee2e6;"">{badge}{error}</td>
      </tr>");
            }
            sb.Append(@"
    </table>
  </td></tr>");
        }

        if (r.GeneralError != "")
        {
            sb.Append($@"
  <tr><td style=""padding:0 24px 20px;"">
    <div style=""background:#fdecea;border:1px solid #dc3545;color:#dc3545;padding:10px 12px;font-size:13px;"">
      {H(r.GeneralError)}
    </div>
  </td></tr>");
        }

        sb.Append($@"
  <tr><td style=""padding:12px 24px;border-top:1px solid #dee2e6;color:#6c757d;font-size:11px;"">
    Automated message from Odyssey Cloud Backups on {H(r.MachineName)}.
  </td></tr>
 </table>
</div>");
        return sb.ToString();
    }

    /// <summary>Branded card for the "module not active" alert.</summary>
    private static string BuildModuleAlertHtml(AppConfig cfg, string reason)
    {
        var site = cfg.Site.SiteName != ""
            ? $"{H(cfg.Site.SiteName)} ({H(cfg.Site.OdyKey)})"
            : H(Environment.MachineName);
        return $@"
<div style=""background:#f4f6f9;padding:24px 12px;font-family:'Segoe UI',Arial,sans-serif;color:#212529;"">
 <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0""
        style=""max-width:640px;margin:0 auto;background:#ffffff;border:1px solid #dee2e6;"">
  <tr><td style=""padding:18px 24px;border-bottom:1px solid #dee2e6;"">
    <div style=""font-size:22px;font-weight:bold;color:#1b3a5c;letter-spacing:1px;"">ODYSSEY</div>
    <div style=""font-size:11px;font-weight:bold;color:#1178d4;letter-spacing:3px;"">CLOUD BACKUPS</div>
  </td></tr>
  <tr><td style=""background:#dc3545;color:#ffffff;padding:12px 24px;font-size:16px;font-weight:bold;"">
    Cloud backups are NOT running
  </td></tr>
  <tr><td style=""padding:20px 24px;font-size:13px;line-height:1.6;"">
    <p style=""margin:0 0 12px;"">Automatic cloud backups for <b>{site}</b> could not run.</p>
    <div style=""background:#fdecea;border:1px solid #dc3545;color:#dc3545;padding:10px 12px;margin:0 0 12px;"">
      {H(reason)}
    </div>
    <p style=""margin:0;"">Please contact Odyssey Software to enable the Cloud Backups
    module for this site. Backups will resume automatically once the module is active.</p>
  </td></tr>
  <tr><td style=""padding:12px 24px;border-top:1px solid #dee2e6;color:#6c757d;font-size:11px;"">
    Automated message from Odyssey Cloud Backups on {H(Environment.MachineName)}.
  </td></tr>
 </table>
</div>";
    }

    private static string H(string s) => WebUtility.HtmlEncode(s);

    private static string Row(string label, string value) =>
        $@"<tr><td style=""padding:3px 16px 3px 0;color:#6c757d;white-space:nowrap;"">{label}</td>" +
        $@"<td style=""padding:3px 0;"">{value}</td></tr>";
}
