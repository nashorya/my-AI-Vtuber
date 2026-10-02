using System.Diagnostics;
using AIVTuber.Core.Config;
using AIVTuber.Core.Cortico;

namespace AIVTuber.Tests;

public sealed class CorticoProcessTests
{
    /// <summary>Plays the app's side of the protocol: tone PCM for every synth, started/ended for every play.</summary>
    private sealed class ToneApp : ICorticoAudioHandler
    {
        public ICorticoStage Stage = null!;
        public readonly List<string> Synth = [], Played = [], Log = [];
        private readonly Dictionary<long, string> _texts = [];
        public bool HoldSynth;
        public readonly TaskCompletionSource SynthHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void ICorticoAudioHandler.Synth(long pieceId, string text)
        {
            lock (Log) { Synth.Add(text); _texts[pieceId] = text; Log.Add($"synth:{pieceId}"); }
            if (HoldSynth) { SynthHeld.TrySetResult(); return; }
            _ = Task.Run(async () =>
            {
                var pcm = new byte[6400];
                for (var i = 0; i < pcm.Length / 2; i++)
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(3000 * Math.Sin(i / 8.0)));
                await Stage.PcmAsync(pieceId, 16000, pcm);
                await Stage.SynthEndAsync(pieceId);
            });
        }
        void ICorticoAudioHandler.CancelSynth(long pieceId) { lock (Log) Log.Add($"cancelSynth:{pieceId}"); }
        void ICorticoAudioHandler.Play(long pieceId)
        {
            lock (Log) { Played.Add(_texts[pieceId]); Log.Add($"play:{pieceId}"); }
            _ = Task.Run(async () => { await Stage.StartedAsync(pieceId); await Task.Delay(200); await Stage.EndedAsync(pieceId); });
        }
        void ICorticoAudioHandler.Stop(long pieceId) { lock (Log) Log.Add($"stop:{pieceId}"); }
        void ICorticoAudioHandler.Cue() { lock (Log) Log.Add("cue"); }
        void ICorticoAudioHandler.Aborted(string reason) { lock (Log) Log.Add($"aborted:{reason}"); }
    }

    [SkippableFact]
    public async Task RealNodeHost_PacesAppAudio_AndCancelsPendingSynthesis()
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
        start.Environment.Remove("FORCE_COLOR"); start.Environment["NO_COLOR"] = "1"; // the port line must be plain digits
        using var server = Process.Start(start)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var port = int.Parse((await server.StandardOutput.ReadLineAsync(deadline.Token))!);
            await using var bridge = await CorticoProcess.StartAsync(new CorticoOptions { Enabled = true, SidecarPath = sidecar },
                temp, new VtsConfig { Host = "127.0.0.1", Port = port }, _ => { }, deadline.Token);
            Assert.Contains("点头", bridge.ScriptGrammar);
            Assert.True(bridge.IsAlive);

            var app = new ToneApp();
            await using (var stage = await bridge.BeginAsync(app, deadline.Token))
            {
                app.Stage = stage;
                await stage.FeedAsync("<微笑>你好【点头】再见。", deadline.Token);
                await stage.CompleteAsync(deadline.Token);
            }
            Assert.Equal(["你好", "再见。"], app.Played);
            Assert.True(bridge.MaxHoldMs > 1000);
            lock (app.Log)
                foreach (var played in app.Log.Where(l => l.StartsWith("play:")))
                    Assert.True(app.Log.IndexOf("synth:" + played[5..]) < app.Log.IndexOf(played));

            var held = new ToneApp { HoldSynth = true };
            var stage2 = await bridge.BeginAsync(held, deadline.Token);
            held.Stage = stage2;
            await stage2.FeedAsync("这次取消", deadline.Token);
            await held.SynthHeld.Task.WaitAsync(deadline.Token);
            // Unfinished: the app gives up the turn and the host is interrupted (acknowledged) before
            // the next turn may start. Host requests for the abandoned turn are no longer delivered.
            await stage2.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);

            var again = new ToneApp();
            await using (var stage3 = await bridge.BeginAsync(again, deadline.Token))
            {
                again.Stage = stage3;
                await stage3.FeedAsync("再试一次", deadline.Token);
                await stage3.CompleteAsync(deadline.Token);
            }
            Assert.Equal(["再试一次"], again.Played);
        }
        finally
        {
            server.StandardInput.Close();
            try { await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { server.Kill(true); }
            Directory.Delete(temp, true);
        }
    }
}

internal static class TestWait
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) Assert.Fail("condition not met before timeout");
            await Task.Delay(20);
        }
    }
}
