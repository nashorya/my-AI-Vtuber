using System.Security.Cryptography;
using System.Text;

namespace AIVTuber.Core.RealtimeAsr;

/// <summary>
/// Client-side signature for 腾讯云实时语音识别 (wss://asr.cloud.tencent.com/asr/v2/{appid}),
/// per the documented [D3] semantics: sort the query parameters by key, build
/// "asr.cloud.tencent.com/asr/v2/?" + canonical query, HMAC-SHA1 with the secret key,
/// base64-encode, then append as the <c>signature</c> parameter. The secret key never
/// appears in the URL or logs.
/// </summary>
public static class TencentRealtimeSignature
{
    public const string Host = "asr.cloud.tencent.com";
    public const string Path = "/asr/v2/";

    /// <summary>Builds the sorted canonical query (without the signature parameter).</summary>
    public static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var sorted = parameters
            .Where(p => !string.IsNullOrEmpty(p.Key))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value ?? string.Empty)}");
        return string.Join("&", sorted);
    }

    /// <summary>The string being signed: host + path + "?" + canonical query.</summary>
    public static string StringToSign(string canonicalQuery) => $"{Host}{Path}?{canonicalQuery}";

    /// <summary>base64(HMAC-SHA1(secretKey, stringToSign)).</summary>
    public static string Sign(string secretKey, string stringToSign)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secretKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        return Convert.ToBase64String(hash);
    }

    /// <summary>Builds the full signed query string (canonical query + signature).</summary>
    public static string BuildSignedQuery(IEnumerable<KeyValuePair<string, string>> parameters, string secretKey)
    {
        var canonical = CanonicalQuery(parameters);
        var signature = Sign(secretKey, StringToSign(canonical));
        return $"{canonical}&signature={Uri.EscapeDataString(signature)}";
    }
}
