using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AIVTuber.Core.Audio;

/// <summary>
/// Mixes the real microphone (16kHz mono 16-bit) and AI TTS output (24kHz mono 16-bit) and
/// writes the combined signal to a virtual render device (e.g. VB-Cable "CABLE Input").
/// Streaming software (e.g. 直播姬) should use the corresponding capture device ("CABLE Output")
/// as its microphone input to receive both voices simultaneously.
///
/// Usage:
///   var mixer = new VirtualMicMixer("CABLE Input (VB-Audio Virtual Cable)");
///   mixer.Start();
///   // from MicrophoneCapture.AudioFrameAvailable:
///   mixer.WriteMic(pcm16kHz);
///   // from AudioPlayer.PcmChunkPlayed:
///   mixer.WriteTts(pcm24kHz);
///   mixer.Dispose();
/// </summary>
public sealed class VirtualMicMixer : IDisposable
{
    private readonly string? _deviceName;
    private WasapiOut? _wasapiOut;
    private MixingSampleProvider? _mixer;
    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _ttsBuffer;
    private bool _disposed;

    public VirtualMicMixer(string? deviceName = null) => _deviceName = deviceName;

    /// <summary>Lists friendly names of all active render (output) devices.</summary>
    public static IReadOnlyList<string> ListRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var list = new List<string>();
        foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            list.Add(dev.FriendlyName);
            dev.Dispose();
        }
        return list;
    }

    /// <summary>Initialises the mixing chain and starts writing to the virtual device.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var enumerator = new MMDeviceEnumerator();
        MMDevice device = FindDevice(enumerator);

        // Query the device's preferred mix format (usually 48kHz float stereo on WASAPI shared).
        var mixFormat = device.AudioClient.MixFormat;
        int mixRate = mixFormat.SampleRate;
        int mixChannels = mixFormat.Channels;

        // Build the two input chains: raw PCM → float sample provider at mixRate/mixChannels.
        _micBuffer = new BufferedWaveProvider(new WaveFormat(16000, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true,
            ReadFully = true,   // output silence when empty — keeps the mix stream continuous
        };
        _ttsBuffer = new BufferedWaveProvider(new WaveFormat(AudioPlayer.DefaultSampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };

        ISampleProvider micChain = BuildChain(_micBuffer, mixRate, mixChannels);
        ISampleProvider ttsChain = BuildChain(_ttsBuffer, mixRate, mixChannels);

        _mixer = new MixingSampleProvider(new[] { micChain, ttsChain })
        {
            ReadFully = true,
        };

        _wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
        _wasapiOut.Init(_mixer);
        _wasapiOut.Play();
    }

    /// <summary>Feed a raw 16kHz mono 16-bit PCM chunk from the real microphone.</summary>
    public void WriteMic(byte[] pcm)
    {
        if (_disposed || _micBuffer is null) return;
        _micBuffer.AddSamples(pcm, 0, pcm.Length);
    }

    /// <summary>Feed a raw 24kHz mono 16-bit PCM chunk from the TTS engine.</summary>
    public void WriteTts(byte[] pcm)
    {
        if (_disposed || _ttsBuffer is null) return;
        _ttsBuffer.AddSamples(pcm, 0, pcm.Length);
    }

    public void Stop()
    {
        _wasapiOut?.Stop();
        _wasapiOut?.Dispose();
        _wasapiOut = null;
        _mixer = null;
        _micBuffer = null;
        _ttsBuffer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private MMDevice FindDevice(MMDeviceEnumerator enumerator)
    {
        if (!string.IsNullOrWhiteSpace(_deviceName))
        {
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (d.FriendlyName.Contains(_deviceName, StringComparison.OrdinalIgnoreCase))
                    return d;
                d.Dispose();
            }
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    /// <summary>
    /// Converts a 16-bit mono BufferedWaveProvider at its native sample rate to a float
    /// sample provider at <paramref name="targetRate"/>/<paramref name="targetChannels"/>.
    /// </summary>
    private static ISampleProvider BuildChain(BufferedWaveProvider source, int targetRate, int targetChannels)
    {
        ISampleProvider sp = source.ToSampleProvider();

        // Resample to device rate if needed
        if (source.WaveFormat.SampleRate != targetRate)
            sp = new WdlResamplingSampleProvider(sp, targetRate);

        // Upmix mono → stereo if needed
        if (targetChannels == 2 && sp.WaveFormat.Channels == 1)
            sp = new MonoToStereoSampleProvider(sp);

        return sp;
    }
}
