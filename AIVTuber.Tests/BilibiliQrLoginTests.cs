using System.Net;
using System.Net.Http;
using System.Text;
using AIVTuber.Core.Config;
using AIVTuber.Core.LiveStream;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.Tests;

public class BilibiliQrLoginTests
{
    [Fact]
    public void ParsePollStatus_MapsKnownCodes()
    {
        Assert.Equal(BiliQrPollStatus.Waiting, BiliQrLogin.ParsePollStatus("""{"code":0,"data":{"code":86101}}"""));
        Assert.Equal(BiliQrPollStatus.Scanned, BiliQrLogin.ParsePollStatus("""{"code":0,"data":{"code":86090}}"""));
        Assert.Equal(BiliQrPollStatus.Expired, BiliQrLogin.ParsePollStatus("""{"code":0,"data":{"code":86038}}"""));
        Assert.Equal(BiliQrPollStatus.Succeeded, BiliQrLogin.ParsePollStatus("""{"code":0,"data":{"code":0,"url":"https://x"}}"""));
    }

    [Fact]
    public void ParsePollStatus_ApiError_IsFailed()
    {
        Assert.Equal(BiliQrPollStatus.Failed, BiliQrLogin.ParsePollStatus("""{"code":-1,"message":"nope"}"""));
    }

    [Fact]
    public void MergeCookies_KeepsExistingBuvid()
    {
        var merged = BiliQrLogin.Merge(
            new BiliQrCredentials("", "", "keep-me", ""),
            BiliQrLogin.ParseCookies(new[]
            {
                new Cookie("SESSDATA", "s"),
                new Cookie("bili_jct", "j"),
            }));
        Assert.Equal("s", merged.Sessdata);
        Assert.Equal("j", merged.BiliJct);
        Assert.Equal("keep-me", merged.Buvid3);
    }

    [Fact]
    public void CredentialsFromCrossDomainUrl_DecodesQuery()
    {
        var got = BiliQrLogin.FromCrossDomainUrl(
            "https://passport.biligame.com/crossDomain?DedeUserID=9&SESSDATA=aa%2Cbb&bili_jct=jj");
        Assert.Equal("aa,bb", got.Sessdata);
        Assert.Equal("jj", got.BiliJct);
        Assert.Equal("9", got.DedeUserId);
    }

    [Theory]
    [InlineData("""{"code":0,"data":{"room_id":25902599}}""", 25902599)]
    [InlineData("""{"code":0,"data":{"roomid":123}}""", 123)]
    [InlineData("""{"code":0,"data":{"room_id":"456"}}""", 456)]
    [InlineData("""{"code":0,"data":{}}""", 0)]
    [InlineData("""{"code":-101,"data":{}}""", 0)]
    public void ExtractRoomId_ReadsKnownShapes(string json, int want)
    {
        Assert.Equal(want, BiliQrLogin.ExtractRoomId(json));
    }

    [Fact]
    public void ParseGenerate_ReadsUrlAndKey()
    {
        var ticket = BiliQrLogin.ParseGenerate("""{"code":0,"data":{"url":"https://scan","qrcode_key":"abc"}}""");
        Assert.Equal("https://scan", ticket.Url);
        Assert.Equal("abc", ticket.QrcodeKey);
    }

    [Fact]
    public async Task RunAsync_CompletesFromPollUrlAndLiveInfo()
    {
        var handler = new ScriptedHandler();
        handler.Map("qrcode/generate", """{"code":0,"data":{"url":"https://scan.example/qr","qrcode_key":"k1"}}""");
        handler.Map("finger/spi", """{"code":0,"data":{"b_3":"BUVID-1"}}""");
        handler.Map("qrcode/poll", """{"code":0,"data":{"code":0,"url":"https://passport.biligame.com/crossDomain?DedeUserID=42&SESSDATA=sess-1&bili_jct=jct-1"}}""");
        handler.Map("live_info", """{"code":0,"data":{"room_id":25902599}}""");

        var client = new BiliQrLoginClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") });
        BiliQrProgress? last = null;
        await foreach (var step in client.RunAsync())
            last = step;

        Assert.NotNull(last);
        Assert.Equal(BiliQrPollStatus.Succeeded, last!.Status);
        Assert.Equal("sess-1", last.Credentials!.Sessdata);
        Assert.Equal("jct-1", last.Credentials.BiliJct);
        Assert.Equal("BUVID-1", last.Credentials.Buvid3);
        Assert.Equal(25902599, last.RoomId);
        Assert.Equal("https://scan.example/qr", last.QrUrl);
    }

    [Fact]
    public async Task ApplyBilibiliLogin_WritesWorkingDraft()
    {
        var vm = new ConfigViewModel(new AppConfig(), ["mic"], _ => { }, _ => Task.CompletedTask);
        vm.ApplyBilibiliLogin(new BiliQrCredentials("S", "J", "B", "1"), 88);
        Assert.Equal("S", vm.Working.Bilibili.Sessdata);
        Assert.Equal("J", vm.Working.Bilibili.BiliJct);
        Assert.Equal("B", vm.Working.Bilibili.Buvid3);
        Assert.Equal(88, vm.Working.Bilibili.RoomId);
        Assert.True(vm.IsDirty);
        var draft = vm.BuildWebDraft();
        var json = System.Text.Json.JsonSerializer.Serialize(draft);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var bili = doc.RootElement.GetProperty("bilibili");
        Assert.Equal("S", bili.GetProperty("sessdata").GetString());
        Assert.Equal("J", bili.GetProperty("biliJct").GetString());
        Assert.Equal("B", bili.GetProperty("buvid3").GetString());
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly List<(string Needle, string Body)> _routes = new();

        public void Map(string needle, string body) => _routes.Add((needle, body));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            foreach (var (needle, body) in _routes)
            {
                if (!url.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"code":-404}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
