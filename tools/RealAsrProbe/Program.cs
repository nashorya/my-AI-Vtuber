// One-shot real-endpoint probe for MiniMaxAsrClient (POST /v1/speech_to_text).
// Usage: dotnet run --project tools/RealAsrProbe -- <config.json> <sample.wav>
using System.Diagnostics;
using AIVTuber.Core.Pipeline;

var configPath = args.Length > 0 ? args[0] : "config.json";
var wavPath = args.Length > 1 ? args[1] : "test_asr_sample.wav";

using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
var asr = doc.RootElement.GetProperty("asr");
var key = asr.GetProperty("api_key").GetString() ?? "";
var model = asr.GetProperty("model").GetString();

using var client = new MiniMaxAsrClient(key, model);
// Feed the WAV bytes via the same 16k path the pipeline uses.
using var fs = File.OpenRead(wavPath);
using var ms = new MemoryStream();
fs.CopyTo(ms);
// Downmix/resample to 16k mono PCM16 like MicrophoneCapture provides.
var pcm = ResampleTo16k(ms.ToArray());
var sw = Stopwatch.StartNew();
var result = await client.RecognizeAsync(pcm);
Console.WriteLine($"elapsed={sw.ElapsedMilliseconds} ms");
Console.WriteLine($"text=\"{result.Text}\"");

static byte[] ResampleTo16k(byte[] wav)
{
    using var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(wav));
    var outFormat = new NAudio.Wave.WaveFormat(16000, 16, 1);
    using var converter = new NAudio.Wave.MediaFoundationResampler(reader, outFormat);
    using var outMs = new MemoryStream();
    byte[] buffer = new byte[16384];
    int read;
    while ((read = converter.Read(buffer, 0, buffer.Length)) > 0)
        outMs.Write(buffer, 0, read);
    return outMs.ToArray();
}
