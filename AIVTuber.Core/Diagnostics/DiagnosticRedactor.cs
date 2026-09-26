using System.Text.RegularExpressions;

namespace AIVTuber.Core.Diagnostics;

/// <summary>
/// Removes secrets from text that goes into logs or an exported diagnostic summary (U06):
/// bearer tokens, API keys, cookies, signed-URL query strings and long opaque tokens.
/// It errs on the side of removing too much; diagnostics keep the shape of the message,
/// not its credentials.
/// </summary>
public static partial class DiagnosticRedactor
{
    public const string Mask = "***";

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;
        // "Authorization: Bearer xyz", "Bearer xyz", "Basic xyz"
        s = AuthHeader().Replace(s, m => m.Groups[1].Value + Mask);
        // key=value / "key": "value" for anything that smells like a credential
        s = SecretAssignment().Replace(s, m => m.Groups[1].Value + Mask);
        // Cookie headers: keep names, drop values
        s = CookieHeader().Replace(s, m => m.Groups[1].Value + Mask);
        // Query strings of URLs can carry signatures and tokens; keep scheme://host/path
        s = UrlQuery().Replace(s, m => m.Groups[1].Value + "?" + Mask);
        // Vendor-looking keys (sk-..., long hex/base64 blobs)
        s = VendorKey().Replace(s, Mask);
        s = OpaqueToken().Replace(s, Mask);
        return s;
    }

    [GeneratedRegex(@"((?:authorization\s*[:=]\s*)?(?:bearer|basic)\s+)[^\s""',;]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthHeader();

    [GeneratedRegex(@"((?:api[_-]?key|apikey|access[_-]?token|session[_-]?token|token|secret|password|passwd|sessdata|bili_jct|buvid3|signature|sig)[""']?\s*[:=]\s*[""']?)[^\s""'&,;}]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"((?:set-)?cookie\s*[:=]\s*)[^\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex CookieHeader();

    [GeneratedRegex(@"(\b(?:https?|wss?)://[^\s?#""']+)\?[^\s#""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlQuery();

    [GeneratedRegex(@"\b(?:sk|pk|rk|ak)-[A-Za-z0-9_\-]{8,}")]
    private static partial Regex VendorKey();

    [GeneratedRegex(@"\b(?=[A-Za-z0-9+/_\-]*\d)(?=[A-Za-z0-9+/_\-]*[A-Za-z])[A-Za-z0-9+/_\-]{32,}={0,2}")]
    private static partial Regex OpaqueToken();
}
