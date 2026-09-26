using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Core.Cortico;

/// <summary>
/// Experimental, restart-only settings stored beside config.json. Kept separate so the
/// existing settings editor cannot silently reset a model calibration or switch owners.
/// </summary>
public sealed class CorticoOptions
{
    public bool Enabled { get; set; }
    public string NodePath { get; set; } = "node";
    public string SidecarPath { get; set; } = "sidecar/cortico";
    public string Live2dDir { get; set; } = "";
    public string ModelProfile { get; set; } = "auto";
    public string PackDir { get; set; } = "";
    public string AudioDevice { get; set; } = "";

    public static CorticoOptions Load(string baseDir)
    {
        var file = Path.Combine(baseDir, "cortico.json");
        return File.Exists(file)
            ? JsonSerializer.Deserialize<CorticoOptions>(File.ReadAllText(file), CorticoProcess.Json) ?? new()
            : new();
    }
}

/// <summary>
/// Bounded command lifetime over JSON-lines IPC. No provider credentials cross into Node.
/// Upstream owns playback timing; synthesis is supplied by the current app TTS provider.
/// </summary>
public sealed class CorticoProcess : ICorticoPerformance, IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    private readonly Process _process;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<long, Turn> _turns = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _ready = new();
    private readonly ConcurrentDictionary<long, Task> _callbacks = new();
    private readonly Func<ITtsClient> _tts;
    private readonly Func<TtsConfig> _ttsConfig;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _reader = Task.CompletedTask;
    private Task _errors = Task.CompletedTask;
    private long _sequence;
    private long _callbackSequence;
    private int _disposed;
    public string ScriptGrammar { get; private set; } = "";
    public bool IsConnected { get; private set; }
    public event Action<string>? Diagnostic;
    private sealed record Turn(Func<string, bool> Authorize, Action Started, CancellationToken Token);

    private CorticoProcess(Process process, Func<ITtsClient> tts, Func<TtsConfig> ttsConfig)
    { _process = process; _tts = tts; _ttsConfig = ttsConfig; }

    public static async Task<CorticoProcess> StartAsync(CorticoOptions options, string baseDir,
        VtsConfig vts, Func<ITtsClient> tts, Func<TtsConfig> ttsConfig,
        Action<string> diagnostic, CancellationToken ct)
    {
        var directory = Path.GetFullPath(options.SidecarPath, baseDir);
        var start = new ProcessStartInfo(options.NodePath)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("--import"); start.ArgumentList.Add("tsx"); start.ArgumentList.Add("host.ts");
        var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start Cortico sidecar");
        var self = new CorticoProcess(process, tts, ttsConfig);
        self.Diagnostic += diagnostic;
        self._reader = self.ReadAsync();
        self._errors = self.ReadErrorsAsync();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var reply = await self.CommandAsync(new { command = "init", config = new {
                vtsUrl = $"ws://{vts.Host}:{vts.Port}", options.Live2dDir, options.ModelProfile,
                options.PackDir, options.AudioDevice, tokenPath = Path.Combine(baseDir, ".cortico-vts-token")
            } }, timeout.Token).ConfigureAwait(false);
            self.ScriptGrammar = reply.GetProperty("grammar").GetString()!;
            return self;
        }
        catch { await self.DisposeAsync(); throw; }
    }

    public async Task<string> PrepareAsync(string script, CancellationToken ct)
    {
        var reply = await CommandAsync(new { command = "prepare", script }, ct).ConfigureAwait(false);
        return reply.GetProperty("spoken").GetString() ?? "";
    }

    public async Task<ICorticoStage> BeginAsync(Func<string, bool> authorize, Action started, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _sequence);
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ready[id] = ready;
        _turns[id] = new Turn(authorize, started, timeout.Token);
        var stage = new Stage(this, id, timeout);
        stage.Result = CommandAsync(new { command = "perform" }, timeout.Token, id);
        try
        {
            // The host accepts segments only once it has taken the turn (it may first wait for a
            // model switch to finish resolving the new rig's mapping).
            var first = await Task.WhenAny(ready.Task, stage.Result).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (first == stage.Result) await stage.Result.ConfigureAwait(false); // surfaces the refusal
            return stage;
        }
        catch
        {
            await stage.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally { _ready.TryRemove(id, out _); }
    }

    public async Task InterruptAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0 || _lifetime.IsCancellationRequested) return;
        // Fenced: only performances begun before this call are stopped, never a later turn.
        await CommandAsync(new { command = "interrupt", fence = Interlocked.Read(ref _sequence) }, ct).ConfigureAwait(false);
    }

    private sealed class Stage(CorticoProcess owner, long id, CancellationTokenSource timeout) : ICorticoStage
    {
        public Task<JsonElement> Result = Task.FromResult(default(JsonElement));
        private int _disposed;

        public async Task FeedAsync(string script, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await owner.CommandAsync(new { command = "feed", requestId = id, script }, linked.Token).ConfigureAwait(false);
        }

        public async Task CompleteAsync(CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await owner.CommandAsync(new { command = "end", requestId = id }, linked.Token).ConfigureAwait(false);
            await Result.WaitAsync(linked.Token).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner._turns.TryRemove(id, out _);
            var unfinished = !Result.IsCompletedSuccessfully;
            if (!unfinished)
            {
                timeout.Dispose();
                return;
            }
            // The app side gave up (stop, pause, sign-out, error): the sidecar may still be
            // performing. Wait for the cut to be acknowledged before the next turn can speak.
            timeout.Cancel();
            if (!owner._lifetime.IsCancellationRequested && Volatile.Read(ref owner._disposed) == 0)
            {
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await owner.CommandAsync(new { command = "interrupt", fence = id }, stopTimeout.Token).ConfigureAwait(false); }
                catch { owner.Kill(); }
            }
            try { await Result.ConfigureAwait(false); } catch { /* reported by the caller's own await */ }
            timeout.Dispose();
        }
    }

    private async Task<JsonElement> CommandAsync(object body, CancellationToken ct, long? requestId = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        linked.CancelAfter(TimeSpan.FromMinutes(3));
        var id = requestId ?? Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(body, Json))!;
            fields["id"] = JsonSerializer.SerializeToElement(id);
            await SendAsync(fields, linked.Token).ConfigureAwait(false);
            var result = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            if (result.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
            return result;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task SendAsync(object message, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message, Json).AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _write.Release(); }
    }

    private async Task ReadAsync()
    {
        Exception failure = new IOException("Cortico sidecar exited");
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                var message = doc.RootElement.Clone();
                var kind = message.GetProperty("kind").GetString();
                if (kind == "result")
                {
                    if (_pending.TryGetValue(message.GetProperty("id").GetInt64(), out var p)) p.TrySetResult(message);
                }
                else if (kind == "status")
                {
                    IsConnected = message.GetProperty("connected").GetBoolean();
                    Diagnostic?.Invoke(line);
                }
                else if (kind == "ready")
                {
                    if (_ready.TryGetValue(message.GetProperty("requestId").GetInt64(), out var ready)) ready.TrySetResult();
                }
                else if (kind is "tts" or "authorize" or "started")
                {
                    var callbackId = Interlocked.Increment(ref _callbackSequence);
                    var task = HandleCallbackAsync(message);
                    _callbacks[callbackId] = task;
                    _ = task.ContinueWith(_ => _callbacks.TryRemove(callbackId, out var ignored), TaskScheduler.Default);
                }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            IsConnected = false;
            foreach (var item in _pending.Values) item.TrySetException(failure);
            await _lifetime.CancelAsync();
        }
    }

    private async Task HandleCallbackAsync(JsonElement message)
    {
        var kind = message.GetProperty("kind").GetString();
        var requestId = message.GetProperty("requestId").GetInt64();
        var id = message.TryGetProperty("id", out var idValue) ? idValue.GetInt64() : 0;
        try
        {
            if (!_turns.TryGetValue(requestId, out var turn) || turn.Token.IsCancellationRequested)
                throw new OperationCanceledException("Turn cancelled");
            if (kind == "started") { turn.Started(); return; }
            if (kind == "authorize")
            {
                var text = message.TryGetProperty("text", out var piece) ? piece.GetString() ?? "" : "";
                bool allowed;
                try { allowed = turn.Authorize(text); }
                catch (Exception ex) { Diagnostic?.Invoke($"authorize failed: {ex.Message}"); allowed = false; }
                await SendAsync(new { kind = "reply", id, allowed }, _lifetime.Token).ConfigureAwait(false);
                return;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(turn.Token, _lifetime.Token);
            using var pcm = new MemoryStream();
            var config = _ttsConfig();
            await foreach (var chunk in _tts().StreamAsync(message.GetProperty("text").GetString()!, config.VoiceId, null, linked.Token))
            {
                if (pcm.Length + chunk.Length > 32 * 1024 * 1024) throw new IOException("TTS audio exceeded preview buffer limit");
                pcm.Write(chunk);
            }
            linked.Token.ThrowIfCancellationRequested();
            await SendAsync(new { kind = "reply", id, pcm = Convert.ToBase64String(pcm.ToArray()), sampleRate = config.SampleRate }, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (kind == "started") return;
            try { await SendAsync(new { kind = "reply", id, error = ex.Message }, _lifetime.Token).ConfigureAwait(false); }
            catch { /* The reader owns process-exit propagation. */ }
        }
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
                Diagnostic?.Invoke(line);
        }
        catch (OperationCanceledException) { }
    }

    private void Kill() { try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _process.StandardInput.Close();
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { Kill(); }
        await _lifetime.CancelAsync();
        await Task.WhenAll(_reader, _errors).ConfigureAwait(false);
        await Task.WhenAll(_callbacks.Values).ConfigureAwait(false);
        _process.Dispose(); _lifetime.Dispose(); _write.Dispose();
    }
}
