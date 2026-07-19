using AIVTuber.Core.Avatar;

namespace AIVTuber.Tests;

public class AvatarConfigLoaderTests
{
    [Fact]
    public void Load_RealPack_FromRepoAssets()
    {
        var dir = FindAvatarAssets();
        Assert.True(Directory.Exists(dir), $"assets not found: {dir}");

        var pack = AvatarConfigLoader.Load(dir);
        Assert.NotEmpty(pack.States);
        Assert.True(pack.States.ContainsKey("neutral"));
        Assert.True(pack.States.ContainsKey("blink"));
        Assert.NotEmpty(pack.MouthSync.Levels);
        Assert.True(pack.Stickers.Items.ContainsKey("sweat_laugh"));
    }

    [Fact]
    public void ResolveAvailableStates_FindsSprites()
    {
        var dir = FindAvatarAssets();
        var pack = AvatarConfigLoader.Load(dir);
        var available = AvatarConfigLoader.ResolveAvailableStates(pack, dir);
        Assert.Contains("neutral", available);
        Assert.True(available.Count >= 8, $"expected most sprites present, got {available.Count}");
    }

    [Fact]
    public void Load_MissingDir_ReturnsPlaceholderWithoutThrow()
    {
        var pack = AvatarConfigLoader.Load(Path.Combine(Path.GetTempPath(), "no-avatar-" + Guid.NewGuid()));
        Assert.Equal("dev_placeholder", pack.Meta.Name);
        Assert.Contains("neutral", pack.States.Keys);
    }

    private static string FindAvatarAssets()
    {
        // Test host cwd is typically bin/Debug/net10.0 — walk up to repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "assets", "avatar");
            if (File.Exists(Path.Combine(candidate, "avatar.json")))
                return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "assets", "avatar");
    }
}
