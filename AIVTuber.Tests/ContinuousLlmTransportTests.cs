using System.Net;
using System.Text;
using System.Text.Json;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Avatar;

namespace AIVTuber.Tests;

public class ContinuousLlmTransportTests
{
    private sealed class ResponseHandler(string content) : HttpMessageHandler
    {
        public JsonElement Request;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            var stream = string.Concat(content.Select(c => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = c.ToString() } } } }) + "\n\n"));
            return new(HttpStatusCode.OK) { Content = new StringContent(stream + "data: [DONE]\n", Encoding.UTF8, "text/event-stream") };
        }
    }
    [Fact]
    public async Task OneRequestParsesSplitJsonAndOnlyEmitsReplyBody()
    {
        var handler = new ResponseHandler("{\"reply\":\"（想吃蛋糕）[emotion:happy]\",\"avatar\":{\"targets\":{\"headRoll\":0.2}}}");
        using var client = new LlmClient("角色", () => ["headRoll"], handler);
        AvatarReplyPlan? plan = null; var emotions = new List<string>(); var spoken = new List<string>();
        client.OnAvatarPlanReady += (_, p) => plan = p;
        client.OnEmotionDetected += (_, e) => emotions.Add(e);
        client.OnSentenceReady += (_, s) => spoken.Add(s);
        var body = new StringBuilder();
        await foreach (var token in client.StreamAsync([], "你好")) body.Append(token);
        Assert.Equal("（想吃蛋糕）", body.ToString()); Assert.Empty(spoken);
        Assert.Equal(["happy"], emotions); Assert.NotNull(plan?.Intent);
        Assert.Equal(512, handler.Request.GetProperty("max_tokens").GetInt32());
        Assert.DoesNotContain("AIVTuberHeadRoll", handler.Request.GetRawText());
    }
    [Fact]
    public async Task BrokenJsonNeverProducesSpeechOrIntent()
    {
        var handler = new ResponseHandler("{\"reply\":\"你好\"");
        using var client = new LlmClient("角色", () => ["headRoll"], handler);
        var emitted = 0;
        client.OnAvatarPlanReady += (_, _) => emitted++;
        client.OnSentenceReady += (_, _) => emitted++;
        await Assert.ThrowsAnyAsync<JsonException>(async () => { await foreach (var _ in client.StreamAsync([], "你好")) emitted++; });
        Assert.Equal(0, emitted);
    }
    [Fact]
    public async Task LegacyAndMemoryClientsRetainPlainTextProtocol()
    {
        var handler = new ResponseHandler("你好[emotion:happy]");
        using var client = new LlmClient("", null, handler);
        var output = new StringBuilder();
        await foreach (var chunk in client.StreamAsync([], "你好")) output.Append(chunk);
        Assert.Equal("你好", output.ToString());
        Assert.Equal(256, handler.Request.GetProperty("max_tokens").GetInt32());
        Assert.DoesNotContain("transitionMs", handler.Request.GetRawText());
    }
}
