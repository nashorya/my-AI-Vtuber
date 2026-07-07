using System.Text;

namespace AIVTuber.Core.Diagnostics;

/// <summary>
/// Minimal thread-safe append logger for diagnosing the audio pipeline.
/// Writes to audio_debug.log next to the executable. Enabled via DebugLog.Enabled.
/// </summary>
public static class DebugLog
{
    private static readonly object _lock = new();
    private static readonly string _path =
        Path.Combine(AppContext.BaseDirectory, "audio_debug.log");

    /// <summary>When false, Write is a no-op (near-zero overhead).</summary>
    public static bool Enabled { get; set; } = true;

    public static void Write(string line)
    {
        if (!Enabled) return;
        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        lock (_lock)
        {
            try { File.AppendAllText(_path, $"{stamp}  {line}{Environment.NewLine}", Encoding.UTF8); }
            catch { /* logging must never break the pipeline */ }
        }
    }

    /// <summary>Peak absolute amplitude (0..1) of a 16-bit mono PCM buffer — for level evidence.</summary>
    public static float PeakRms(byte[] pcm16)
    {
        int samples = pcm16.Length / 2;
        if (samples == 0) return 0f;
        short peak = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = BitConverter.ToInt16(pcm16, i * 2);
            short a = s == short.MinValue ? short.MaxValue : Math.Abs(s);
            if (a > peak) peak = a;
        }
        return peak / 32768f;
    }
}
