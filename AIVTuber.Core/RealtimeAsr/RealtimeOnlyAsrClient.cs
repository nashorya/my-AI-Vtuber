using AIVTuber.Core.Pipeline;

namespace AIVTuber.Core.RealtimeAsr;

/// <summary>
/// Legacy <see cref="IAsrClient"/> placeholder for realtime-only providers
/// (tencent_realtime / volcano_realtime). These providers expose only bidirectional
/// sessions (<see cref="IRealtimeAsrSession"/>) in this repository; whole-segment
/// recognition is not implemented for them. Using the legacy path fails loudly instead of
/// silently routing realtime audio through an unimplemented code path.
/// </summary>
public sealed class RealtimeOnlyAsrClient(string provider) : IAsrClient
{
    private readonly string _message =
        $"asr.provider={provider} 只支持实时会话路径（realtime.streaming_asr_enabled=true），" +
        "不支持整段识别。请启用实时模式或换回 legacy provider。";

    public Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default)
        => Task.FromException<AsrResult>(new NotSupportedException(_message));

    public IAsyncEnumerable<AsrResult> StreamRecognizeAsync(IAsyncEnumerable<byte[]> audioStream,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(_message);
}
