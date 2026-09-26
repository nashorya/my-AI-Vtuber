// Lists capture devices the same way MicrophoneCapture resolves input_device_index.
using NAudio.CoreAudioApi;

Console.WriteLine("-- WASAPI capture endpoints --");
var capture = new MMDeviceEnumerator();
int i = 0;
foreach (var d in capture.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
{
    Console.WriteLine($"{i}: {d.FriendlyName}  (default={d.ID == capture.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID})");
    i++;
}
Console.WriteLine("-- WaveIn devices --");
for (int w = 0; w < NAudio.Wave.WaveInEvent.DeviceCount; w++)
    Console.WriteLine($"WaveIn {w}: {NAudio.Wave.WaveInEvent.GetCapabilities(w).ProductName}");
