using System.Text;
using System.Text.Json;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public class MimoTtsClientTests
{
    [Fact]
    public void BuildRequestJson_UsesPcm16StreamAndAssistantText()
    {
        var json = MimoTtsClient.BuildRequestJson(
            "你好呀", "冰糖", "mimo-v2.5-tts", emotion: null, speed: 1.0);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("mimo-v2.5-tts", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("pcm16", root.GetProperty("audio").GetProperty("format").GetString());
        Assert.Equal("冰糖", root.GetProperty("audio").GetProperty("voice").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("assistant", messages[0].GetProperty("role").GetString());
        Assert.Equal("你好呀", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public void BuildRequestJson_PrependsEmotionStyleTag()
    {
        var json = MimoTtsClient.BuildRequestJson(
            "今天真好", "茉莉", MimoTtsClient.DefaultModel, emotion: "happy", speed: 1.0);

        using var doc = JsonDocument.Parse(json);
        var messages = doc.RootElement.GetProperty("messages");
        var content = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString();
        Assert.Equal("(开心)今天真好", content);
    }

    [Fact]
    public void BuildRequestJson_AddsUserInstructionForFastSpeed()
    {
        var json = MimoTtsClient.BuildRequestJson(
            "快说", "苏打", MimoTtsClient.DefaultModel, emotion: null, speed: 1.3);

        using var doc = JsonDocument.Parse(json);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Contains("语速稍快", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public void MapToStyleTag_MapsCommonEmotions()
    {
        Assert.Equal("开心", MimoTtsClient.MapToStyleTag("happy"));
        Assert.Equal("悲伤", MimoTtsClient.MapToStyleTag("难过"));
        Assert.Equal("愤怒", MimoTtsClient.MapToStyleTag("angry"));
        Assert.Null(MimoTtsClient.MapToStyleTag(null));
        Assert.Null(MimoTtsClient.MapToStyleTag("unknown-xyz"));
    }

    [Fact]
    public void ApplyStyleTag_DoesNotDoubleTag()
    {
        Assert.Equal("(开心)hi", MimoTtsClient.ApplyStyleTag("(开心)hi", "sad"));
        Assert.Equal("（悲伤）hi", MimoTtsClient.ApplyStyleTag("（悲伤）hi", "happy"));
    }

    [Fact]
    public void TryParseSseDataLine_ExtractsPayload()
    {
        Assert.True(MimoTtsClient.TryParseSseDataLine("data: {\"a\":1}", out var p));
        Assert.Equal("{\"a\":1}", p);
        Assert.True(MimoTtsClient.TryParseSseDataLine("data: [DONE]", out var done));
        Assert.Equal("[DONE]", done);
        Assert.False(MimoTtsClient.TryParseSseDataLine("event: message", out _));
        Assert.False(MimoTtsClient.TryParseSseDataLine("", out _));
    }

    [Fact]
    public void TryExtractAudioPcm_DecodesBase64FromDelta()
    {
        var pcm = Encoding.UTF8.GetBytes("AB"); // any bytes
        var b64 = Convert.ToBase64String(pcm);
        var json = "{\"choices\":[{\"delta\":{\"audio\":{\"data\":\"" + b64 + "\"}}}]}";

        Assert.True(MimoTtsClient.TryExtractAudioPcm(json, out var decoded));
        Assert.Equal(pcm, decoded);
    }

    [Fact]
    public void TryExtractAudioPcm_ReturnsFalseWithoutAudio()
    {
        Assert.False(MimoTtsClient.TryExtractAudioPcm(
            """{"choices":[{"delta":{"content":"x"}}]}""", out _));
    }

    [Fact]
    public async Task StreamAsync_YieldsPcmChunksFromSse()
    {
        var pcm1 = new byte[] { 1, 2, 3, 4 };
        var pcm2 = new byte[] { 5, 6 };
        var b1 = Convert.ToBase64String(pcm1);
        var b2 = Convert.ToBase64String(pcm2);
        var sse = string.Join("\n",
            $"data: {{\"choices\":[{{\"delta\":{{\"audio\":{{\"data\":\"{b1}\"}}}}}}]}}",
            "",
            $"data: {{\"choices\":[{{\"delta\":{{\"audio\":{{\"data\":\"{b2}\"}}}}}}]}}",
            "data: [DONE]",
            "");

        var handler = new StubHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            });
        using var http = new HttpClient(handler);
        using var client = new MimoTtsClient(
            new TtsConfig { Provider = "mimo", ApiKey = "k", VoiceId = "冰糖" },
            http,
            baseUrl: "http://mimo.test/v1");

        var chunks = new List<byte[]>();
        await foreach (var c in client.StreamAsync("你好", "冰糖", "happy"))
            chunks.Add(c);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(pcm1, chunks[0]);
        Assert.Equal(pcm2, chunks[1]);
        Assert.Contains(handler.LastRequestHeaders, h =>
            h.Key.Equals("api-key", StringComparison.OrdinalIgnoreCase)
            && h.Value.Contains("k"));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<(string Key, string Value)> LastRequestHeaders { get; } = [];

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestHeaders.Clear();
            foreach (var h in request.Headers)
                LastRequestHeaders.Add((h.Key, string.Join(",", h.Value)));
            return Task.FromResult(_respond(request));
        }
    }
}
