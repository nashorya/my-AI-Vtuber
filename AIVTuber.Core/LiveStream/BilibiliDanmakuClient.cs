using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIVTuber.Core.Config;
using AIVTuber.Core;

namespace AIVTuber.Core.LiveStream;

/// <summary>
/// Bilibili danmaku client using a local bridge subprocess (Go exe, Python fallback).
/// Receives danmaku via local HTTP endpoint, auto-restarts up to 3 times on crash.
/// </summary>
public sealed class BilibiliDanmakuClient : IDisposable
{
    private readonly BilibiliConfig _config;
    private Process? _pythonProcess;
    private HttpListener? _httpListener;
    private CancellationTokenSource? _cts;
    private int _restartCount;
    private bool _disposed;

    public event EventHandler<Danmaku>? OnDanmaku;
    /// <summary>Raised when a PK match starts and the opposing streamer has been resolved.
    /// Only fires when <see cref="BilibiliConfig.PkNotice"/> is enabled.</summary>
    public event EventHandler<PkOpponent>? OnPkStarted;
    /// <summary>Raised when the bridge reports the current PK match ended.</summary>
    public event EventHandler? OnPkEnded;
    public event EventHandler<string>? OnProcessExited;
    public event EventHandler<string>? OnError;
    /// <summary>Raised for each stdout/stderr line emitted by the bridge (for diagnostics).</summary>
    public event EventHandler<string>? OnBridgeOutput;

    public BilibiliDanmakuClient(BilibiliConfig config) => _config = config;

    /// <summary>True while the Python bridge subprocess is alive. Reflects real bridge health,
    /// not just whether this client object exists.</summary>
    public bool IsBridgeRunning => _pythonProcess is { HasExited: false };

    /// <summary>Starts the HTTP endpoint and Python bridge subprocess.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add($"http://localhost:{_config.PushPort}/danmaku/");
        _httpListener.Prefixes.Add($"http://localhost:{_config.PushPort}/pk/");
        _httpListener.Start();
        _ = AcceptLoopAsync(_cts.Token);
        StartBridgeProcess();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Stops the HTTP listener and kills the bridge process.</summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_pythonProcess is { HasExited: false })
        {
            try { _pythonProcess.Kill(entireProcessTree: true); _pythonProcess.WaitForExit(3000); }
            catch { }
        }
        if (_httpListener is not null)
        {
            try { await Task.Run(() => _httpListener.Stop()).ConfigureAwait(false); }
            catch { }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _httpListener is not null)
        {
            try
            {
                var ctx = await _httpListener!.GetContextAsync().ConfigureAwait(false);
                _ = ProcessRequestAsync(ctx, ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { OnError?.Invoke(this, $"HTTP error: {ex.Message}"); }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

            var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
            if (path.StartsWith("/pk", StringComparison.OrdinalIgnoreCase))
            {
                if (PkOpponent.IsEndPush(body))
                    OnPkEnded?.Invoke(this, EventArgs.Empty);
                else if (PkOpponent.TryParse(body, out var opponent))
                    OnPkStarted?.Invoke(this, opponent!);
            }
            else
            {
                var data = JsonSerializer.Deserialize<DanmakuPush>(body);
                if (data is not null && !string.IsNullOrEmpty(data.Content))
                {
                    OnDanmaku?.Invoke(this, new Danmaku
                    {
                        Uid = data.Uid ?? "0", Username = data.Username ?? "unknown",
                        Content = data.Content, Timestamp = DateTime.UtcNow, Platform = "bilibili"
                    });
                }
            }
            ctx.Response.StatusCode = 200;
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"status\":\"ok\"}"), ct);
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"Request error: {ex.Message}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    internal static (string FileName, string Arguments) ResolveBridgeCommand(string contentRoot, string pythonPath)
    {
        var exe = Path.Combine(contentRoot, "danmaku_bridge.exe");
        if (File.Exists(exe))
            return (exe, string.Empty);

        var script = Path.Combine(contentRoot, "danmaku_bridge.py");
        var args = File.Exists(script) ? $"\"{script}\"" : "danmaku_bridge.py";
        return (pythonPath, args);
    }

    private void StartBridgeProcess()
    {
        KillOrphanedProcess();
        var contentRoot = AppPaths.ContentRoot;
        var (fileName, arguments) = ResolveBridgeCommand(contentRoot, _config.PythonPath);
        var si = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = contentRoot,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        si.Environment["ROOM_ID"] = _config.RoomId.ToString();
        si.Environment["SESSDATA"] = _config.Sessdata;
        si.Environment["BILI_JCT"] = _config.BiliJct;
        si.Environment["BUVID3"] = _config.Buvid3;
        // Trailing slash so it matches the HttpListener prefix "/danmaku/" (a POST to "/danmaku"
        // without the slash would not match the prefix).
        si.Environment["PUSH_URL"] = $"http://localhost:{_config.PushPort}/danmaku/";
        si.Environment["PK_PUSH_URL"] = $"http://localhost:{_config.PushPort}/pk/";
        si.Environment["PK_NOTICE"] = _config.PkNotice ? "1" : "0";
        // UseShellExecute=false makes the child inherit our full environment. If the host
        // shell exports a proxy, the bridge's Bilibili API calls detour through it and
        // degrade from ~0.1s to seconds. These APIs never need a proxy.
        si.Environment["no_proxy"] = "*";
        si.Environment["NO_PROXY"] = "*";

        _pythonProcess = new Process { StartInfo = si, EnableRaisingEvents = true };
        _pythonProcess.Exited += OnPythonProcessExited;
        _pythonProcess.OutputDataReceived += (_, ev) => { if (ev.Data is not null) OnBridgeOutput?.Invoke(this, ev.Data); };
        _pythonProcess.ErrorDataReceived  += (_, ev) => { if (ev.Data is not null) OnBridgeOutput?.Invoke(this, ev.Data); };
        _pythonProcess.Start();
        _pythonProcess.BeginOutputReadLine();
        _pythonProcess.BeginErrorReadLine();
        OnBridgeOutput?.Invoke(this, $"桥进程已启动 pid={_pythonProcess.Id} 文件={fileName} 房间={_config.RoomId} 推送端口={_config.PushPort}");
    }

    private void OnPythonProcessExited(object? sender, EventArgs e)
    {
        var exitCode = (sender as Process)?.ExitCode;
        if (_cts?.IsCancellationRequested ?? true) return;
        if (++_restartCount > 3) { OnError?.Invoke(this, $"桥进程已退出(code={exitCode})并连续崩溃3次，已停止重启"); return; }

        OnProcessExited?.Invoke(this, $"桥进程已退出(code={exitCode})，5秒后重启(第{_restartCount}次)");
        Task.Delay(5000).ContinueWith(_ => { if (!_cts.IsCancellationRequested) StartBridgeProcess(); });
    }

    private void KillOrphanedProcess()
    {
        // `lsof` is Unix-only; on Windows this best-effort cleanup is skipped (HttpListener will
        // surface a clear error if the push port is genuinely in use).
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "lsof", Arguments = $"-i :{_config.PushPort} -t",
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            });
            if (proc is null) return;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(line.Trim(), out var pid) && pid != Environment.ProcessId)
                    try { Process.GetProcessById(pid).Kill(); } catch { }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        _pythonProcess?.Dispose();
        _httpListener?.Close();
        _cts?.Dispose();
    }

    private sealed class DanmakuPush
    {
        [JsonPropertyName("uid")] public string? Uid { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
    }
}