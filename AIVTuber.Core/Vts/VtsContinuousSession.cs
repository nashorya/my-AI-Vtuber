using System.Text.Json;
using AIVTuber.Core.Avatar;
using AIVTuber.Core.Diagnostics;

namespace AIVTuber.Core.Vts;

public sealed record VtsParameter(string Id, float Min, float Max, float Default, float Value);
public sealed record VtsModelSnapshot(string Id, string Name, string Revision,
    IReadOnlyList<VtsParameter> Parameters, IReadOnlySet<string> Inputs, bool HasActiveExpressions)
{
    public IReadOnlyList<VtsParameter> DefaultInputs { get; init; } = [];
}

/// <summary>Model discovery, verification and continuous ownership. No UI or audio dependency.</summary>
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
    private int _suppressModelEvents;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _reloadedModels = new();
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
    public string[] DeclaredChannels => Volatile.Read(ref _allowedChannels).ToArray();
    public string[] AllowedChannels => _paused ? [] : DeclaredChannels;
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
        if (_paused)
        {
            DebugLog.Write($"[Avatar/VTS] generation={generation} model={Model?.Id} rejected: injection paused");
            return;
        }
        if (generation <= Interlocked.Read(ref _canceledThrough) ||
            generation != Interlocked.Read(ref _turnGeneration))
        {
            DebugLog.Write($"[Avatar/VTS] generation={generation} model={Model?.Id} rejected: turn superseded");
            return;
        }
        if (Interlocked.Read(ref _turnEpoch) != Interlocked.Read(ref _controlEpoch))
        {
            DebugLog.Write($"[Avatar/VTS] generation={generation} model={Model?.Id} rejected: control rebuilt after turn started");
            return;
        }
        var allowed = AllowedChannels.ToHashSet();
        var filtered = intent.Targets.Where(p => allowed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        if (filtered.Count == 0) return;
        director?.Submit(generation, intent with { Targets = filtered });
        DebugLog.Write($"[Avatar/VTS] generation={generation} model={Model?.Id} targets={string.Join(',', filtered.Select(p => p.Key + "=" + p.Value.ToString("0.##")))} accepted");
    }
    public void Cancel(long generation)
    {
        long old;
        do { old = Interlocked.Read(ref _canceledThrough); if (generation <= old) break; }
        while (Interlocked.CompareExchange(ref _canceledThrough, generation, old) != old);
        _director?.Cancel(generation);
    }
    public void OnRms(float rms) { _director?.OnRms(rms); }
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
        if (response.MessageType is not ("ModelLoadedEvent" or "ModelConfigChangedEvent")) return;
        _receivedModelEvent = true;
        if (Volatile.Read(ref _suppressModelEvents) != 0) return;
        // Reloading the same model is not a switch. Mapping edits still invalidate custom profiles.
        if (response.MessageType == "ModelLoadedEvent" && IsSameLoadedModel(response)) return;
        if (response.MessageType == "ModelConfigChangedEvent" && _config.UseBuiltInTracking) return;
        InvalidateControl();
        _revision = Guid.NewGuid().ToString("N");
        Supervise(RefreshAfterEventAsync());
    }

    private bool IsSameLoadedModel(VtsResponse response)
    {
        if (response.Data is not { } data || data.ValueKind != JsonValueKind.Object) return false;
        if (data.TryGetProperty("modelLoaded", out var loaded) &&
            loaded.ValueKind is JsonValueKind.False or JsonValueKind.Null)
            return false;
        if (!data.TryGetProperty("modelID", out var id) || id.GetString() is not { Length: > 0 } modelId)
            return false;
        return Model is { } current && string.Equals(current.Id, modelId, StringComparison.OrdinalIgnoreCase);
    }
    private async Task RefreshAfterEventAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            for (var attempt = 0; attempt < 8 && !_disposed; attempt++)
            {
                var revision = _revision;
                try
                {
                    await StopAsync(false).ConfigureAwait(false);
                    var model = await RefreshAsync(_life.Token).ConfigureAwait(false);
                    if (model is null)
                    {
                        SetStatus("正在等待新模型加载");
                        await Task.Delay(400, _life.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (_config.Enabled && _config.UseBuiltInTracking)
                        await ApplyAsync(_config, _life.Token).ConfigureAwait(false);
                    if (revision == _revision) return;
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("请刷新", StringComparison.Ordinal))
                {
                    await Task.Delay(300, _life.Token).ConfigureAwait(false);
                }
            }
            if (!_disposed && (Model is null || _paused))
                SetStatus("换模型后未接上，请点恢复控制");
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
        Interlocked.Increment(ref _suppressModelEvents);
        try
        {
            await _client.ConnectAsync(ct).ConfigureAwait(false);
            foreach (var eventName in new[] { "ModelLoadedEvent", "ModelConfigChangedEvent" })
                await _client.SubscribeAsync(eventName, ct).ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
            // Reconnect only restores idle, never a previous utterance. Previously verified mappings
            // are checked again against actual output ranges; manual config events invalidate Revision.
            await ApplyAsync(_config, ct).ConfigureAwait(false);
        }
        finally { Interlocked.Decrement(ref _suppressModelEvents); }
    }
    public async Task<VtsModelSnapshot?> RefreshAsync(CancellationToken ct = default)
    {
        var revision = _revision;
        var current = await _client.QueryAsync("CurrentModelRequest", ct).ConfigureAwait(false);
        if (!current.GetProperty("modelLoaded").GetBoolean())
        {
            await StopAsync(false).ConfigureAwait(false);
            Volatile.Write(ref _model, null);
            Volatile.Write(ref _allowedChannels, []);
            SetStatus("VTS 未加载模型");
            return null;
        }
        var id = current.GetProperty("modelID").GetString()!;
        if (Model is { } previous && previous.Id != id) await StopAsync(false).ConfigureAwait(false);
        var parameters = await _client.QueryAsync("Live2DParameterListRequest", ct).ConfigureAwait(false);
        var inputs = await _client.QueryAsync("InputParameterListRequest", ct).ConfigureAwait(false);
        var expressions = await _client.QueryAsync("ExpressionStateRequest", ct).ConfigureAwait(false);
        if (parameters.GetProperty("modelID").GetString() != id) return null;
        var list = parameters.GetProperty("parameters").EnumerateArray().Select(p => new VtsParameter(
            p.GetProperty("name").GetString()!, p.GetProperty("min").GetSingle(), p.GetProperty("max").GetSingle(),
            p.GetProperty("defaultValue").GetSingle(), p.GetProperty("value").GetSingle())).ToArray();
        if (inputs.TryGetProperty("modelID", out var inputModel) && inputModel.GetString() != id)
            return null;
        var defaultInputs = new List<VtsParameter>();
        if (inputs.TryGetProperty("defaultParameters", out var defaults))
            foreach (var p in defaults.EnumerateArray())
                if (p.TryGetProperty("min", out var min) && min.TryGetSingle(out var lo) &&
                    p.TryGetProperty("max", out var max) && max.TryGetSingle(out var hi) &&
                    p.TryGetProperty("defaultValue", out var def) && def.TryGetSingle(out var rest))
                    defaultInputs.Add(new(p.GetProperty("name").GetString()!, lo, hi, rest, rest));
        var inputNames = new HashSet<string>();
        foreach (var group in new[] { "defaultParameters", "customParameters" })
            if (inputs.TryGetProperty(group, out var array))
                foreach (var p in array.EnumerateArray()) inputNames.Add(p.GetProperty("name").GetString()!);
        var active = expressions.TryGetProperty("expressions", out var expressionList) &&
            expressionList.EnumerateArray().Any(p => p.TryGetProperty("active", out var a) && a.GetBoolean());
        if (revision != _revision) return null;
        if (!_receivedModelEvent && Model is null && _config.Profiles.TryGetValue(id, out var saved) && !string.IsNullOrEmpty(saved.Revision))
            _revision = saved.Revision;
        var model = new VtsModelSnapshot(id, current.GetProperty("modelName").GetString()!, _revision, list, inputNames, active) { DefaultInputs = defaultInputs };
        Volatile.Write(ref _model, model);
        SetStatus(_config.UseBuiltInTracking
            ? $"模型：{model.Name}；内置面捕输入已读取，效果取决于模型已有映射；保存启用或点击恢复控制"
            : $"模型：{model.Name}；{list.Length} 个参数；请配置并验证映射");
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
            if (epoch != Interlocked.Read(ref _controlEpoch)) throw new OperationCanceledException("试动已被停止");
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
            if (epoch != Interlocked.Read(ref _controlEpoch)) throw new OperationCanceledException("试动已被停止");
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
        Interlocked.Increment(ref _suppressModelEvents);
        string? reloadId = null;
        VtsTrackingBackend? tracking = null;
        AvatarChannelBinding[]? trackingBindings = null;
        var modelName = "";
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (epoch != Interlocked.Read(ref _controlEpoch)) return;
                await StopCoreAsync(true).ConfigureAwait(false);
                _config = config.Snapshot();
                if (!_config.Enabled) { SetStatus("实验未启用"); return; }
                if (Model is not { } model || !_client.IsConnected) { SetStatus("未连接模型"); return; }
                if (_config.UseBuiltInTracking)
                {
                    var baseline = new VtsTrackingBackend(_client, model.DefaultInputs);
                    if (baseline.CreateBindings().Length == 0)
                    {
                        SetStatus("没有可用的标准面捕输入，请检查 VTS 版本或使用高级映射");
                        return;
                    }
                    var extras = await EnsureBodyInputsAsync(ct).ConfigureAwait(false);
                    tracking = new VtsTrackingBackend(_client, model.DefaultInputs.Concat(extras));
                    trackingBindings = tracking.CreateBindings();
                    Volatile.Write(ref _allowedChannels, trackingBindings
                        .Where(b => AvatarChannels.All.Any(c => c.Name == b.Channel && c.AiControlled))
                        .Select(b => b.Channel).ToArray());
                    modelName = model.Name;
                    var firstLoad = _reloadedModels.TryAdd(model.Id, 0);
                    if (VtsModelFile.TryPinLoadedModel(model.Id) || firstLoad)
                        reloadId = model.Id;
                }
                else
                {
                    if (!_config.Profiles.TryGetValue(model.Id, out var profile) || profile.Revision != model.Revision)
                    { SetStatus("模型尚未验证；请刷新、试动后保存"); return; }
                    var bindings = profile.Channels.Where(b => b.Verified && b.IsValid && model.Inputs.Contains(b.InputId) &&
                        model.Parameters.Any(p => p.Id == b.ParameterId && b.Minimum >= p.Min && b.Maximum <= p.Max)).ToArray();
                    if (bindings.Length == 0) { SetStatus("没有已验证且可用的通道"); return; }
                    if (bindings.Select(b => b.Channel).Distinct().Count() != bindings.Length ||
                        bindings.Select(b => b.ParameterId).Distinct().Count() != bindings.Length)
                        throw new InvalidOperationException("通道或目标参数重复，请重新配置");
                    // Custom inputs are global in VTS, whereas calibration is per model. Restore
                    // their ranges after reconnect.
                    foreach (var binding in bindings)
                        await _client.CreateParameterAsync(binding.InputId, binding.Minimum, binding.Maximum, binding.Neutral, ct).ConfigureAwait(false);
                    ValidateModel(profile);
                    if (epoch != Interlocked.Read(ref _controlEpoch)) return;
                    StartDirector(_client, bindings);
                    SetStatus($"连续控制运行中：{model.Name}，{bindings.Length} 个通道");
                    return;
                }
            }
            finally { _gate.Release(); }

            if (reloadId is not null)
            {
                SetStatus("已把身体反向、步伐钉死，正在重新加载模型");
                try { await _client.LoadModelAsync(reloadId, ct).ConfigureAwait(false); }
                catch (Exception ex) when (!_disposed) { SetStatus("重新加载模型失败：" + ex.Message); }
            }
            if (tracking is null || trackingBindings is not { Length: > 0 }) return;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (epoch != Interlocked.Read(ref _controlEpoch)) return;
                StartDirector(tracking, trackingBindings);
                SetStatus($"内置面捕控制运行中：{modelName}，{trackingBindings.Length} 个输入通道；复用已有映射，未验证视觉效果");
            }
            finally { _gate.Release(); }
        }
        finally { Interlocked.Decrement(ref _suppressModelEvents); }
    }
    private async Task<List<VtsParameter>> EnsureBodyInputsAsync(CancellationToken ct)
    {
        var extras = new List<VtsParameter>();
        foreach (var channel in AvatarChannels.All.Where(c => c.Name.StartsWith("body", StringComparison.Ordinal)))
        {
            var id = channel.InputId;
            try { await _client.CreateParameterAsync(id, -1, 1, 0, ct).ConfigureAwait(false); }
            catch (VtsApiException) { /* already exists */ }
            extras.Add(new VtsParameter(id, -1, 1, 0, 0));
        }
        return extras;
    }

    private void StartDirector(IAvatarParameterBackend backend, AvatarChannelBinding[] bindings)
    {
        Volatile.Write(ref _allowedChannels, bindings.Where(b => AvatarChannels.All.Any(c => c.Name == b.Channel && c.AiControlled)).Select(b => b.Channel).ToArray());
        _director = new AvatarMotionDirector(backend, bindings);
        _director.Faulted += (_, error) => { _paused = true; SetStatus("控制暂停：" + error); };
        _paused = false;
        _director.Start();
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
        try { await Task.WhenAll(_background.Keys).ConfigureAwait(false); } catch { /* observed above */ }
    }
}
