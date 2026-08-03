using AIVTuber.Core;

namespace AIVTuber.Tests;

public class AppPathsTests
{
    [Fact]
    public void ResolveAvatarAssetsDirectory_PrefersFolderWithSprites()
    {
        var root = Path.Combine(Path.GetTempPath(), "apppaths-" + Guid.NewGuid().ToString("N"));
        var incomplete = Path.Combine(root, "incomplete");
        var complete = Path.Combine(root, "complete");
        Directory.CreateDirectory(incomplete);
        Directory.CreateDirectory(Path.Combine(complete, "sprites"));
        File.WriteAllText(Path.Combine(incomplete, "avatar.json"), "{\"meta\":{\"name\":\"x\"}}");
        File.WriteAllText(Path.Combine(complete, "avatar.json"), "{\"meta\":{\"name\":\"y\"}}");
        File.WriteAllBytes(Path.Combine(complete, "sprites", "gen_00.png"), [0x89, 0x50, 0x4E, 0x47]);

        try
        {
            // Absolute configured path that points at the complete pack.
            var resolved = AppPaths.ResolveAvatarAssetsDirectory(complete);
            Assert.Equal(Path.GetFullPath(complete), resolved);

            // Incomplete absolute path still returns that folder (caller may fallback).
            var incompleteResolved = AppPaths.ResolveAvatarAssetsDirectory(incomplete);
            Assert.Equal(Path.GetFullPath(incomplete), incompleteResolved);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ContentRoot_IsNonEmptyExistingDirectory()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppPaths.ContentRoot));
        Assert.True(Directory.Exists(AppPaths.ContentRoot));
    }
}
