namespace AIVTuber.Core;

/// <summary>
/// Resolves the on-disk content root (config.json, assets/, sidecars).
/// <see cref="AppContext.BaseDirectory"/> is unreliable under PublishSingleFile — it often
/// points at a temp extract folder while config/assets sit next to the .exe.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Directory that contains (or should contain) <c>config.json</c> and <c>assets/</c>.
    /// Prefers the executable's directory when it looks like a content root.
    /// </summary>
    public static string ContentRoot => _contentRoot.Value;

    private static readonly Lazy<string> _contentRoot = new(ResolveContentRoot);

    private static string ResolveContentRoot()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            if (LooksLikeContentRoot(candidate))
                return Path.GetFullPath(candidate);
        }

        // Last resort: exe dir (even if empty) so writes land next to the binary, not in %TEMP%.
        var exeDir = GetExecutableDirectory();
        if (!string.IsNullOrEmpty(exeDir))
            return Path.GetFullPath(exeDir);

        return Path.GetFullPath(AppContext.BaseDirectory);
    }

    /// <summary>
    /// Pick the best avatar assets directory: must contain avatar.json; prefer ones that also
    /// have sprites/ or layered/ so we do not silently fall back to dev_placeholder.
    /// </summary>
    public static string ResolveAvatarAssetsDirectory(string configuredAssetsPath)
    {
        string? bestIncomplete = null;

        foreach (var dir in EnumerateDirectoryCandidates(configuredAssetsPath, "assets/avatar"))
        {
            if (!Directory.Exists(dir)) continue;
            if (!File.Exists(Path.Combine(dir, "avatar.json"))) continue;

            var spritesDir = Path.Combine(dir, "sprites");
            var hasSprites = Directory.Exists(spritesDir)
                && Directory.EnumerateFiles(spritesDir, "*.png").Any();
            var hasLayered = File.Exists(Path.Combine(dir, "layered", "body.png"));

            if (hasSprites || hasLayered)
                return Path.GetFullPath(dir);

            bestIncomplete ??= Path.GetFullPath(dir);
        }

        if (bestIncomplete is not null)
            return bestIncomplete;

        // Fall back to content-root join even if missing — callers detect and report.
        var primary = Path.IsPathRooted(configuredAssetsPath)
            ? configuredAssetsPath
            : Path.Combine(ContentRoot, configuredAssetsPath);
        return Path.GetFullPath(primary);
    }

    private static IEnumerable<string> EnumerateDirectoryCandidates(
        string relativeOrAbsolute, params string[] fallbackRelative)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path);
                if (seen.Add(full))
                    list.Add(full);
            }
            catch
            {
                // Invalid path — skip.
            }
        }

        if (Path.IsPathRooted(relativeOrAbsolute))
        {
            Add(relativeOrAbsolute);
        }
        else
        {
            foreach (var root in EnumerateCandidates())
                Add(Path.Combine(root, relativeOrAbsolute));
        }

        foreach (var fb in fallbackRelative)
        {
            if (string.IsNullOrWhiteSpace(fb)) continue;
            foreach (var root in EnumerateCandidates())
                Add(Path.Combine(root, fb));
        }

        return list;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        var exeDir = GetExecutableDirectory();
        if (!string.IsNullOrEmpty(exeDir))
            yield return exeDir;

        yield return AppContext.BaseDirectory;

        var cwd = Environment.CurrentDirectory;
        if (!string.IsNullOrEmpty(cwd))
            yield return cwd;
    }

    private static bool LooksLikeContentRoot(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return false;

        if (File.Exists(Path.Combine(dir, "config.json")))
            return true;
        if (File.Exists(Path.Combine(dir, "config.json.template")))
            return true;
        if (File.Exists(Path.Combine(dir, "assets", "avatar", "avatar.json")))
            return true;

        return false;
    }

    private static string? GetExecutableDirectory()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
                return Path.GetDirectoryName(processPath);
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
