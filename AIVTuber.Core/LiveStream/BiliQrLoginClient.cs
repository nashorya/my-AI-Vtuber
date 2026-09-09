using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;

namespace AIVTuber.Core.LiveStream;

public sealed class BiliQrLoginClient : IDisposable
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    private const string GenerateUrl =
        "https://passport.bilibili.com/x/passport-login/web/qrcode/generate?source=main-fe-header";
    private const string PollUrl =
        "https://passport.bilibili.com/x/passport-login/web/qrcode/poll";
    private const string SpiUrl = "https://api.bilibili.com/x/frontend/finger/spi";
    private const string LiveInfoUrl = "https://api.live.bilibili.com/xlive/web-ucenter/user/live_info";
    private const string MyInfoUrl = "https://api.bilibili.com/x/space/myinfo";
    private const string RoomInfoUrl = "https://api.live.bilibili.com/room/v1/Room/getRoomInfoOld";

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies;
    private readonly TimeSpan _pollInterval;
    private readonly bool _ownsHttp;

    public BiliQrLoginClient(HttpClient http, CookieContainer? cookies = null, TimeSpan? pollInterval = null)
        : this(http, cookies ?? new CookieContainer(), pollInterval ?? TimeSpan.FromMilliseconds(1500), ownsHttp: false)
    {
    }

    private BiliQrLoginClient(HttpClient http, CookieContainer cookies, TimeSpan pollInterval, bool ownsHttp)
    {
        _http = http;
        _cookies = cookies;
        _pollInterval = pollInterval;
        _ownsHttp = ownsHttp;
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public static BiliQrLoginClient Create()
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        http.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
        return new BiliQrLoginClient(http, cookies, TimeSpan.FromMilliseconds(1500), ownsHttp: true);
    }

    public async IAsyncEnumerable<BiliQrProgress> RunAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var creds = new BiliQrCredentials("", "", "", "");
        var buvid = BiliQrLogin.ParseBuvid3(await GetStringAsync(SpiUrl, cancellationToken));
        if (!string.IsNullOrEmpty(buvid))
            creds = creds with { Buvid3 = buvid };

        var ticket = BiliQrLogin.ParseGenerate(await GetStringAsync(GenerateUrl, cancellationToken));
        yield return new BiliQrProgress(BiliQrPollStatus.Waiting, ticket.Url);

        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pollJson = await GetStringAsync(
                $"{PollUrl}?qrcode_key={Uri.EscapeDataString(ticket.QrcodeKey)}&source=main-fe-header",
                cancellationToken);
            var status = BiliQrLogin.ParsePollStatus(pollJson);
            switch (status)
            {
                case BiliQrPollStatus.Waiting:
                    yield return new BiliQrProgress(BiliQrPollStatus.Waiting, ticket.Url);
                    break;
                case BiliQrPollStatus.Scanned:
                    yield return new BiliQrProgress(BiliQrPollStatus.Scanned, ticket.Url);
                    break;
                case BiliQrPollStatus.Expired:
                    yield return new BiliQrProgress(BiliQrPollStatus.Expired, ticket.Url, Error: "二维码已过期");
                    yield break;
                case BiliQrPollStatus.Succeeded:
                    creds = BiliQrLogin.Merge(creds, CookiesFromJar());
                    creds = BiliQrLogin.Merge(creds, BiliQrLogin.FromCrossDomainUrl(BiliQrLogin.PollLoginUrl(pollJson)));
                    if (string.IsNullOrEmpty(creds.Buvid3))
                    {
                        var again = BiliQrLogin.ParseBuvid3(await GetStringAsync(SpiUrl, cancellationToken));
                        if (!string.IsNullOrEmpty(again))
                            creds = creds with { Buvid3 = again };
                    }
                    if (string.IsNullOrEmpty(creds.Sessdata) || string.IsNullOrEmpty(creds.BiliJct))
                    {
                        yield return new BiliQrProgress(BiliQrPollStatus.Failed, ticket.Url, Error: "登录成功但未拿到 Cookie");
                        yield break;
                    }
                    var roomId = await FetchRoomIdAsync(cancellationToken);
                    yield return new BiliQrProgress(BiliQrPollStatus.Succeeded, ticket.Url, creds, roomId);
                    yield break;
                default:
                    yield return new BiliQrProgress(BiliQrPollStatus.Failed, ticket.Url, Error: "扫码登录失败");
                    yield break;
            }

            if (_pollInterval > TimeSpan.Zero)
                await Task.Delay(_pollInterval, cancellationToken);
        }

        yield return new BiliQrProgress(BiliQrPollStatus.Expired, ticket.Url, Error: "等待扫码超时");
    }

    private async Task<int> FetchRoomIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var room = BiliQrLogin.ExtractRoomId(await GetStringAsync(LiveInfoUrl, cancellationToken));
            if (room > 0) return room;
        }
        catch { /* try fallback */ }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await GetStringAsync(MyInfoUrl, cancellationToken));
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("mid", out var mid))
            {
                var uid = mid.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? mid.ToString()
                    : mid.GetString();
                if (!string.IsNullOrEmpty(uid))
                    return BiliQrLogin.ExtractRoomId(await GetStringAsync($"{RoomInfoUrl}?mid={Uri.EscapeDataString(uid)}", cancellationToken));
            }
        }
        catch { /* room stays 0 */ }
        return 0;
    }

    private BiliQrCredentials CookiesFromJar()
    {
        var hosts = new[]
        {
            "https://www.bilibili.com/",
            "https://passport.bilibili.com/",
            "https://api.bilibili.com/",
            "https://api.live.bilibili.com/",
        };
        var creds = new BiliQrCredentials("", "", "", "");
        foreach (var host in hosts)
        {
            creds = BiliQrLogin.Merge(creds, BiliQrLogin.ParseCookies(_cookies.GetCookies(new Uri(host)).Cast<Cookie>()));
        }
        return creds;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://www.bilibili.com/");
        req.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
