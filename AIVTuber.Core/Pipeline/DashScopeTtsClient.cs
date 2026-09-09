using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Config;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// Aliyun DashScope realtime TTS over WebSocket (CosyVoice). One connection per sentence:
/// run-task → task-started → continue-task(text) → finish-task → binary PCM frames → task-finished.
/// PCM is requested at <see cref="AudioPlayer.DefaultSampleRate"/> so it plays directly.
/// </summary>
public sealed class DashScopeTtsClient : ITtsClient
{
    private readonly TtsConfig _config;

    public DashScopeTtsClient(TtsConfig config) => _config = config;

    internal static (string Model, string Voice) NormalizePair(string? model, string? voice)
    {
        var m = string.IsNullOrWhiteSpace(model) ? "cosyvoice-v3-flash" : model.Trim();
        return (m, (voice ?? "").Trim());
    }

    /// <summary>Surface Aliyun 418 (voice/model mismatch) with an actionable hint.</summary>
    internal static string FormatTaskFailed(string? err, string model, string voiceId)
    {
        var detail = string.IsNullOrWhiteSpace(err) ? "(no detail)" : err;
        if (detail.Contains("418", StringComparison.Ordinal))
        {
            return $"DashScope TTS failed: {detail} — voice '{voiceId}' 与 model '{model}' 不匹配。"
                   + " cosyvoice-v3.5-* 无系统音色，需用声音复刻/设计返回的 voice_id；"
                   + "系统音色请改用 cosyvoice-v3-flash/plus + longanyang 等。";
        }

        return $"DashScope TTS failed: {detail}";
    }

    private static string? MapToDashScopeInstruction(string? emotion) => emotion?.ToLowerInvariant() switch
    {
        "happy"     => "用开心愉悦的语气说话",
        "sad"       => "用悲伤低落的语气说话",
        "angry"     => "用愤怒激动的语气说话",
        "fearful"   => "用恐惧害怕的语气说话",
        "disgusted" => "用厌恶的语气说话",
        "surprised" => "用惊讶的语气说话",
        "calm"      => "用平静自然的语气说话",
        "neutral"   => "用平静自然的语气说话",
        "whisper"   => "用低语悄悄的方式说话",
        _           => null,
    };

    public async IAsyncEnumerable<byte[]> StreamAsync(
        string text,
        string voiceId,
        string? emotion,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (model, voice) = NormalizePair(_config.Model, voiceId);

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"bearer {_config.ApiKey}");
        ws.Options.SetRequestHeader("X-DashScope-DataInspection", "enable");
        await ws.ConnectAsync(new Uri(DashScopeProtocol.InferenceUrl), cancellationToken);

        var taskId = DashScopeProtocol.NewTaskId();
        var instructions = MapToDashScopeInstruction(emotion);
        await DashScopeSocket.SendTextAsync(ws,
            DashScopeProtocol.RunTaskTts(taskId, model, voice, _config.SampleRate, _config.Speed, instructions),
            cancellationToken);

        var textSent = false;

        while (true)
        {
            var msg = await DashScopeSocket.ReceiveMessageAsync(ws, cancellationToken);
            if (msg is null) yield break;

            if (msg.Value.Type == WebSocketMessageType.Binary)
            {
                if (msg.Value.Bytes.Length > 0) yield return msg.Value.Bytes; // raw PCM audio
                continue;
            }

            var json = Encoding.UTF8.GetString(msg.Value.Bytes);
            var (ev, err) = DashScopeProtocol.ParseEvent(json);
            switch (ev)
            {
                case "task-started":
                    if (!textSent)
                    {
                        textSent = true;
                        await DashScopeSocket.SendTextAsync(ws, DashScopeProtocol.ContinueTask(taskId, text), cancellationToken);
                        await DashScopeSocket.SendTextAsync(ws, DashScopeProtocol.FinishTask(taskId), cancellationToken);
                    }
                    break;

                case "task-finished":
                    await DashScopeSocket.CloseAsync(ws);
                    yield break;

                case "task-failed":
                    await DashScopeSocket.CloseAsync(ws);
                    throw new InvalidOperationException(FormatTaskFailed(err, model, voice));
            }
        }
    }
}
