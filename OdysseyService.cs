using System.Text;
using System.Xml.Linq;

namespace MariaDBBackupTray;

public class CompanyDetails
{
    public bool IsSuccess { get; init; }
    public string ResponseMessage { get; init; } = "";
    public string SiteName { get; init; } = "";
    public string OdySiteId { get; init; } = "";
    public bool IsCloudBackup { get; init; }
    public bool IsPermanent { get; init; }
    public string ExpiryDateRaw { get; init; } = "";
    public DateTime? ExpiryDate { get; init; }
}

/// <summary>
/// Client for the Odyssey Control Panel SOAP webservice. Used to verify that
/// an ODY site key exists and that the site is registered for Cloud Backups.
/// </summary>
public static class OdysseyService
{
    private const string Endpoint =
        "https://webservices.odysseysoftware.co.za/CONTROL_PANEL_WEB/awws/Control_Panel.awws";
    private const string SoapAction =
        "urn:Control_Panel/Get_CompanyDetailsCloudBackups";

    private static readonly HttpClient Http = new()
        { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Call Get_CompanyDetailsCloudBackups once. Throws on transport errors.
    /// </summary>
    public static async Task<CompanyDetails> GetCompanyDetailsAsync(string odyKey)
    {
        // WinDev awws uses unwrapped document/literal: the parameter element
        // goes DIRECTLY under Body (no operation wrapper); the operation is
        // routed via the SOAPAction header. A wrapped body is silently
        // ignored and the procedure receives an empty sODY.
        var envelope =
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<s:Body><sODY>" +
            System.Security.SecurityElement.Escape(odyKey) +
            "</sODY></s:Body></s:Envelope>";

        using var content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", $"\"{SoapAction}\"");
        using var response = await Http.PostAsync(Endpoint, content);
        var xml = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        var doc = XDocument.Parse(xml);
        string Val(string name) => doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";

        var expiryRaw = Val("Expiry_Date");
        return new CompanyDetails
        {
            IsSuccess = ParseBool(Val("bIsSuccess")),
            ResponseMessage = Val("sResponseMessage"),
            SiteName = Val("Site_Name"),
            OdySiteId = Val("ODY_SiteID"),
            IsCloudBackup = ParseBool(Val("IsCloud_Backup")),
            IsPermanent = ParseBool(Val("IsPermanent")),
            ExpiryDateRaw = expiryRaw,
            ExpiryDate = ParseDate(expiryRaw),
        };
    }

    /// <summary>
    /// The licensing rule: the Cloud Backup module must be enabled, and be
    /// either permanent or not yet past its expiry date.
    /// Returns (allowed, reason) — reason set when not allowed.
    /// </summary>
    public static (bool Allowed, string Reason) CheckEligibility(CompanyDetails d)
    {
        if (!d.IsCloudBackup)
            return (false, "The Cloud Backups module is not enabled for this site.");
        if (d.IsPermanent)
            return (true, "");
        if (d.ExpiryDate == null)
            return (false, "The Cloud Backups module has no valid expiry date.");
        if (d.ExpiryDate.Value.Date < DateTime.Today)
            return (false, "The Cloud Backups module expired on " +
                           $"{d.ExpiryDate.Value:yyyy-MM-dd}.");
        return (true, "");
    }

    /// <summary>WinDev serializes dates as "YYYYMMDD"; accept common formats.</summary>
    private static DateTime? ParseDate(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0 || raw == "00000000") return null;
        string[] formats =
        {
            "yyyyMMdd", "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy",
            "yyyy-MM-ddTHH:mm:ss", "yyyyMMddHHmmss",
        };
        foreach (var f in formats)
        {
            if (DateTime.TryParseExact(raw, f, null,
                    System.Globalization.DateTimeStyles.None, out var d))
                return d;
        }
        return DateTime.TryParse(raw, out var any) ? any : null;
    }

    /// <summary>
    /// Verify with retries, mirroring the WinDev client: retry up to 5 times,
    /// 5 seconds apart, while there is no usable response. Never throws.
    /// </summary>
    public static async Task<CompanyDetails> VerifyOdyKeyAsync(string odyKey)
    {
        var lastError = "";
        for (var attempt = 0; attempt <= 5; attempt++)
        {
            if (attempt > 0) await Task.Delay(5000);
            try
            {
                var result = await GetCompanyDetailsAsync(odyKey);
                if (result.IsSuccess || result.ResponseMessage != "")
                    return result;
                lastError = "Empty response from the verification service.";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Log.Warn($"ODY verification attempt {attempt + 1} failed: {ex.Message}");
            }
        }
        return new CompanyDetails { IsSuccess = false, ResponseMessage = lastError };
    }

    /// <summary>The service returns booleans as "1"/"true"; strings for some.</summary>
    private static bool ParseBool(string v) =>
        v == "1" ||
        v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("yes", StringComparison.OrdinalIgnoreCase);
}
