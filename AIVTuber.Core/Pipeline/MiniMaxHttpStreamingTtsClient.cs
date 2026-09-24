using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIVTuber.Core.Config;
using NAudio.Wave;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// MiniMax TTS over HTTP streaming (t2a_v2 with stream=true) — RT-01.
/// Requests PCM16 explicitly; if the service answers with SSE, events are parsed incrementally
/// (never ReadAsStringAsync on the streaming path) and each event's encoded audio is decoded and
/// yielded as soon as it arrives, so the first PCM chunk can enter the player while the HTTP
/// response is still open. If the service answers with a non-SSE media type (plain JSON fallback
/// or raw audio bytes), the body is handled accordingly.
/// Real-endpoint behavior is UNVERIFIED (no API key available); parsing is defensive and pinned
/// by fake-transport tests in AIVTuber.Tests.
/// Select via tts.transport = "streaming" (default "legacy" keeps the previous routing unchanged).
/// </summary>
public sealed class MiniMaxHttpStreamingTtsClient : ITtsClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TtsConfig _config;
    private readonly bool _ownsClient;

    /// <summary>Timing hook for first-packet instrumentation (RT-00 tracing can subscribe later;
    /// no logging infrastructure is introduced on this branch).</summary>
    public TtsStreamingTelemetry Telemetry { get; } = new();

    public MiniMaxHttpStreamingTtsClient(TtsConfig config)
        : this(config, new HttpClientHandler(), ownsClient: true)
    {
    }

    /// <summary>Fake-transport constructor used by tests: injects a stub HttpMessageHandler.</summary>
    internal MiniMaxHttpStreamingTtsClient(TtsConfig config, HttpMessageHandler handler, bool ownsClient = true)
    {
        _config = config;
        _ownsClient = ownsClient;
        _httpClient = new HttpClient(handler, disposeHandler: ownsClient)
        {
            // The body is consumed incrementally; Timeout only guards the whole request here
            // because HttpClient's timeout applies per-request — cancellation flows via tokens.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async IAsyncEnumerable<byte[]> StreamAsync(
        string text,
        string voiceId,
        string? emotion,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(_config.Model) ? "speech-2.8-hd" : _config.Model;
        var miniMaxEmotion = MapEmotion(emotion);
        var json = BuildStreamingRequestJson(text, voiceId, model, _config.Speed, _config.SampleRate, miniMaxEmotion);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.minimaxi.com/v1/t2a_v2")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";

        if (contentType.Contains("event-stream"))
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await foreach (var pcm in StreamSseAsync(stream, cancellationToken))
                yield return pcm;
        }
        else if (contentType.Contains("json"))
        {
            // Service ignored stream=true and answered with one JSON document: explicit fallback,
            // same parsing rules as the legacy non-streaming implementation.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var audio = TtsClient.ParseMiniMaxAudio(body);
            if (audio.Length > 0)
            {
                Telemetry.MarkFirstEncodedAudio();
                Telemetry.MarkFirstPcm();
                yield return audio;
            }
        }
        else
        {
            // Raw audio media type (e.g. audio/mpeg, audio/L16, application/octet-stream):
            // incremental block reads through the same decoder pipeline.
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await foreach (var pcm in StreamRawAudioAsync(stream, contentType, cancellationToken))
                yield return pcm;
        }
    }

    private async IAsyncEnumerable<byte[]> StreamSseAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? audioFormat = null;          // per-event declared format; first non-null wins
        IAudioDecoder? decoder = null;
        var aligner = new PcmAligner();
        var yielded = new List<byte[]>();    // for aggregate-replay detection on the final event

        await foreach (var sseEvent in SseEventReader.ReadAsync(stream, cancellationToken))
        {            // Keepalive comments never dispatch events; [DONE], JSON null and empty data carry nothing to play.
            if (sseEvent.Data.Trim() is "" or "[DONE]" or "[EOF]" or "null")
                continue;

            MiniMaxStreamAudio chunk;
            try
            {
                chunk = ParseSseAudioPayload(sseEvent.Data);
            }
            catch (JsonException)
            {
                continue; // a partial/unknown JSON event must not kill the stream mid-sentence
            }

            if (chunk.Error is { } err)
                throw new InvalidOperationException($"MiniMax TTS stream error {err.Code}: {err.Message}");

            if (chunk.Audio is not { Length: > 0 } encoded)
            {
                if (chunk.IsFinal) yield break; // final with no audio: done, nothing lost
                continue;
            }

            Telemetry.MarkFirstEncodedAudio();

            audioFormat ??= chunk.AudioFormat;
            decoder ??= CreateDecoder(audioFormat, _config.SampleRate);

            // Aggregate-replay guard: if the final event repeats the concatenation of everything
            // already yielded, it is a replay — skip it (no复读). Any other final audio is a
            // genuine tail and must not be dropped (不能丢 final).
            if (chunk.IsFinal && IsReplayOf(yielded, encoded))
                yield break;

            foreach (var raw in decoder.Decode(encoded))
            {
                if (raw.Length == 0) continue;
                Telemetry.MarkFirstPcm();
                foreach (var aligned in aligner.Push(raw))
                {
                    yielded.Add(aligned);
                    yield return aligned;
                }
            }

            if (chunk.IsFinal)
            {
                foreach (var tail in aligner.Flush())
                    yield return tail;
                yield break;
            }
        }
    }

    private async IAsyncEnumerable<byte[]> StreamRawAudioAsync(
        Stream stream, string contentType, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var decoder = CreateDecoder(
            contentType.Contains("mpeg") ? "mp3" : "pcm", _config.SampleRate);
        var aligner = new PcmAligner();
        var buffer = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0) break;
            Telemetry.MarkFirstEncodedAudio();
            var copy = new byte[read];
            Array.Copy(buffer, copy, read);
            foreach (var raw in decoder.Decode(copy))
            {
                if (raw.Length == 0) continue;
                Telemetry.MarkFirstPcm();
                foreach (var aligned in aligner.Push(raw))
                    yield return aligned;
            }
        }
        foreach (var tail in aligner.Flush())
            yield return tail;
    }

    private static bool IsReplayOf(IReadOnlyList<byte[]> yielded, byte[] candidate)
    {
        long total = 0;
        foreach (var y in yielded) total += y.Length;
        if (total != candidate.Length) return false;
        int pos = 0;
        foreach (var y in yielded)
        {
            if (!y.AsSpan(0, y.Length).SequenceEqual(candidate.AsSpan(pos, y.Length))) return false;
            pos += y.Length;
        }
        return true;
    }

    private static IAudioDecoder CreateDecoder(string? format, int targetSampleRate) =>
        format is not null && format.Contains("mp3", StringComparison.OrdinalIgnoreCase)
            ? new Mp3StreamingDecoder(targetSampleRate)
            : PassthroughDecoder.Instance;

    private static string? MapEmotion(string? emotion) => emotion?.ToLowerInvariant() switch
    {
        "happy" => "happy", "sad" => "sad", "angry" => "angry",
        "fearful" => "fearful", "disgusted" => "disgusted", "surprised" => "surprised",
        "calm" => "calm", "neutral" => "calm", "whisper" => "whisper",
        _ => null,
    };

    /// <summary>Builds the t2a_v2 request body with stream=true (PCM16 pinned at the configured rate).</summary>
    internal static string BuildStreamingRequestJson(
        string text, string voiceId, string model, double speed, int sampleRate, string? emotion = null)
    {
        object voiceSetting = emotion is null
            ? new { voice_id = voiceId, speed, vol = 1.0, pitch = 0 }
            : new { voice_id = voiceId, speed, vol = 1.0, pitch = 0, emotion };

        return JsonSerializer.Serialize(new
        {
            model,
            text,
            stream = true,
            voice_setting = voiceSetting,
            audio_setting = new { sample_rate = sampleRate, format = "pcm", channel = 1 },
        }, JsonOptions);
    }

    /// <summary>Parses one SSE data payload into (audio bytes, declared format, final flag, error).
    /// Handles hex (documented transport) and falls back to base64 when the string is not valid hex.
    /// Never throws for unknown shapes; returns empty audio instead.</summary>
    internal static MiniMaxStreamAudio ParseSseAudioPayload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Null)
            return default;

        if (root.TryGetProperty("base_resp", out var baseResp) &&
            baseResp.TryGetProperty("status_code", out var codeEl) &&
            codeEl.GetInt32() != 0)
        {
            var msg = baseResp.TryGetProperty("status_msg", out var m) ? m.GetString() : "unknown error";
            return new MiniMaxStreamAudio { Error = (codeEl.GetInt32(), msg) };
        }

        var result = new MiniMaxStreamAudio();
        if (root.TryGetProperty("is_final", out var isFinal) && isFinal.ValueKind == JsonValueKind.True)
            result.IsFinal = true;
        // Some builds attach extra_info (audio length, usage) to the last event instead of is_final.
        if (!result.IsFinal && root.TryGetProperty("extra_info", out var extra) &&
            extra.ValueKind is JsonValueKind.Object or JsonValueKind.True)
            result.IsFinal = true;

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("audio_format", out var fmtEl) && fmtEl.GetString() is { Length: > 0 } fmt)
                result.AudioFormat = fmt;
            if (data.TryGetProperty("audio", out var audioEl) && audioEl.GetString() is { Length: > 0 } encoded)
                result.Audio = DecodeAudioString(encoded);
        }

        return result;
    }

    private static byte[] DecodeAudioString(string s)
    {
        try
        {
            return Convert.FromHexString(s);
        }
        catch (FormatException)
        {
            try
            {
                return Convert.FromBase64String(s);
            }
            catch (FormatException)
            {
                return [];
            }
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Lightweight timing hook for RT-00 tracing (no logging infrastructure on this branch).</summary>
public sealed class TtsStreamingTelemetry
{
    private long? _firstEncodedAudio;
    private long? _firstPcm;

    /// <summary>Stopwatch timestamp of the first encoded (vendor representation) audio chunk.</summary>
    public long? FirstEncodedAudioTimestamp => _firstEncodedAudio;
    /// <summary>Stopwatch timestamp of the first decoded PCM chunk handed to the caller.</summary>
    public long? FirstPcmTimestamp => _firstPcm;

    internal void MarkFirstEncodedAudio() => _firstEncodedAudio ??= Stopwatch.GetTimestamp();
    internal void MarkFirstPcm() => _firstPcm ??= Stopwatch.GetTimestamp();
}

internal record struct MiniMaxStreamAudio
{
    public byte[]? Audio { get; set; }
    public string? AudioFormat { get; set; }
    public bool IsFinal { get; set; }
    public (int Code, string? Message)? Error { get; set; }
}

/// <summary>Reads complete SSE events from a raw byte stream. Lines are split only on complete
/// LF bytes (LF is never part of a UTF-8 multi-byte sequence), so characters spanning network
/// chunk boundaries decode safely. Handles blank-line dispatch, ':' keepalive comments,
/// event names, multi-line data and a trailing event without final newline.</summary>
internal static class SseEventReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pending = new List<byte>();
        string? eventName = null;
        var dataLines = new List<string>();
        var buffer = new byte[8192];

        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);            if (read == 0) break;

            int start = 0;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;

                int lineLen = i - start;
                if (lineLen > 0 && buffer[start + lineLen - 1] == (byte)'\r') lineLen--;

                pending.AddRange(buffer.AsSpan(start, lineLen));
                var line = Encoding.UTF8.GetString(pending.ToArray());
                pending.Clear();
                if (line.Length > 0 && line[^1] == '\r') line = line[..^1];

                if (TryDispatchLine(line, dataLines, ref eventName, out var evt) && evt is not null)
                    yield return evt;

                start = i + 1;
            }
            pending.AddRange(buffer.AsSpan(start, read - start));
        }

        // Stream ended mid-line: dispatch the remainder.
        if (pending.Count > 0)
        {
            var line = Encoding.UTF8.GetString(pending.ToArray());
            pending.Clear();
            if (TryDispatchLine(line, dataLines, ref eventName, out var evt) && evt is not null)
                yield return evt;
        }
        if (dataLines.Count > 0)
            yield return new SseEvent(eventName, string.Join("\n", dataLines));
    }

    private static bool TryDispatchLine(
        string line, List<string> dataLines, ref string? eventName, out SseEvent? evt)
    {
        evt = null;
        if (line.Length == 0)
        {
            if (dataLines.Count == 0 && eventName is null) return false;
            evt = new SseEvent(eventName, string.Join("\n", dataLines));
            dataLines.Clear();
            eventName = null;
            return true;
        }
        if (line[0] == ':') return false; // comment / keepalive
        if (line.StartsWith("data:", StringComparison.Ordinal))
        {
            var value = line[5..].TrimStart();
            dataLines.Add(value);
        }
        else if (line.StartsWith("event:", StringComparison.Ordinal))
        {
            eventName = line[6..].TrimStart();
        }
        // Other fields (id:, retry:) are ignored.
        return false;
    }
}

internal sealed record SseEvent(string? EventName, string Data);

/// <summary>Keeps PCM output 16-bit sample aligned: an odd trailing byte is held back until the
/// next chunk completes it. Flush drops an unterminated byte (it cannot be played).</summary>
internal sealed class PcmAligner
{
    private byte? _carry;

    public IEnumerable<byte[]> Push(byte[] chunk)
    {
        if (chunk.Length == 0) yield break;

        byte[] data = chunk;
        if (_carry is { } c)
        {
            data = new byte[1 + chunk.Length];
            data[0] = c;
            Array.Copy(chunk, 0, data, 1, chunk.Length);
            _carry = null;
        }

        if (data.Length % 2 == 1)
        {
            _carry = data[^1];
            if (data.Length > 1)
                yield return data[..^1];
        }
        else
        {
            yield return data;
        }
    }

    public IEnumerable<byte[]> Flush()
    {
        // A lone unpaired byte is not playable: drop it (documented and tested).
        _carry = null;
        yield break;
    }
}

internal interface IAudioDecoder
{
    /// <summary>Feeds encoded bytes, yields decoded PCM16 mono chunks at the target sample rate.</summary>
    IEnumerable<byte[]> Decode(byte[] encoded);
}

internal sealed class PassthroughDecoder : IAudioDecoder
{
    public static readonly PassthroughDecoder Instance = new();
    public IEnumerable<byte[]> Decode(byte[] encoded)
    {
        if (encoded.Length > 0) yield return encoded;
    }
}

/// <summary>Incremental MP3 decoder: NAudio frame parsing + ACM decompressor. MP3 bytes are
/// consumed as they arrive and PCM is returned immediately — never waits for the full file.
/// Defensive path only (we request PCM; this covers a server that answers MP3 anyway, taking
/// data.audio_format as the authority). UNVERIFIED against the real endpoint.</summary>
internal sealed class Mp3StreamingDecoder : IAudioDecoder
{
    private readonly int _targetSampleRate;
    private MemoryStream _buffer = new();
    private IMp3FrameDecompressor? _decompressor;
    private LinearResampler? _resampler;

    public Mp3StreamingDecoder(int targetSampleRate) => _targetSampleRate = targetSampleRate;

    public IEnumerable<byte[]> Decode(byte[] encoded)
    {
        if (encoded.Length == 0) yield break;
        _buffer.Position = _buffer.Length;
        _buffer.Write(encoded, 0, encoded.Length);
        _buffer.Position = 0;

        while (true)
        {
            long frameStart = _buffer.Position;
            Mp3Frame? frame;
            try
            {
                frame = Mp3Frame.LoadFromStream(_buffer);
            }
            catch (Exception) // incomplete/corrupt frame: keep the tail, wait for more bytes
            {
                _buffer.Position = frameStart;
                break;
            }
            if (frame is null)
            {
                _buffer.Position = frameStart;
                break; // end of buffered data
            }

            if (_decompressor is null)
            {
                int channels = frame.ChannelMode == ChannelMode.Mono ? 1 : 2;
                var sourceFormat = new Mp3WaveFormat(frame.SampleRate, channels, 1, frame.BitRate);
                _decompressor = new AcmMp3FrameDecompressor(sourceFormat);
            }

            var pcm = new byte[16384];
            int decoded = _decompressor.DecompressFrame(frame, pcm, 0);
            if (decoded <= 0) continue;

            var outFormat = _decompressor.OutputFormat;
            if (outFormat.SampleRate != _targetSampleRate || outFormat.Channels != 1)
            {
                _resampler ??= new LinearResampler(outFormat.SampleRate, outFormat.Channels, _targetSampleRate);
                foreach (var resampled in _resampler.Process(pcm, decoded))
                    yield return resampled;
            }
            else
            {
                yield return pcm[..decoded];
            }
        }

        Compact();
    }

    /// <summary>Keeps only the unconsumed tail so the buffer does not grow unboundedly.</summary>
    private void Compact()
    {
        long consumed = _buffer.Position;
        if (consumed == 0) return;
        long tail = _buffer.Length - consumed;
        var next = new MemoryStream((int)Math.Max(tail, 256));
        if (tail > 0)
        {
            var tailBytes = new byte[tail];
            _buffer.Position = consumed;
            _buffer.ReadExactly(tailBytes, 0, (int)tail);
            next.Write(tailBytes, 0, (int)tail);
        }
        _buffer = next;
    }
}

/// <summary>Stateful linear-interpolation resampler to 16-bit mono output. Carries phase and the
/// last source sample across chunks so consecutive chunks stay continuous.</summary>
internal sealed class LinearResampler
{
    private readonly double _ratio;       // source frames per output frame
    private readonly int _sourceChannels;
    private double _nextPos;              // source-frame position (relative to chunk start) of next output
    private float _prevSample;            // last source sample of the previous chunk (mono-mixed)

    public LinearResampler(int sourceRate, int sourceChannels, int targetRate)
    {
        _ratio = (double)sourceRate / targetRate;
        _sourceChannels = sourceChannels;
    }

    public IEnumerable<byte[]> Process(byte[] pcm, int length)
    {
        int frameCount = length / (2 * _sourceChannels);
        if (frameCount < 2)
        {
            if (frameCount == 1) _prevSample = ReadSample(pcm, frameCount - 1);
            yield break;
        }

        var outBytes = new List<byte>();
        for (double t = _nextPos; ; t += _ratio)
        {
            int idx = (int)t;
            if (idx + 1 >= frameCount)
            {
                _nextPos = t - (frameCount - 1);
                break;
            }
            double frac = t - idx;
            float a = idx < 0 ? _prevSample : ReadSample(pcm, idx);
            float b = ReadSample(pcm, idx + 1);
            short s = (short)Math.Clamp(a + (b - a) * frac, short.MinValue, short.MaxValue);
            outBytes.Add((byte)(s & 0xFF));
            outBytes.Add((byte)((s >> 8) & 0xFF));
        }

        _prevSample = ReadSample(pcm, frameCount - 1);
        if (outBytes.Count > 0)
            yield return outBytes.ToArray();
    }

    private float ReadSample(byte[] pcm, int frame)
    {
        float sum = 0;
        for (int ch = 0; ch < _sourceChannels; ch++)
        {
            int off = (frame * _sourceChannels + ch) * 2;
            sum += BitConverter.ToInt16(pcm, off);
        }
        return sum / _sourceChannels;
    }
}
