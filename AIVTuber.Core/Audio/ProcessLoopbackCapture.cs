using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.Wave;

#pragma warning disable CA1416 // Windows-only COM APIs — this binary targets Windows only

namespace AIVTuber.Core.Audio;

/// <summary>
/// Captures audio from a specific process on Windows 10 2004+ / Windows 11.
/// Uses the Windows Process Loopback Capture API — no virtual sound card required.
/// Specify a process name (e.g. "chrome", "msedge", "obs64") in config.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    // Virtual device for process loopback (mmdeviceapi.h: VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK)
    private const string VirtualDevice = "{2eef81be-33fa-4800-9670-1cd474972c3f}";

    private readonly string? _processName;
    private readonly uint? _fixedPid;
    private readonly WasapiNative.PROCESS_LOOPBACK_MODE _loopbackMode;
    private readonly int _targetSampleRate;
    private volatile bool _running;
    private Thread? _thread;
    private bool _disposed;

    public event EventHandler<byte[]>? AudioFrameAvailable;
    /// <summary>Fired per frame with RMS in [0, 1]. Use for a level indicator.</summary>
    public event EventHandler<float>? LevelUpdated;
    public event EventHandler<Exception>? ErrorOccurred;

    public ProcessLoopbackCapture(string processName, int targetSampleRate = 16000)
    {
        _processName = processName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        _loopbackMode = WasapiNative.PROCESS_LOOPBACK_MODE.INCLUDE_TARGET_PROCESS_TREE;
        _targetSampleRate = targetSampleRate;
    }

    /// <summary>
    /// Creates a loopback capture that records ALL system audio except this process's output,
    /// preventing the AI from hearing its own TTS speech.
    /// </summary>
    public static ProcessLoopbackCapture CreateExcludingSelf(int targetSampleRate = 16000)
        => new(targetSampleRate);

    private ProcessLoopbackCapture(int targetSampleRate)
    {
        _fixedPid = (uint)Environment.ProcessId;
        _loopbackMode = WasapiNative.PROCESS_LOOPBACK_MODE.EXCLUDE_TARGET_PROCESS_TREE;
        _targetSampleRate = targetSampleRate;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _running = true;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "ProcessLoopback" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(3000);
        _thread = null;
    }

    private void CaptureLoop()
    {
        while (_running)
        {
            try
            {
                uint pid;
                if (_fixedPid.HasValue)
                {
                    pid = _fixedPid.Value;
                }
                else
                {
                    var procs = Process.GetProcessesByName(_processName!);
                    if (procs.Length == 0) { Thread.Sleep(2000); continue; }
                    pid = (uint)procs[0].Id;
                    foreach (var p in procs) p.Dispose();
                }

                CaptureFromPid(pid);
            }
            catch (Exception ex) when (_running)
            {
                ErrorOccurred?.Invoke(this, ex);
                Thread.Sleep(1000);
            }
        }
    }

    private void CaptureFromPid(uint pid)
    {
        // Build activation params in a pinned GCHandle so the pointer stays valid
        var activationParams = new WasapiNative.AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = WasapiNative.AUDIOCLIENT_ACTIVATION_TYPE.PROCESS_LOOPBACK,
            ProcessLoopbackParams = new WasapiNative.AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
            {
                TargetProcessId = pid,
                ProcessLoopbackMode = _loopbackMode,
            }
        };

        var handle = GCHandle.Alloc(activationParams, GCHandleType.Pinned);
        try
        {
            var propvar = new WasapiNative.PROPVARIANT_BLOB
            {
                vt = 65, // VT_BLOB
                cbSize = (uint)Marshal.SizeOf<WasapiNative.AUDIOCLIENT_ACTIVATION_PARAMS>(),
                pBlobData = handle.AddrOfPinnedObject(),
            };

            var completion = new ActivationCompletion();
            int hr = WasapiNative.ActivateAudioInterfaceAsync(
                VirtualDevice, ref WasapiNative.IID_IAudioClient, ref propvar, completion, out _);
            Marshal.ThrowExceptionForHR(hr);

            if (!completion.CompletionEvent.WaitOne(5000))
                throw new TimeoutException("Process audio activation timed out");
            Marshal.ThrowExceptionForHR(completion.HResult);

            var audioClient = completion.AudioClient
                ?? throw new InvalidOperationException("AudioClient activation returned null");

            // GetMixFormat returns a CoTaskMem pointer we must free after use
            Marshal.ThrowExceptionForHR(audioClient.GetMixFormat(out IntPtr fmtPtr));
            WaveFormat sourceFormat;
            try
            {
                sourceFormat = WaveFormat.MarshalFromPtr(fmtPtr);
                // Initialize in shared mode with loopback flag
                Marshal.ThrowExceptionForHR(audioClient.Initialize(
                    0,           // AUDCLNT_SHAREMODE_SHARED
                    0x00020000,  // AUDCLNT_STREAMFLAGS_LOOPBACK
                    100_000_000, // 10-second buffer in 100-ns units
                    0, fmtPtr, Guid.Empty));
            }
            finally { Marshal.FreeCoTaskMem(fmtPtr); }

            Marshal.ThrowExceptionForHR(audioClient.GetService(
                ref WasapiNative.IID_IAudioCaptureClient, out IntPtr capturePtr));
            var captureClient = (WasapiNative.IAudioCaptureClient)Marshal.GetObjectForIUnknown(capturePtr);
            Marshal.Release(capturePtr);

            // Resample device format → 16kHz mono 16-bit for VAD/ASR
            var targetFormat = new WaveFormat(_targetSampleRate, 16, 1);
            var buffered = new BufferedWaveProvider(sourceFormat)
            {
                BufferLength = sourceFormat.AverageBytesPerSecond * 5,
                DiscardOnBufferOverflow = true,
                ReadFully = false,
            };
            using var resampler = new MediaFoundationResampler(buffered, targetFormat);
            int frameBytes = _targetSampleRate * 2 * 30 / 1000; // 30 ms chunks

            Marshal.ThrowExceptionForHR(audioClient.Start());

            while (_running)
            {
                Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out uint packetSize));
                while (packetSize > 0 && _running)
                {
                    Marshal.ThrowExceptionForHR(captureClient.GetBuffer(
                        out IntPtr data, out uint frames, out uint flags, out _, out _));

                    if (frames > 0)
                    {
                        int bytes = (int)(frames * (uint)sourceFormat.BlockAlign);
                        var raw = new byte[bytes];
                        bool silent = (flags & 2) != 0; // AUDCLNT_BUFFERFLAGS_SILENT
                        if (!silent) Marshal.Copy(data, raw, 0, bytes);
                        buffered.AddSamples(raw, 0, bytes);
                    }
                    Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(frames));
                    Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out packetSize));
                }

                while (buffered.BufferedBytes >= frameBytes)
                {
                    var chunk = new byte[frameBytes];
                    int read = resampler.Read(chunk, 0, frameBytes);
                    if (read <= 0) continue;
                    var frame = chunk[..read];
                    AudioFrameAvailable?.Invoke(this, frame);
                    if (LevelUpdated is not null)
                    {
                        int samples = read / 2;
                        double sum = 0;
                        for (int i = 0; i < samples; i++)
                        {
                            float s = BitConverter.ToInt16(frame, i * 2) / 32768f;
                            sum += s * s;
                        }
                        LevelUpdated.Invoke(this, (float)Math.Sqrt(sum / Math.Max(samples, 1)));
                    }
                }

                Thread.Sleep(10);
            }

            audioClient.Stop();
            Marshal.ReleaseComObject(captureClient);
            Marshal.ReleaseComObject(audioClient);
        }
        finally { handle.Free(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}

// ── COM completion handler ────────────────────────────────────────────────────

internal sealed class ActivationCompletion : WasapiNative.IActivateAudioInterfaceCompletionHandler
{
    public readonly ManualResetEvent CompletionEvent = new(false);
    public int HResult { get; private set; }
    public WasapiNative.IAudioClient? AudioClient { get; private set; }

    public void ActivateCompleted(WasapiNative.IActivateAudioInterfaceAsyncOperation op)
    {
        op.GetActivateResult(out int hr, out object? iface);
        HResult = hr;
        if (hr >= 0) AudioClient = iface as WasapiNative.IAudioClient;
        CompletionEvent.Set();
    }
}

// ── P/Invoke + COM definitions ────────────────────────────────────────────────

internal static class WasapiNative
{
    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PROPVARIANT_BLOB activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    public static Guid IID_IAudioClient        = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    // ── Structs ───────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT_BLOB
    {
        public ushort vt;
        public ushort wReserved1, wReserved2, wReserved3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    public enum AUDIOCLIENT_ACTIVATION_TYPE { DEFAULT = 0, PROCESS_LOOPBACK = 1 }
    public enum PROCESS_LOOPBACK_MODE { INCLUDE_TARGET_PROCESS_TREE = 0, EXCLUDE_TARGET_PROCESS_TREE = 1 }

    [StructLayout(LayoutKind.Sequential)]
    public struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    // ── COM interfaces ────────────────────────────────────────────────────────

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult([MarshalAs(UnmanagedType.Error)] out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object? activateInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int ShareMode, uint StreamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, Guid AudioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        [PreserveSig] int IsFormatSupported(int ShareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        [PreserveSig] int ReleaseBuffer(uint NumFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }
}
