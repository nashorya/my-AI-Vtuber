using System.Globalization;
using System.Net;
using System.Text.Json;

namespace AIVTuber.Core.LiveStream;

public enum BiliQrPollStatus
{
    Waiting,
    Scanned,
    Expired,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record BiliQrCredentials(string Sessdata, string BiliJct, string Buvid3, string DedeUserId);

public sealed record BiliQrTicket(string Url, string QrcodeKey);

public sealed record BiliQrProgress(
    BiliQrPollStatus Status,
    string? QrUrl = null,
    BiliQrCredentials? Credentials = null,
    int RoomId = 0,
    string? Error = null);

public static class BiliQrLogin
{
    public static BiliQrPollStatus ParsePollStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var api) && api.ValueKind == JsonValueKind.Number && api.GetInt32() != 0)
            return BiliQrPollStatus.Failed;
        var dataCode = 0;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("code", out var inner) && inner.ValueKind == JsonValueKind.Number)
            dataCode = inner.GetInt32();
        return dataCode switch
        {
            0 => BiliQrPollStatus.Succeeded,
            86101 => BiliQrPollStatus.Waiting,
            86090 => BiliQrPollStatus.Scanned,
            86038 => BiliQrPollStatus.Expired,
            _ => BiliQrPollStatus.Failed,
        };
    }

    public static BiliQrCredentials ParseCookies(IEnumerable<Cookie> cookies)
    {
        var creds = new BiliQrCredentials("", "", "", "");
        foreach (var cookie in cookies)
        {
            switch (cookie.Name.ToLowerInvariant())
            {
                case "sessdata":
                    creds = creds with { Sessdata = cookie.Value };
                    break;
                case "bili_jct":
                    creds = creds with { BiliJct = cookie.Value };
                    break;
                case "buvid3":
                    creds = creds with { Buvid3 = cookie.Value };
                    break;
                case "dedeuserid":
                    creds = creds with { DedeUserId = cookie.Value };
                    break;
            }
        }
        return creds;
    }

    public static BiliQrCredentials Merge(BiliQrCredentials baseCreds, BiliQrCredentials extra) =>
        new(
            string.IsNullOrEmpty(extra.Sessdata) ? baseCreds.Sessdata : extra.Sessdata,
            string.IsNullOrEmpty(extra.BiliJct) ? baseCreds.BiliJct : extra.BiliJct,
            string.IsNullOrEmpty(extra.Buvid3) ? baseCreds.Buvid3 : extra.Buvid3,
            string.IsNullOrEmpty(extra.DedeUserId) ? baseCreds.DedeUserId : extra.DedeUserId);

    public static BiliQrCredentials FromCrossDomainUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return new BiliQrCredentials("", "", "", "");
        var query = ParseQuery(uri.Query);
        return new BiliQrCredentials(
            query.GetValueOrDefault("SESSDATA", ""),
            query.GetValueOrDefault("bili_jct", ""),
            query.GetValueOrDefault("buvid3", ""),
            query.GetValueOrDefault("DedeUserID", ""));
    }

    public static string? PollLoginUrl(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("url", out var url) &&
            url.ValueKind == JsonValueKind.String)
            return url.GetString();
        return null;
    }

    public static int ExtractRoomId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
            return 0;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return 0;
        foreach (var key in new[] { "room_id", "roomid" })
        {
            if (!data.TryGetProperty(key, out var el)) continue;
            var room = AsRoomId(el);
            if (room > 0) return room;
        }
        return 0;
    }

    public static BiliQrTicket ParseGenerate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
            throw new InvalidOperationException(ReadMessage(root) ?? "无法申请登录二维码");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("无法申请登录二维码");
        var url = data.TryGetProperty("url", out var u) ? u.GetString() : null;
        var key = data.TryGetProperty("qrcode_key", out var k) ? k.GetString() : null;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            throw new InvalidOperationException("无法申请登录二维码");
        return new BiliQrTicket(url, key);
    }

    public static string? ParseBuvid3(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;
        return data.TryGetProperty("b_3", out var b3) && b3.ValueKind == JsonValueKind.String ? b3.GetString() : null;
    }

    private static int AsRoomId(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.TryGetInt32(out var n) ? n : (int)el.GetDouble(),
        JsonValueKind.String when int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => 0,
    };

    private static string? ReadMessage(JsonElement root) =>
        root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString() : null;

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (query.StartsWith('?')) query = query[1..];
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = Uri.UnescapeDataString(part[..eq].Replace('+', ' '));
            var val = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            result[key] = val;
        }
        return result;
    }
}
