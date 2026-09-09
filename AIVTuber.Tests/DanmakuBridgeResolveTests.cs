using AIVTuber.Core.LiveStream;

namespace AIVTuber.Tests;

public class DanmakuBridgeResolveTests
{
    [Fact]
    public void PrefersGoExeWhenPresent()
    {
        var dir = Directory.CreateTempSubdirectory("aivtuber-bridge-");
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "danmaku_bridge.exe"), [0]);
            File.WriteAllText(Path.Combine(dir.FullName, "danmaku_bridge.py"), "");
            var (file, args) = BilibiliDanmakuClient.ResolveBridgeCommand(dir.FullName, "python");
            Assert.Equal(Path.Combine(dir.FullName, "danmaku_bridge.exe"), file);
            Assert.Equal("", args);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FallsBackToPythonScript()
    {
        var dir = Directory.CreateTempSubdirectory("aivtuber-bridge-");
        try
        {
            var script = Path.Combine(dir.FullName, "danmaku_bridge.py");
            File.WriteAllText(script, "");
            var (file, args) = BilibiliDanmakuClient.ResolveBridgeCommand(dir.FullName, "python");
            Assert.Equal("python", file);
            Assert.Equal($"\"{script}\"", args);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
