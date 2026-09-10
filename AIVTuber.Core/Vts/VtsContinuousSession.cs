using System.Text.Json;
using AIVTuber.Core.Avatar;
using AIVTuber.Core.Diagnostics;

namespace AIVTuber.Core.Vts;

public sealed record VtsParameter(string Id, float Min, float Max, float Default, float Value);
public sealed record VtsModelSnapshot(string Id, string Name, string Revision,
    IReadOnlyList<VtsParameter> Parameters, IReadOnlySet<string> Inputs, bool HasActiveExpressions);

/// <summary>Model discovery, verification and manual takeover. No UI or audio dependency.</summary>
public sealed class VtsContinuousSession : IAsyncDisposable, IAvatarMotionSink
{
    private readonly VtsClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private ContinuousControlConfig _config;
    private AvatarMotionDirector? _director;
    private VtsModelSnapshot? _model;
    private volatile bool _paused = true;
    private bool _disposed;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _trials = new();
    private CancellationTokenSource? _previewCancellation;
    private int _refreshing, _reconnecting;
    private Task? _expressionMonitor;
    private bool _pollExpressions;
    private string[] _allowedChannels = [];
    private long _canceledThrough;
    private long _controlEpoch, _turnEpoch, _turnGeneration;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _background = new();
    private void Supervise(Task task)
    {
        _background[task] = 0;
        _ = task.ContinueWith(done =>
        {
            _background.TryRemove(done, out _);
            if (done.IsFaulted && !_disposed) SetStatus("VTS 后台任务失败：" + done.Exception!.GetBaseException().Message);
        }, TaskScheduler.Default);
    }
    private bool _receivedModelEvent;
    private string _revision = Guid.NewGuid().ToString("N");
    public string Status { get; private set; } = "实验未启用";
    public VtsModelSnapshot? Model => Volatile.Read(ref _model);
    public event EventHandler? Changed;
    public VtsContinuousSession(VtsClient client, ContinuousControlConfig config)
    {
        _client = client; _config = config.Snapshot();
        _client.OnEvent += OnEvent;
        _client.OnDisconnected += OnDisconnected;
        _client.OnStateChanged += OnState;
    }
    public string[] AllowedChannels => _paused ? [] : Volatile.Read(ref _allowedChannels).ToArray();
    private void SetStatus(string message)
    {
        Status = message;
        DebugLog.Write($"[Avatar/VTS] {message}");
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void OnState(object? sender, string state) => SetStatus(state);
    public void BeginTurn(long generation)
    {
        Interlocked.Exchange(ref _turnEpoch, Interlocked.Read(ref _controlEpoch));
        Interlocked.Exchange(ref _turnGeneration, generation);
    }
    public void Submit(long generation, AvatarIntent intent)
    {
        var director = _director;
        if (_paused || generation <= Interlocked.Read(ref _canceledThrough) ||
            generation != Interlocked.Read(ref _turnGeneration) || Interlocked.Read(ref _turnEpoch) != Interlocked.Read(ref _controlEpoch))
        {
            DebugLog.Write($"[Avatar/VTS] generation={generation} model={Model?.Id} rejected: control paused or turn invalidated");
            return;
        }
        var allowed = AllowedChannels.ToHashSet();
        var filtered = intent.Targets.Where(p => allowed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        if (filtered.Count == 0) return;
        director?.Submit(generation, intent with { Targets = filtered });
        DebugLog.Write($"[Avatar/VTS] generation={generation} model={Model?.Id} targets={string.Join(',', filtered.Keys)} accepted");
    }
    public void Cancel(long generation)
    {
        long old;
        do { old = Interlocked.Read(ref _canceledThrough); if (generation <= old) break; }
        while (Interlocked.CompareExchange(ref _canceledThrough, generation, old) != old);
        _director?.Cancel(generation);
    }
    public void OnRms(float rms) { if (!_paused) _director?.OnRms(rms); }
    public void NoteListening() { if (!_paused) _director?.NoteListening(); }

    private void InvalidateControl()
    {
        Interlocked.Increment(ref _controlEpoch);
        _paused = true;
        // Stop sampling synchronously, even if a settings operation holds the async gate.
        _director?.Halt();
    }

    private void OnEvent(object? sender, VtsResponse response)
    {
        if (response.MessageType is "ModelLoadedEvent" or "ModelConfigChangedEvent")
        {
            InvalidateControl();
            _receivedModelEvent = true;
            _revision = Guid.NewGuid().ToString("N");
            Supervise(RefreshAfterEventAsync());
        }
        else if (response.MessageType is "HotkeyTriggeredEvent" or "ExpressionToggledEvent")
        {
            if (response.Data is { } payload && payload.TryGetProperty("isLive2DItem", out var item) && item.GetBoolean()) return;
            if (response.Data is { } body && body.TryGetProperty("justLoaded", out var loaded) && loaded.GetBoolean()) return;
            // Never overwrite a human's expression with a neutral frame.
            InvalidateControl();
            Supervise(PauseFromEventAsync());
        }
    }
    private async Task PauseFromEventAsync()
    {
        try { await StopAsync(false).ConfigureAwait(false); SetStatus("人工操作已暂停 AI，请检查表情/动画后手动恢复"); }
        catch (Exception ex) { if (!_disposed) SetStatus(ex.Message); }
    }
    private async Task RefreshAfterEventAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            string revision;
            do
            {
                revision = _revision;
                await StopAsync(false).ConfigureAwait(false);
                await RefreshAsync(_life.Token).ConfigureAwait(false);
            } while (revision != _revision && !_disposed);
        }
        catch (Exception ex) { if (!_disposed) SetStatus("模型刷新失败：" + ex.Message); }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }
    private void OnDisconnected(object? sender, string reason)
    {
        InvalidateControl();
        Supervise(RecoverAsync());
    }
    private async Task RecoverAsync()
    {
        if (Interlocked.Exchange(ref _reconnecting, 1) != 0) return;
        try
        {
            await StopAsync(false).ConfigureAwait(false);
            foreach (var delay in new[] { 1, 2, 5 })
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), _life.Token).ConfigureAwait(false);
                try { await ConnectAsync(_life.Token).ConfigureAwait(false); return; }
                catch (VtsApiException ex) when (ex.AuthorizationDenied) { SetStatus("授权被拒绝，请手动连接"); return; }
                catch (Exception) when (!_life.IsCancellationRequested) { }
            }
            SetStatus("重连失败，请点击重新连接");
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        finally { Interlocked.Exchange(ref _reconnecting, 0); }
    }
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await StopAsync(false).ConfigureAwait(false);
        await _client.ConnectAsync(ct).ConfigureAwait(false);
        foreach (var eventName in new[] { "ModelLoadedEvent", "ModelConfigChangedEvent", "HotkeyTriggeredEvent" })
            await _client.SubscribeAsync(eventName, ct).ConfigureAwait(false);
        try { await _client.SubscribeAsync("ExpressionToggledEvent", ct).ConfigureAwait(false); _pollExpressions = false; }
        catch (VtsApiException ex) when (ex.ErrorId == 950) { _pollExpressions = true; }
        _expressionMonitor ??= MonitorExpressionsAsync();
        await RefreshAsync(ct).ConfigureAwait(false);
        // Reconnect only restores idle, never a previous utterance. Previously verified mappings
        // are checked again against actual output ranges; manual config events invalidate Revision.
        await ApplyAsync(_config, ct).ConfigureAwait(false);
    }
    private async Task MonitorExpressionsAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(_life.Token).ConfigureAwait(false))
            {
                if (!_pollExpressions || !_client.IsConnected || _paused) continue;
                try
                {
                    var state = await _client.QueryAsync("ExpressionStateRequest", _life.Token).ConfigureAwait(false);
                    if (state.TryGetProperty("expressions", out var list) && list.EnumerateArray().Any(p => p.GetProperty("active").GetBoolean()))
                        await PauseFromEventAsync().ConfigureAwait(false);
                }
                catch (Exception) when (!_life.IsCancellationRequested) { /* socket recovery owns errors */ }
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
    }
    public async Task<VtsModelSnapshot?> RefreshAsync(CancellationToken ct = default)
    {
        var revision = _revision;
        var current = await _client.QueryAsync("CurrentModelRequest", ct).ConfigureAwait(false);
        if (!current.GetProperty("modelLoaded").GetBoolean())
        { Volatile.Write(ref _model, null); SetStatus("VTS 未加载模型"); return null; }
        var id = current.GetProperty("modelID").GetString()!;
        var parameters = await _client.QueryAsync("Live2DParameterListRequest", ct).ConfigureAwait(false);
        var inputs = await _client.QueryAsync("InputParameterListRequest", ct).ConfigureAwait(false);
        var expressions = await _client.QueryAsync("ExpressionStateRequest", ct).ConfigureAwait(false);
        if (parameters.GetProperty("modelID").GetString() != id) throw new InvalidOperationException("查询时模型已切换，请刷新");
        var list = parameters.GetProperty("parameters").EnumerateArray().Select(p => new VtsParameter(
            p.GetProperty("name").GetString()!, p.GetProperty("min").GetSingle(), p.GetProperty("max").GetSingle(),
            p.GetProperty("defaultValue").GetSingle(), p.GetProperty("value").GetSingle())).ToArray();
        var inputNames = new HashSet<string>();
        foreach (var group in new[] { "defaultParameters", "customParameters" })
            if (inputs.TryGetProperty(group, out var array))
                foreach (var p in array.EnumerateArray()) inputNames.Add(p.GetProperty("name").GetString()!);
        var active = expressions.TryGetProperty("expressions", out var expressionList) &&
            expressionList.EnumerateArray().Any(p => p.TryGetProperty("active", out var a) && a.GetBoolean());
        if (revision != _revision) throw new InvalidOperationException("查询期间模型配置已变化，请刷新");
        if (!_receivedModelEvent && Model is null && _config.Profiles.TryGetValue(id, out var saved) && !string.IsNullOrEmpty(saved.Revision))
            _revision = saved.Revision;
        var model = new VtsModelSnapshot(id, current.GetProperty("modelName").GetString()!, _revision, list, inputNames, active);
        Volatile.Write(ref _model, model);
        SetStatus($"模型：{model.Name}；{list.Length} 个参数；请配置并验证映射");
        return model;
    }
    public AvatarModelProfile CreateDraft(ContinuousControlConfig config)
    {
        var model = Model ?? throw new InvalidOperationException("请先连接并加载模型");
        var existing = config.Snapshot().Profiles.GetValueOrDefault(model.Id);
        var profile = existing ?? new AvatarModelProfile { ModelId = model.Id, ModelName = model.Name };
        if (profile.Revision != model.Revision) foreach (var b in profile.Channels) b.Verified = false;
        profile.Revision = model.Revision;
        foreach (var channel in AvatarChannels.All.Where(c => !profile.Channels.Any(b => b.Channel == c.Name)))
        {
            var parameter = model.Parameters.FirstOrDefault(p => p.Id == channel.SuggestedParameter);
            profile.Channels.Add(new()
            {
                Channel = channel.Name, ParameterId = parameter?.Id ?? "", Minimum = parameter?.Min ?? (channel.Unipolar ? 0 : -1),
                Maximum = parameter?.Max ?? 1, Neutral = parameter?.Default ?? (channel.Name.StartsWith("eyeOpen") ? 1 : 0)
            });
        }
        return profile;
    }
    public async Task PrepareInputsAsync(AvatarModelProfile profile, CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        ValidateModel(profile);
        foreach (var b in profile.Channels.Where(b => b.IsValid))
            await _client.CreateParameterAsync(b.InputId, b.Minimum, b.Maximum, b.Neutral, ct).ConfigureAwait(false);
        await RefreshAsync(ct).ConfigureAwait(false);
        SetStatus("输入已创建；请在 VTS 将同名输入映射到目标，输入/输出范围均设为表中最小/最大值");
    }
    private void ValidateModel(AvatarModelProfile profile)
    {
        if (Model is not { } model || model.Id != profile.ModelId || profile.Revision != model.Revision)
            throw new InvalidOperationException("模型或配置已变化，请重新刷新通道");
    }
    private static string TrialKey(AvatarModelProfile profile, AvatarChannelBinding binding)
        => profile.ModelId + ":" + profile.Revision + ":" + JsonSerializer.Serialize(new
        { binding.Channel, binding.ParameterId, binding.Minimum, binding.Maximum, binding.Neutral, binding.Inverted });
    public void ConfirmTrial(AvatarModelProfile profile, AvatarChannelBinding binding)
    {
        ValidateModel(profile);
        if (!_trials.ContainsKey(TrialKey(profile, binding))) throw new InvalidOperationException("请先完成该通道当前配置的试动，再视觉确认");
        binding.Verified = true;
    }
    public async Task TestAsync(AvatarModelProfile profile, AvatarChannelBinding binding, float value, CancellationToken ct = default)
    {
        var epoch = Interlocked.Increment(ref _controlEpoch);
        var trialKey = TrialKey(profile, binding);
        var testBinding = JsonSerializer.Deserialize<AvatarChannelBinding>(JsonSerializer.Serialize(binding))!;
        testBinding.Verified = true;
        using var previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        await using var test = new AvatarMotionDirector(_client, new[] { testBinding }, preview: true);
        string? failure = null;
        test.Faulted += (_, error) => { failure = error; previewCancellation.Cancel(); };
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(true).ConfigureAwait(false);
            ValidateModel(profile);
            if (!binding.IsValid || Model!.Parameters.All(p => p.Id != binding.ParameterId || binding.Minimum < p.Min || binding.Maximum > p.Max))
                throw new InvalidOperationException("目标参数或校准范围无效");
            if (!float.IsFinite(value) || value < (AvatarChannels.All.First(c => c.Name == binding.Channel).Unipolar ? 0 : -1) || value > 1)
                throw new InvalidOperationException("试动幅度无效");
            if (!Model.Inputs.Contains(binding.InputId)) throw new InvalidOperationException("请先创建输入并在 VTS 配置映射");
            if (Model.HasActiveExpressions) throw new InvalidOperationException("请先在 VTS 清除活动表情再试动");
            if (epoch != Interlocked.Read(ref _controlEpoch)) throw new OperationCanceledException("试动已被停止或人工操作取消");
            _previewCancellation = previewCancellation;
            _director = test;
            test.Submit(0, new(new Dictionary<string, float> { [binding.Channel] = value }, 400, 1000));
            test.Start();
        }
        finally { _gate.Release(); }
        try
        {
            await Task.Delay(1500, previewCancellation.Token).ConfigureAwait(false);
            ValidateModel(profile);
            if (epoch != Interlocked.Read(ref _controlEpoch)) throw new OperationCanceledException("试动已被停止或人工操作取消");
            if (test.SentFrames == 0) throw new IOException("试动没有获得 VTS 注入响应");
            var actual = await _client.QueryAsync("Live2DParameterListRequest", previewCancellation.Token).ConfigureAwait(false);
            if (actual.GetProperty("modelID").GetString() != profile.ModelId) throw new InvalidOperationException("试动期间模型已切换");
            // Readback is diagnostic only: even a changing value is not proof of correct rigging.
            _trials[trialKey] = 0;
        }
        catch (OperationCanceledException) when (failure is not null) { throw new IOException("试动失败：" + failure); }
        finally
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_previewCancellation, previewCancellation)) _previewCancellation = null;
                if (Interlocked.CompareExchange(ref _director, null, test) == test && !previewCancellation.IsCancellationRequested &&
                    epoch == Interlocked.Read(ref _controlEpoch))
                    await test.ReturnToNeutralAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        SetStatus("试动结束；请观察是否正确运动，再确认此通道。数值响应不能替代视觉确认。");
    }
    public async Task ApplyAsync(ContinuousControlConfig config, CancellationToken ct = default)
    {
        var epoch = Interlocked.Increment(ref _controlEpoch);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (epoch != Interlocked.Read(ref _controlEpoch)) return;
            await StopCoreAsync(true).ConfigureAwait(false);
            _config = config.Snapshot();
            if (!_config.Enabled) { SetStatus("实验未启用"); return; }
            if (Model is not { } model || !_client.IsConnected) { SetStatus("未连接模型"); return; }
            if (!_config.Profiles.TryGetValue(model.Id, out var profile) || profile.Revision != model.Revision)
            { SetStatus("模型尚未验证；请刷新、试动后保存"); return; }
            if (model.HasActiveExpressions) { SetStatus("存在活动表情，请在 VTS 清除后刷新并恢复"); return; }
            var bindings = profile.Channels.Where(b => b.Verified && b.IsValid && model.Inputs.Contains(b.InputId) &&
                model.Parameters.Any(p => p.Id == b.ParameterId && b.Minimum >= p.Min && b.Maximum <= p.Max)).ToArray();
            if (bindings.Length == 0) { SetStatus("没有已验证且可用的通道"); return; }
            if (bindings.Select(b => b.Channel).Distinct().Count() != bindings.Length ||
                bindings.Select(b => b.ParameterId).Distinct().Count() != bindings.Length)
                throw new InvalidOperationException("通道或目标参数重复，请重新配置");
            // Custom inputs are global in VTS, whereas calibration is per model. Restore
            // their ranges after reconnect (including the legacy mouth input's defaults).
            foreach (var binding in bindings)
                await _client.CreateParameterAsync(binding.InputId, binding.Minimum, binding.Maximum, binding.Neutral, ct).ConfigureAwait(false);
            ValidateModel(profile);
            if (epoch != Interlocked.Read(ref _controlEpoch)) return;
            Volatile.Write(ref _allowedChannels, bindings.Where(b => AvatarChannels.All.Any(c => c.Name == b.Channel && c.AiControlled)).Select(b => b.Channel).ToArray());
            _director = new AvatarMotionDirector(_client, bindings);
            _director.Faulted += (_, error) => { _paused = true; SetStatus("控制暂停：" + error); };
            _paused = false;
            _director.Start();
            SetStatus($"连续控制运行中：{model.Name}，{bindings.Length} 个通道");
        }
        finally { _gate.Release(); }
    }
    public async Task ResumeAsync(CancellationToken ct = default)
    {
        await RefreshAsync(ct).ConfigureAwait(false);
        await ApplyAsync(_config, ct).ConfigureAwait(false);
    }
    public async Task StopAsync(bool neutral = true)
    {
        Interlocked.Increment(ref _controlEpoch);
        _paused = true;
        if (!neutral) _director?.Halt();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync(neutral).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private async Task StopCoreAsync(bool neutral)
    {
        _previewCancellation?.Cancel();
        _paused = true;
        Volatile.Write(ref _allowedChannels, []);
        var director = Interlocked.Exchange(ref _director, null);
        if (director is null) return;
        if (neutral && _client.IsConnected) await director.ReturnToNeutralAsync().ConfigureAwait(false);
        else await director.DisposeAsync().ConfigureAwait(false);
        DebugLog.Write($"[Avatar/VTS] stopped frames={director.ProducedFrames} sent={director.SentFrames} superseded={director.SupersededFrames}");
        SetStatus("控制已停止");
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true; _life.Cancel();
        _client.OnEvent -= OnEvent; _client.OnDisconnected -= OnDisconnected; _client.OnStateChanged -= OnState;
        await StopAsync().ConfigureAwait(false);
        if (_expressionMonitor is not null) await _expressionMonitor.ConfigureAwait(false);
        try { await Task.WhenAll(_background.Keys).ConfigureAwait(false); } catch { /* observed above */ }
    }
}
