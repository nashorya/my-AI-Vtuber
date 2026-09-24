// Single-shot mic probe: sets the target WASAPI capture device volume to max,
// records 6s via WaveIn (same path as MicrophoneCapture), reports peak,
// saves mic-probe.wav, ASRs it if loud enough.
using System.Diagnostics;
using AIVTuber.Core.Pipeline;
using NAudio.CoreAudioApi;
using NAudio.Wave;

var configPath = args.Length > 0 ? args[0] : "config.json";
int idx = args.Length > 1 ? int.Parse(args[1]) : 0;

// max out volume for every active capture endpoint matching the WaveIn product name
var wanted = WaveInEvent.GetCapabilities(idx).ProductName;
Console.WriteLine($"WaveIn device {idx}: {wanted}");
var enumr = new MMDeviceEnumerator();
foreach (var d in enumr.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
{
    if (d.FriendlyName.Contains(wanted.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
    {
        d.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0f; d.AudioEndpointVolume.Mute = false;
        Console.WriteLine($"set WASAPI volume 100% for: {d.FriendlyName}");
    }
}

var format = new WaveFormat(16000, 16, 1);
using var waveIn = new WaveInEvent { DeviceNumber = idx, WaveFormat = format, BufferMilliseconds = 50 };
var data = new MemoryStream(); var peak = 0f;
waveIn.DataAvailable += (_, e) =>
{
    for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
    {
        var s = Math.Abs((short)(e.Buffer[i] | (e.Buffer[i + 1] << 8))) / 32768f;
        if (s > peak) peak = s;
    }
    data.Write(e.Buffer, 0, e.BytesRecorded);
};
waveIn.StartRecording();
Console.WriteLine("recording 25s ... SPEAK NOW");
await Task.Delay(10000);
waveIn.StopRecording();
var pcm = data.ToArray();
Console.WriteLine($"{pcm.Length / 32000.0:F1}s peak={peak:F3}");
using (var fs = new FileStream("mic-probe.wav", FileMode.Create))
using (var w = new WaveFileWriter(fs, format)) w.Write(pcm, 0, pcm.Length);
if (peak >= 0.03)
{
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
    var asr = doc.RootElement.GetProperty("asr");
    using var client = new MiniMaxAsrClient(asr.GetProperty("api_key").GetString(), asr.GetProperty("model").GetString());
    var sw = Stopwatch.StartNew();
    var r = await client.RecognizeAsync(pcm);
    Console.WriteLine($"ASR({sw.ElapsedMilliseconds}ms): \"{r.Text}\"");
}
else Console.WriteLine("still below 0.03");
