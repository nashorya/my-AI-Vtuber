using AIVTuber.Core.Avatar;

namespace AIVTuber.Tests;

public class AvatarConfigWatcherTests
{
    [Fact]
    public async Task DebouncesMultipleEvents()
    {
        var dir = Path.Combine(Path.GetTempPath(), "avatar-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "avatar.json");
        await File.WriteAllTextAsync(path, "{\"meta\":{\"name\":\"t0\"}}");

        var count = 0;
        using var watcher = new AvatarConfigWatcher(
            dir,
            () =>
            {
                Interlocked.Increment(ref count);
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(200));

        // Burst of writes — FileSystemWatcher may coalesce, but must not fire once-per-write after debounce.
        for (var i = 0; i < 8; i++)
        {
            await File.WriteAllTextAsync(path, $"{{\"meta\":{{\"name\":\"t{i}\"}}}}");
            await Task.Delay(25);
        }

        await Task.Delay(500);

        Assert.InRange(count, 1, 3);
        Assert.True(count < 8, $"debounce failed: got {count} reloads for 8 writes");
    }
}
