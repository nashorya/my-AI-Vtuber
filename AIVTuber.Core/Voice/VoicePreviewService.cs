using System.Runtime.CompilerServices;
using System.Text.Json;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Auth;
using AIVTuber.Core.Config;
using AIVTuber.Core.Diagnostics;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Core.Voice;

public enum PreviewState { Preparing, Playing, Finished, Stopped, Failed, Rejected }

/// <summary>One preview update. <see cref="RequestId"/> lets the page drop late updates of a
/// preview that a newer one already replaced.</summary>
public sealed record PreviewStatus(long RequestId, string ChoiceId, PreviewState State, string Message,
    UserFacingError? Error = null);

/// <summary>Where preview audio goes: only the local listening device.</summary>
public interface IPreviewAudioOutput : IDisposable
{
    Task PlayAsync(IAsyncEnumerable<byte[]> pcm, CancellationToken ct);
    /// <summary>Stops immediately; must not wait for the vendor.</summary>
    void Stop();
}

/// <summary>A dedicated <see cref="AudioPlayer"/> on the listening device. It is not the
/// runtime's formal player, so nothing is tapped into the virtual microphone.</summary>
public sealed class AudioPlayerPreviewOutput(int sampleRate, int deviceIndex) : IPreviewAudioOutput
{
    private readonly AudioPlayer _player = new(sampleRate, deviceIndex);
    public Task PlayAsync(IAsyncEnumerable<byte[]> pcm, CancellationToken ct) => _player.PlayChunksAsync(pcm, ct);
    public void Stop() => _player.Stop();
    public void Dispose() => _player.Dispose();
}

/// <summary>
/// Voice preview (V02/V03). Synthesises a fixed short sentence with a candidate voice through
/// the same TTS adapter the companion uses, without touching the formal voice, the LLM, the
/// conversation or memory, subtitles, the virtual microphone or avatar motion.
/// <list type="bullet">
/// <item>Each preview freezes its TTS settings and gets its own request id, cancellation and
/// short-lived client; a newer preview, Stop, sign-out or expiry ends the old one locally at
/// once and its late audio is discarded.</item>
/// <item>No preview while the companion is speaking: it would either be talked over or have to
/// interrupt the formal reply.</item>
/// <item>Preview needs the same account grant as any other vendor call.</item>
/// </list>
/// Note: audio goes to the listening device only; if the streaming software captures that
/// device as desktop audio, the preview can still end up on stream.
/// </summary>
public sealed class VoicePreviewService : IDisposable
{
    public const string SampleText = "你好呀，这是我现在的声音，听起来怎么样？";
    public const int SampleTextVersion = 1;

    private readonly Func<DistributionProfile?> _profile;
    private readonly Func<TtsConfig> _ttsSnapshot;
    private readonly Func<TtsConfig, ITtsClient> _clientFactory;
    private readonly Func<TtsConfig, IPreviewAudioOutput> _outputFactory;
    private readonly Func<ICloudAccess> _cloud;
    private readonly Func<bool> _companionSpeaking;
    private readonly object _sync = new();
    private long _sequence;
    private Active? _active;
    private ICloudAccess? _subscribed;

    private sealed record Active(long Id, string ChoiceId, CancellationTokenSource Cts, IPreviewAudioOutput Output);

    public VoicePreviewService(
        Func<DistributionProfile?> profile,
        Func<TtsConfig> ttsSnapshot,
        Func<TtsConfig, ITtsClient> clientFactory,
        Func<TtsConfig, IPreviewAudioOutput> outputFactory,
        Func<ICloudAccess> cloud,
        Func<bool> companionSpeaking)
    {
        _profile = profile;
        _ttsSnapshot = ttsSnapshot;
        _clientFactory = clientFactory;
        _outputFactory = outputFactory;
        _cloud = cloud;
        _companionSpeaking = companionSpeaking;
    }

    public event Action<PreviewStatus>? StatusChanged;

    /// <summary>Id of the preview that is currently allowed to produce sound (0 = none).</summary>
    public long CurrentRequestId { get { lock (_sync) return _active?.Id ?? 0; } }

    /// <summary>Starts a preview and completes when it has finished, been stopped or failed.</summary>
    public async Task<PreviewStatus> PreviewAsync(string choiceId, CancellationToken ct = default)
    {
        var cloud = _cloud();
        EnsureRevocationHook(cloud);
        var id = Interlocked.Increment(ref _sequence);
        StopActive(); // a newer preview always replaces the older one

        if (!cloud.IsAllowed)
            return Publish(new(id, choiceId, PreviewState.Rejected, "登录后才能试听音色。"));
        if (_companionSpeaking())
            return Publish(new(id, choiceId, PreviewState.Rejected, "AI 正在说话，请先暂停陪播或等它说完再试听。"));
        var voice = _profile()?.FindVoiceByChoiceId(choiceId);
        if (voice is null)
            return Publish(new(id, choiceId, PreviewState.Rejected, "这个音色暂时不可用，请重新选择。"));

        var frozen = Freeze(_ttsSnapshot(), voice.VoiceId);
        var epoch = cloud.Epoch;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IPreviewAudioOutput output;
        try { output = _outputFactory(frozen); }
        catch (Exception ex)
        {
            cts.Dispose();
            var deviceError = UserErrorMapper.FromException(ex, ErrorArea.Device);
            return Publish(new(id, choiceId, PreviewState.Failed, deviceError?.UserMessage ?? "试听设备不可用。", deviceError));
        }
        var active = new Active(id, choiceId, cts, output);
        lock (_sync)
        {
            if (_sequence != id) { cts.Dispose(); output.Dispose(); return new(id, choiceId, PreviewState.Stopped, "已被新的试听取代"); }
            _active = active;
        }

        Publish(new(id, choiceId, PreviewState.Preparing, "正在准备试听…"));
        ITtsClient? client = null;
        try
        {
            client = _clientFactory(frozen);
            var stream = Guard(client.StreamAsync(SampleText, frozen.VoiceId, null, cts.Token), active, cloud, epoch);
            await output.PlayAsync(stream, cts.Token).ConfigureAwait(false);
            if (!IsCurrent(active, cloud, epoch) || cts.IsCancellationRequested)
                return Finish(active, PreviewState.Stopped, "试听已停止");
            return Finish(active, PreviewState.Finished, "试听结束");
        }
        catch (OperationCanceledException)
        {
            return Finish(active, PreviewState.Stopped, "试听已停止");
        }
        catch (Exception ex)
        {
            if (!IsCurrent(active, cloud, epoch)) return Finish(active, PreviewState.Stopped, "试听已停止");
            var error = UserErrorMapper.FromException(ex, ErrorArea.Voice);
            return Finish(active, PreviewState.Failed, error?.UserMessage ?? "试听没有完成，请重试。", error);
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    /// <summary>Stops the current preview locally, without waiting for the vendor.</summary>
    public void Stop() => StopActive();

    public void Dispose()
    {
        StopActive();
        if (_subscribed is not null) _subscribed.Revoked -= OnRevoked;
    }

    private void OnRevoked(string _) => StopActive();

    private void EnsureRevocationHook(ICloudAccess cloud)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_subscribed, cloud)) return;
            if (_subscribed is not null) _subscribed.Revoked -= OnRevoked;
            _subscribed = cloud;
            cloud.Revoked += OnRevoked;
        }
    }

    private void StopActive()
    {
        Active? active;
        lock (_sync)
        {
            active = _active;
            _active = null;
        }
        if (active is null) return;
        try { active.Cts.Cancel(); } catch (ObjectDisposedException) { }
        try { active.Output.Stop(); } catch { /* device already gone */ }
    }

    private bool IsCurrent(Active active, ICloudAccess cloud, long epoch)
    {
        lock (_sync)
            return ReferenceEquals(_active, active) && cloud.IsAllowed && cloud.Epoch == epoch;
    }

    private async IAsyncEnumerable<byte[]> Guard(IAsyncEnumerable<byte[]> source, Active active, ICloudAccess cloud, long epoch,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var first = true;
        await foreach (var chunk in source.WithCancellation(ct).ConfigureAwait(false))
        {
            // Late audio of a replaced/stopped/revoked preview never reaches the speaker.
            if (!IsCurrent(active, cloud, epoch) || active.Cts.IsCancellationRequested) yield break;
            if (first)
            {
                first = false;
                Publish(new(active.Id, active.ChoiceId, PreviewState.Playing, "正在播放试听"));
            }
            yield return chunk;
        }
    }

    private PreviewStatus Finish(Active active, PreviewState state, string message, UserFacingError? error = null)
    {
        lock (_sync)
            if (ReferenceEquals(_active, active)) _active = null;
        active.Cts.Dispose();
        active.Output.Dispose();
        var status = new PreviewStatus(active.Id, active.ChoiceId, state, message, error);
        // A superseded preview reports nothing: the page only follows the newest request.
        return Publish(status, publish: Interlocked.Read(ref _sequence) == active.Id);
    }

    private PreviewStatus Publish(PreviewStatus status, bool publish = true)
    {
        if (publish) StatusChanged?.Invoke(status);
        return status;
    }

    internal static TtsConfig Freeze(TtsConfig source, string voiceId)
    {
        var copy = JsonSerializer.Deserialize<TtsConfig>(JsonSerializer.Serialize(source, ConfigManager.JsonOptions), ConfigManager.JsonOptions)!;
        copy.VoiceId = voiceId;
        return copy;
    }
}
