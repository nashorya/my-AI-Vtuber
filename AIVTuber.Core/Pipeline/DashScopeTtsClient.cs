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
        var model = string.IsNullOrWhiteSpace(_config.Model) ? "cosyvoice-v3-flash" : _config.Model;

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"bearer {_config.ApiKey}");
        ws.Options.SetRequestHeader("X-DashScope-DataInspection", "enable");
        await ws.ConnectAsync(new Uri(DashScopeProtocol.InferenceUrl), cancellationToken);

        var taskId = DashScopeProtocol.NewTaskId();
        var instructions = MapToDashScopeInstruction(emotion);
        await DashScopeSocket.SendTextAsync(ws,
            DashScopeProtocol.RunTaskTts(taskId, model, voiceId, AudioPlayer.DefaultSampleRate, _config.Speed, instructions),
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
                    throw new InvalidOperationException($"DashScope TTS failed: {err}");
            }
        }
    }
}
