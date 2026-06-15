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

    public async IAsyncEnumerable<byte[]> StreamAsync(
        string text,
        string voiceId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(_config.Model) ? "cosyvoice-v3-flash" : _config.Model;

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"bearer {_config.ApiKey}");
        ws.Options.SetRequestHeader("X-DashScope-DataInspection", "enable");
        await ws.ConnectAsync(new Uri(DashScopeProtocol.InferenceUrl), cancellationToken);

        var taskId = DashScopeProtocol.NewTaskId();
        await DashScopeSocket.SendTextAsync(ws,
            DashScopeProtocol.RunTaskTts(taskId, model, voiceId, AudioPlayer.DefaultSampleRate, _config.Speed),
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
