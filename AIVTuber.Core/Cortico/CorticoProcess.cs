using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIVTuber.Core.Config;

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
/// The sidecar paces pieces and drives the rig; the app synthesizes and plays every piece.
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
    private readonly ConcurrentDictionary<long, ICorticoAudioHandler> _turns = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _ready = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task _reader = Task.CompletedTask;
    private Task _errors = Task.CompletedTask;
    private long _sequence;
    private int _disposed;
    private long _lastStatusAt = Environment.TickCount64;
    public string ScriptGrammar { get; private set; } = "";
    public bool IsConnected { get; private set; }
    public int MaxHoldMs { get; private set; } = 5000;
    public bool IsAlive => !_lifetime.IsCancellationRequested &&
        Environment.TickCount64 - Interlocked.Read(ref _lastStatusAt) <= 2000;
    public event Action<string>? Diagnostic;

    private CorticoProcess(Process process) => _process = process;

    public static async Task<CorticoProcess> StartAsync(CorticoOptions options, string baseDir,
        VtsConfig vts, Action<string> diagnostic, CancellationToken ct)
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
        var self = new CorticoProcess(process);
        self.Diagnostic += diagnostic;
        self._reader = self.ReadAsync();
        self._errors = self.ReadErrorsAsync();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var reply = await self.CommandAsync(new { command = "init", config = new {
                vtsUrl = $"ws://{vts.Host}:{vts.Port}", options.Live2dDir, options.ModelProfile,
                options.PackDir, tokenPath = Path.Combine(baseDir, ".cortico-vts-token")
            } }, timeout.Token).ConfigureAwait(false);
            self.ScriptGrammar = reply.GetProperty("grammar").GetString()!;
            Interlocked.Exchange(ref self._lastStatusAt, Environment.TickCount64);
            return self;
        }
        catch { await self.DisposeAsync(); throw; }
    }

    public async Task<ICorticoStage> BeginAsync(ICorticoAudioHandler handler, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _sequence);
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ready[id] = ready;
        _turns[id] = handler;
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

        public Task PcmAsync(long pieceId, int sampleRate, byte[] pcm) =>
            owner.ReportAsync(new { kind = "pcm", requestId = id, pieceId, sampleRate, data = Convert.ToBase64String(pcm) });
        public Task SynthEndAsync(long pieceId) => owner.ReportAsync(new { kind = "synthEnd", requestId = id, pieceId });
        public Task SynthErrorAsync(long pieceId, string message) => owner.ReportAsync(new { kind = "synthError", requestId = id, pieceId, message });
        public Task StartedAsync(long pieceId) => owner.ReportAsync(new { kind = "started", requestId = id, pieceId });
        public Task EndedAsync(long pieceId) => owner.ReportAsync(new { kind = "ended", requestId = id, pieceId });
        public Task StoppedAsync(long pieceId) => owner.ReportAsync(new { kind = "stopped", requestId = id, pieceId });

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

    private async Task ReportAsync(object message)
    {
        if (Volatile.Read(ref _disposed) != 0 || _lifetime.IsCancellationRequested) return;
        try { await SendAsync(message, _lifetime.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        { Diagnostic?.Invoke($"report dropped: {ex.Message}"); }
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
                    if (message.TryGetProperty("maxHoldMs", out var hold) && hold.TryGetInt32(out var holdMs) && holdMs > 0)
                        MaxHoldMs = holdMs;
                    Interlocked.Exchange(ref _lastStatusAt, Environment.TickCount64);
                    Diagnostic?.Invoke(line);
                }
                else if (kind == "ready")
                {
                    if (_ready.TryGetValue(message.GetProperty("requestId").GetInt64(), out var ready)) ready.TrySetResult();
                }
                else if (kind is "synth" or "cancelSynth" or "play" or "stop" or "cue" or "aborted")
                    Dispatch(kind, message);
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

    private void Dispatch(string kind, JsonElement message)
    {
        if (!_turns.TryGetValue(message.GetProperty("requestId").GetInt64(), out var handler)) return;
        var pieceId = message.TryGetProperty("pieceId", out var piece) ? piece.GetInt64() : 0;
        try
        {
            switch (kind)
            {
                case "synth": handler.Synth(pieceId, message.GetProperty("text").GetString() ?? ""); break;
                case "cancelSynth": handler.CancelSynth(pieceId); break;
                case "play": handler.Play(pieceId); break;
                case "stop": handler.Stop(pieceId); break;
                case "cue": handler.Cue(); break;
                case "aborted": handler.Aborted(message.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : ""); break;
            }
        }
        catch (Exception ex) { Diagnostic?.Invoke($"{kind} handler failed: {ex.Message}"); }
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
        _process.Dispose(); _lifetime.Dispose(); _write.Dispose();
    }
}
