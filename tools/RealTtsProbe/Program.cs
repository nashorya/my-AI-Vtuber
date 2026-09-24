// One-shot real-endpoint probe for the RT-01 MiniMax HTTP streaming TTS client.
// Usage: dotnet run --project tools/RealTtsProbe -- <path-to-config.json> [text]
// Reads the tts section (key/voice/model) from the given config.json; the transport
// used is always "streaming" regardless of the config value, so the legacy path is
// never accidentally exercised here. Result PCM is written to probe-output.pcm.
using System.Diagnostics;
using System.Text.Json;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

var configPath = args.Length > 0 ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "config.json");
var text = args.Length > 1 ? args[1] : "你好，这是一次流式合成测试。";

var json = JsonDocument.Parse(File.ReadAllText(configPath)).RootElement;
var tts = json.GetProperty("tts");
var cfg = new TtsConfig
{
    Provider = "minimax",
    ApiKey = tts.GetProperty("api_key").GetString() ?? "",
    VoiceId = tts.GetProperty("voice_id").GetString() ?? "",
    Model = tts.GetProperty("model").GetString() ?? "",
    BaseUrl = tts.GetProperty("base_url").GetString() ?? "",
    SampleRate = tts.GetProperty("sample_rate").GetInt32(),
    Transport = "streaming",
};
Console.WriteLine($"model={cfg.Model} voice={cfg.VoiceId[..8]}... base_url={cfg.BaseUrl} rate={cfg.SampleRate}");

using var client = new MiniMaxHttpStreamingTtsClient(cfg);
var sw = Stopwatch.StartNew();
var firstMs = -1L; var totalBytes = 0L; var chunks = 0;
await using var outFs = File.Create(Path.Combine(AppContext.BaseDirectory, "probe-output.pcm"));
await foreach (var pcm in client.StreamAsync(text, cfg.VoiceId, null))
{
    if (firstMs < 0) { firstMs = sw.ElapsedMilliseconds; Console.WriteLine($"first PCM chunk at {firstMs} ms ({pcm.Length} bytes)"); }
    chunks++; totalBytes += pcm.Length;
    await outFs.WriteAsync(pcm);
}
Console.WriteLine($"done: chunks={chunks} totalPCM={totalBytes} bytes elapsed={sw.ElapsedMilliseconds} ms");
Console.WriteLine(firstMs >= 0 && totalBytes > 0 ? "RESULT: streaming OK" : "RESULT: no audio received");
