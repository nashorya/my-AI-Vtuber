using System.Diagnostics;
using System.Runtime.CompilerServices;
using AIVTuber.Core.Config;
using AIVTuber.Core.Cortico;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public sealed class CorticoProcessTests
{
    [SkippableFact]
    public async Task RealNodeHost_RoundTripsAudioAndCancelsPendingSynthesis()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "sidecar/cortico/host.ts")))
            directory = directory.Parent;
        Skip.If(directory is null, "Source checkout is required");
        var sidecar = Path.Combine(directory!.FullName, "sidecar/cortico");
        Skip.IfNot(Directory.Exists(Path.Combine(sidecar, "node_modules/tsx")), "Run npm ci in sidecar/cortico first");
        var temp = Path.Combine(Path.GetTempPath(), "cortico-ipc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var start = new ProcessStartInfo("node") { WorkingDirectory = sidecar, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--import"); start.ArgumentList.Add("tsx"); start.ArgumentList.Add("tests/fake-vts.ts");
        using var server = Process.Start(start)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var port = int.Parse((await server.StandardOutput.ReadLineAsync(deadline.Token))!);
            var tts = new FixtureTts();
            await using var bridge = await CorticoProcess.StartAsync(new CorticoOptions {
                Enabled = true, SidecarPath = sidecar, AudioDevice = "none"
            }, temp, new VtsConfig { Host = "127.0.0.1", Port = port }, () => tts,
                () => new TtsConfig { SampleRate = 16000 }, _ => {}, deadline.Token);
            Assert.Contains("点头", bridge.ScriptGrammar);
            Assert.Equal("你好", await bridge.PrepareAsync("<微笑>你好【点头】", deadline.Token));
            var starts = 0;
            await bridge.PerformAsync("<微笑>你好", _ => true, () => starts++, deadline.Token);
            Assert.True(starts > 0);
            tts.Block = true;
            using var cancel = new CancellationTokenSource();
            var pending = bridge.PerformAsync("这次取消", _ => true, () => {}, cancel.Token);
            await tts.Blocked.Task.WaitAsync(deadline.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            tts.Block = false;
            await bridge.PerformAsync("再试一次", _ => true, () => starts++, deadline.Token);
            Assert.True(starts >= 2);
        }
        finally
        {
            server.StandardInput.Close();
            try { await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { server.Kill(true); }
            Directory.Delete(temp, true);
        }
    }

    private sealed class FixtureTts : ITtsClient
    {
        public bool Block;
        public TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<byte[]> StreamAsync(string text, string voiceId, string? emotion,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Block) { Blocked.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            var pcm = new byte[6400];
            for (var i = 0; i < pcm.Length / 2; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(3000 * Math.Sin(i / 8.0)));
            yield return pcm;
        }
    }
}
