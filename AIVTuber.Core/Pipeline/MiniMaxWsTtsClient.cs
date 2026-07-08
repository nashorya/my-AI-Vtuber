using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Config;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// MiniMax TTS over WebSocket (wss://api.minimaxi.com/ws/v1/t2a_v2).
/// One WS session per sentence: task_start → task_continue → task_finish → stream audio chunks.
/// Audio arrives as hex-encoded PCM in task_continued events, yielded immediately for low latency.
/// </summary>
public sealed class MiniMaxWsTtsClient : ITtsClient
{
    private readonly TtsConfig _config;

    public MiniMaxWsTtsClient(TtsConfig config) => _config = config;

    public async IAsyncEnumerable<byte[]> StreamAsync(
        string text,
        string voiceId,
        string? emotion,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(_config.Model) ? "speech-2.8-turbo" : _config.Model;
        var miniMaxEmotion = MapEmotion(emotion);

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.ApiKey}");

        await ws.ConnectAsync(new Uri("wss://api.minimaxi.com/ws/v1/t2a_v2"), cancellationToken);

        // Wait for connected_success
        var connected = await ReceiveEventAsync(ws, cancellationToken);
        if (connected?.Event != "connected_success")
            throw new InvalidOperationException($"MiniMax WS: unexpected event '{connected?.Event}' (expected connected_success)");

        // Send task_start
        await SendJsonAsync(ws, BuildTaskStart(model, voiceId, miniMaxEmotion), cancellationToken);

        // Wait for task_started
        var started = await ReceiveEventAsync(ws, cancellationToken);
        if (started?.Event != "task_started")
            throw new InvalidOperationException($"MiniMax WS: unexpected event '{started?.Event}' (expected task_started)");

        // Send the sentence text, then signal end
        await SendJsonAsync(ws, new { @event = "task_continue", text }, cancellationToken);
        await SendJsonAsync(ws, new { @event = "task_finish" }, cancellationToken);

        // Stream audio chunks until task_finished or task_failed
        while (true)
        {
            var ev = await ReceiveEventAsync(ws, cancellationToken);
            if (ev is null) yield break;

            switch (ev.Event)
            {
                case "task_continued":
                    if (ev.Data?.Audio is { Length: > 0 } hex)
                    {
                        var pcm = Convert.FromHexString(hex);
                        if (pcm.Length > 0) yield return pcm;
                    }
                    if (ev.IsFinal == true) yield break;
                    break;

                case "task_finished":
                    yield break;

                case "task_failed":
                    throw new InvalidOperationException(
                        $"MiniMax WS TTS failed: {ev.BaseResp?.StatusCode} {ev.BaseResp?.StatusMsg}");
            }
        }
    }

    private object BuildTaskStart(string model, string voiceId, string? emotion)
    {
        var speed = _config.Speed;
        object voiceSetting = emotion is null
            ? new { voice_id = voiceId, speed, vol = 1.0, pitch = 0 }
            : new { voice_id = voiceId, speed, vol = 1.0, pitch = 0, emotion };

        return new
        {
            @event = "task_start",
            model,
            voice_setting = voiceSetting,
            audio_setting = new
            {
                sample_rate = AudioPlayer.DefaultSampleRate,
                format = "pcm",
                channel = 1,
            },
        };
    }

    private static string? MapEmotion(string? emotion) => emotion switch
    {
        null => null,
        // English pass-through
        "happy"     => "happy",
        "sad"       => "sad",
        "angry"     => "angry",
        "fearful"   => "fearful",
        "disgusted" => "disgusted",
        "surprised" => "surprised",
        "calm"      => "calm",
        "neutral"   => "calm",
        "whisper"   => "whisper",
        // Chinese → MiniMax emotion
        "开心" => "happy",
        "高兴" => "happy",
        "愉快" => "happy",
        "难过" => "sad",
        "悲伤" => "sad",
        "生气" => "angry",
        "愤怒" => "angry",
        "害羞" => "happy",   // MiniMax 没有 shy，用 happy 近似
        "恐惧" => "fearful",
        "害怕" => "fearful",
        "厌恶" => "disgusted",
        "惊讶" => "surprised",
        "平静" => "calm",
        "低语" => "whisper",
        _     => null,
    };

    private static async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task<WsEvent?> ReceiveEventAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new ArraySegment<byte>(new byte[65536]);
        using var ms = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer.Array!, buffer.Offset, result.Count);
        }
        while (!result.EndOfMessage);

        ms.Position = 0;
        return JsonSerializer.Deserialize<WsEvent>(ms, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class WsEvent
    {
        public string? Event { get; set; }
        public string? SessionId { get; set; }
        public bool? IsFinal { get; set; }
        public WsAudioData? Data { get; set; }
        public WsBaseResp? BaseResp { get; set; }
    }

    private sealed class WsAudioData
    {
        public string? Audio { get; set; }
    }

    private sealed class WsBaseResp
    {
        public int StatusCode { get; set; }
        public string? StatusMsg { get; set; }
    }
}
