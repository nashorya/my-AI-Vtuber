using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using AIVTuber.Core.Config;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// Aliyun DashScope realtime ASR over WebSocket (Fun-ASR / Paraformer). One connection per
/// RecognizeAsync call: run-task → task-started → stream the PCM buffer as binary → finish-task →
/// collect the final result-generated sentences → task-finished.
/// </summary>
public sealed class DashScopeAsrClient : IAsrClient
{
    private const int ChunkSize = 3200; // 100ms @ 16kHz/16bit/mono
    private readonly AsrConfig _config;

    public DashScopeAsrClient(AsrConfig config) => _config = config;

    public async Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(_config.Model) ? "paraformer-realtime-v2" : _config.Model;

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"bearer {_config.ApiKey}");
        await ws.ConnectAsync(new Uri(DashScopeProtocol.InferenceUrl), cancellationToken);

        var taskId = DashScopeProtocol.NewTaskId();
        await DashScopeSocket.SendTextAsync(ws, DashScopeProtocol.RunTaskAsr(taskId, model, 16000), cancellationToken);

        var transcript = new StringBuilder();
        var audioSent = false;

        while (true)
        {
            var msg = await DashScopeSocket.ReceiveMessageAsync(ws, cancellationToken);
            if (msg is null) break;
            if (msg.Value.Type != WebSocketMessageType.Text) continue;

            var json = Encoding.UTF8.GetString(msg.Value.Bytes);
            var (ev, err) = DashScopeProtocol.ParseEvent(json);
            switch (ev)
            {
                case "task-started":
                    if (!audioSent)
                    {
                        audioSent = true;
                        await DashScopeSocket.SendAudioAsync(ws, pcm16k, ChunkSize, cancellationToken);
                        await DashScopeSocket.SendTextAsync(ws, DashScopeProtocol.FinishTask(taskId), cancellationToken);
                    }
                    break;

                case "result-generated":
                    var (sentence, end) = DashScopeProtocol.ParseAsrSentence(json);
                    if (end && !string.IsNullOrEmpty(sentence)) transcript.Append(sentence);
                    break;

                case "task-finished":
                    await DashScopeSocket.CloseAsync(ws);
                    return new AsrResult(transcript.ToString());

                case "task-failed":
                    await DashScopeSocket.CloseAsync(ws);
                    throw new InvalidOperationException($"DashScope ASR failed: {err}");
            }
        }

        return new AsrResult(transcript.ToString());
    }

    public async IAsyncEnumerable<AsrResult> StreamRecognizeAsync(
        IAsyncEnumerable<byte[]> audioStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new List<byte>();
        await foreach (var chunk in audioStream.WithCancellation(cancellationToken))
            buffer.AddRange(chunk);

        if (buffer.Count > 0)
        {
            var result = await RecognizeAsync(buffer.ToArray(), cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Text)) yield return result;
        }
    }
}
