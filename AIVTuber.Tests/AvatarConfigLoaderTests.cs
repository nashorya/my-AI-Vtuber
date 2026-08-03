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
        Assert.Equal(0f, pack.MotionLayer.Breath.AmpPx);
        Assert.Equal(0f, pack.MotionLayer.Breath.ScaleAmp);
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

    [Fact]
    public void TryLoad_AcceptsUtf8BomPrefixedJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), "avatar-bom-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var json = """
                {"meta":{"name":"bom-pack","canvas":{"width":1,"height":1},"pivot":{"x":0,"y":0}},
                 "states":{"neutral":{"file":"sprites/n.png","category":"base"}}}
                """;
            var utf8 = System.Text.Encoding.UTF8.GetBytes(json);
            var withBom = new byte[3 + utf8.Length];
            withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
            Buffer.BlockCopy(utf8, 0, withBom, 3, utf8.Length);
            File.WriteAllBytes(Path.Combine(dir, "avatar.json"), withBom);

            Assert.True(AvatarConfigLoader.TryLoad(dir, out var pack));
            Assert.Equal("bom-pack", pack.Meta.Name);
            Assert.Contains("neutral", pack.States.Keys);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
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
