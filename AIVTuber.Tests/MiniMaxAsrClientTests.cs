using System.Net;
using System.Net.Http.Headers;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public class MiniMaxAsrClientTests
{
    [Fact]
    public async Task RecognizeAsync_PostsWavToSpeechToText()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK,
            """{"text":"喂喂喂，大肥鱼。","duration":1.2}""");
        using var client = new MiniMaxAsrClient("sk-test", "asr-1.0", new HttpClient(handler));

        var result = await client.RecognizeAsync(new byte[3200]);

        Assert.Equal("喂喂喂，大肥鱼。", result.Text);
        Assert.Equal("https://api.minimaxi.com/v1/speech_to_text", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", handler.Request.Headers.Authorization.Parameter);
        Assert.True(handler.Request.Headers.TryGetValues("language", out var langs));
        Assert.Equal("zh", langs.Single());
        Assert.Contains(handler.FormKeys, k => k == "model");
        Assert.Contains(handler.FormKeys, k => k == "file");
        Assert.Equal("asr-1.0", handler.Form["model"]);
        Assert.True(handler.File.Length > 44);
        Assert.Equal((byte)'R', handler.File[0]);
    }

    [Fact]
    public void NormalizeModel_DefaultsToAsr10()
    {
        Assert.Equal("asr-1.0", MiniMaxAsrClient.NormalizeModel(null));
        Assert.Equal("asr-1.0", MiniMaxAsrClient.NormalizeModel("  "));
        Assert.Equal("asr-1.0", MiniMaxAsrClient.NormalizeModel("asr-1.0"));
        Assert.Equal("asr-1.0", MiniMaxAsrClient.NormalizeModel("Qwen/Qwen3-ASR-0.6B"));
    }

    [Fact]
    public async Task RecognizeAsync_EmptyPcm_DoesNotCallApi()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, """{"text":"nope"}""");
        using var client = new MiniMaxAsrClient("sk-test", "asr-1.0", new HttpClient(handler));

        var result = await client.RecognizeAsync([]);

        Assert.Equal("", result.Text);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task RecognizeAsync_ErrorIncludesBody()
    {
        var handler = new CaptureHandler(HttpStatusCode.Unauthorized,
            """{"type":"error","error":{"message":"login fail (1004)"}}""");
        using var client = new MiniMaxAsrClient("bad", "asr-1.0", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.RecognizeAsync(new byte[64]));
        Assert.Contains("1004", ex.Message);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    private sealed class CaptureHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public List<string> FormKeys { get; } = [];
        public Dictionary<string, string> Form { get; } = new();
        public byte[] File { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is MultipartFormDataContent multi)
            {
                foreach (var part in multi)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"') ?? "";
                    FormKeys.Add(name);
                    if (name == "file")
                        File = await part.ReadAsByteArrayAsync(cancellationToken);
                    else
                        Form[name] = await part.ReadAsStringAsync(cancellationToken);
                }
            }

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, new MediaTypeHeaderValue("application/json")),
            };
        }
    }
}
