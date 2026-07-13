using System.Diagnostics;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Core.Runtime;

internal static class AsrSidecarReadiness
{
    internal static async Task<LocalAsrHealth> WaitUntilReadyAsync(
        Func<CancellationToken, Task<LocalAsrHealth>> probeHealth,
        Func<bool> hasExited,
        Func<string> processDiagnostic,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);
        var lastHealth = new LocalAsrHealth(LocalAsrHealthStatus.Unknown, "No health response received.");

        try
        {
            while (true)
            {
                linkedCts.Token.ThrowIfCancellationRequested();
                if (hasExited())
                    throw new InvalidOperationException(
                        $"Local ASR sidecar exited before becoming ready. {processDiagnostic()}".Trim());

                lastHealth = await probeHealth(linkedCts.Token).ConfigureAwait(false);
                switch (lastHealth.Status)
                {
                    case LocalAsrHealthStatus.Ready:
                        return lastHealth;
                    case LocalAsrHealthStatus.Failed:
                        throw new InvalidOperationException(
                            $"Local ASR sidecar reported failed: {lastHealth.Detail ?? processDiagnostic()}".Trim());
                }

                await Task.Delay(pollInterval, linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Local ASR sidecar did not become ready within {timeout.TotalSeconds:0.#}s. " +
                $"Last health: {lastHealth.Status} ({lastHealth.Detail ?? "no detail"}). " +
                processDiagnostic());
        }
    }
}

internal sealed class AsrSidecarProcess : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);
    private readonly string _baseDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _operationLock = new();
    private readonly object _diagnosticLock = new();
    private readonly Queue<string> _diagnostics = new();
    private CancellationTokenSource? _startCts;
    private Process? _process;
    private bool _stopping;

    internal AsrSidecarProcess(string baseDir) => _baseDir = baseDir;

    internal event EventHandler<string>? Diagnostic;
    internal event EventHandler? UnexpectedExit;

    internal async Task<LocalAsrHealth> StartAsync(
        AsrConfig config,
        LocalAsrClient client,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource startCts;
        lock (_operationLock)
        {
            _startCts?.Cancel();
            startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _startCts = startCts;
        }

        var gateHeld = false;
        try
        {
            await _gate.WaitAsync(startCts.Token).ConfigureAwait(false);
            gateHeld = true;
            startCts.Token.ThrowIfCancellationRequested();
            await StopCoreAsync().ConfigureAwait(false);

            var scriptPath = Path.Combine(_baseDir, "asr_server.py");
            if (!File.Exists(scriptPath))
                throw new FileNotFoundException("Local ASR sidecar script was not found.", scriptPath);

            ClearDiagnostics();
            var startInfo = new ProcessStartInfo
            {
                FileName = config.PythonPath,
                Arguments = $"\"{scriptPath}\"",
                WorkingDirectory = _baseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (Uri.TryCreate(config.LocalAsrUrl, UriKind.Absolute, out var localAsrUri))
            {
                startInfo.Environment["ASR_HOST"] = localAsrUri.Host;
                startInfo.Environment["ASR_PORT"] = localAsrUri.Port.ToString();
            }
            if (!string.IsNullOrWhiteSpace(config.Model))
                startInfo.Environment["ASR_MODEL"] = config.Model;

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };
            process.OutputDataReceived += OnOutput;
            process.ErrorDataReceived += OnOutput;
            process.Exited += OnExited;

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("ASR-SIDECAR-001 Process.Start returned false.");
                _process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                return await AsrSidecarReadiness.WaitUntilReadyAsync(
                    client.GetHealthAsync,
                    () => process.HasExited,
                    BuildProcessDiagnostic,
                    timeout,
                    TimeSpan.FromMilliseconds(500),
                    startCts.Token).ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (!ReferenceEquals(_process, process))
                    process.Dispose();
                throw new InvalidOperationException(
                    $"ASR-SIDECAR-001 Managed Python runtime could not start: {config.PythonPath}", ex);
            }
            catch
            {
                if (!ReferenceEquals(_process, process))
                    process.Dispose();
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (gateHeld)
                _gate.Release();
            lock (_operationLock)
            {
                if (ReferenceEquals(_startCts, startCts))
                    _startCts = null;
            }
            startCts.Dispose();
        }
    }

    internal async Task StopAsync()
    {
        lock (_operationLock)
            _startCts?.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;

        _stopping = true;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeoutCts = new CancellationTokenSource(StopTimeout);
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    AddDiagnostic("Timed out waiting for the ASR sidecar process tree to exit.");
                }
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        finally
        {
            process.OutputDataReceived -= OnOutput;
            process.ErrorDataReceived -= OnOutput;
            process.Exited -= OnExited;
            process.Dispose();
            _stopping = false;
        }
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
            AddDiagnostic(e.Data);
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (!_stopping)
            UnexpectedExit?.Invoke(this, EventArgs.Empty);
    }

    private void AddDiagnostic(string line)
    {
        lock (_diagnosticLock)
        {
            _diagnostics.Enqueue(line);
            while (_diagnostics.Count > 20)
                _diagnostics.Dequeue();
        }
        Diagnostic?.Invoke(this, line);
    }

    private string BuildProcessDiagnostic()
    {
        string output;
        lock (_diagnosticLock)
            output = string.Join(Environment.NewLine, _diagnostics);

        var process = _process;
        var exit = process is { HasExited: true } ? $"Exit code {process.ExitCode}. " : "";
        return $"{exit}{output}".Trim();
    }

    private void ClearDiagnostics()
    {
        lock (_diagnosticLock)
            _diagnostics.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
