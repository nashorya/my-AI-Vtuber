using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIVTuber.Core.Auth;

namespace AIVTuber.Packager;

/// <summary>
/// Builds private streamer packages from one public build plus a profile kept outside the
/// repository (DIST-01/03/04/06). Runs on the operator's machine only; it never uploads, and
/// it never prints key material.
/// <code>
/// stream      --profile P --app-dir PUBLISH --out DIR [--app-version V] [--source-commit SHA]
/// credentials --profile P --out DIR [--app-version V] [--source-commit SHA]
/// </code>
/// </summary>
public static class PackagerCli
{
    private static readonly string[] PersonalFiles =
    [
        "config.json",
        Path.Combine(DistributionProfile.DirectoryName, DistributionProfile.FileName),
        CredentialRevisionGuard.StateFileName,
        "memory.db",
    ];

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".txt", ".md", ".config", ".xml", ".ini", ".yaml", ".yml", ".template", ".env", ".py", ".ps1" };

    // Common vendor key shapes; a hit in a public build means someone left a real key behind.
    private static readonly Regex KeyLike = new(@"\bsk-[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            var (command, opts) = Parse(args);
            var profilePath = Path.GetFullPath(Required(opts, "profile"));
            var outDir = Path.GetFullPath(Required(opts, "out"));
            RefuseInsideRepository(profilePath, "专属档案");
            RefuseInsideRepository(outDir, "输出目录");
            var profile = DistributionProfile.LoadFile(profilePath);

            switch (command)
            {
                case "stream":
                {
                    var appDir = Path.GetFullPath(Required(opts, "app-dir"));
                    if (!Directory.Exists(appDir)) throw new PackagerException($"找不到公开程序目录 {appDir}。");
                    CheckPublicBuild(appDir, profile);
                    var version = opts.GetValueOrDefault("app-version") ?? ReadVersion(appDir);
                    var zipName = $"AIVTuber-v{version}-{profile.ProfileId}-c{profile.CredentialRevision}.zip";
                    var manifest = Manifest(profile, version, opts.GetValueOrDefault("source-commit"), HashFiles(appDir));
                    WriteZip(Path.Combine(outDir, zipName), profilePath, manifest, appDir);
                    Report(stdout, outDir, zipName, profile);
                    return 0;
                }
                case "credentials":
                {
                    var version = opts.GetValueOrDefault("app-version") ?? "";
                    var zipName = $"AIVTuber-credentials-{profile.ProfileId}-c{profile.CredentialRevision}.zip";
                    var manifest = Manifest(profile, version, opts.GetValueOrDefault("source-commit"), new Dictionary<string, string>());
                    WriteZip(Path.Combine(outDir, zipName), profilePath, manifest, appDir: null);
                    Report(stdout, outDir, zipName, profile);
                    return 0;
                }
                default:
                    stderr.WriteLine("用法: AIVTuber.Packager (stream|credentials) --profile <档案> --out <目录> [--app-dir <公开程序目录>]");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is PackagerException or DistributionProfileException or ArgumentException or IOException)
        {
            stderr.WriteLine($"错误: {ex.Message}");
            return 1;
        }
    }

    private static void CheckPublicBuild(string appDir, DistributionProfile profile)
    {
        foreach (var relative in PersonalFiles)
            if (File.Exists(Path.Combine(appDir, relative)))
                throw new PackagerException($"公开程序目录里不应有 {relative}（个人配置/记忆/凭据只能来自专属档案）。");
        if (Directory.Exists(Path.Combine(appDir, DistributionProfile.DirectoryName)))
            throw new PackagerException($"公开程序目录里不应有 {DistributionProfile.DirectoryName}{Path.DirectorySeparatorChar} 目录。");

        var secrets = new[] { profile.Providers.Llm.ApiKey, profile.Providers.Asr.ApiKey, profile.Providers.Tts.ApiKey }
            .Where(k => k.Length >= 8).ToArray();
        foreach (var file in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories))
        {
            if (!TextExtensions.Contains(Path.GetExtension(file))) continue;
            var text = File.ReadAllText(file);
            if (secrets.Any(text.Contains) || KeyLike.IsMatch(text))
                throw new PackagerException($"公开程序目录疑似夹带 Key：{Path.GetRelativePath(appDir, file)}。请用干净的公开构建重新打包。");
        }
    }

    private static Dictionary<string, object?> Manifest(
        DistributionProfile profile, string version, string? sourceCommit, IReadOnlyDictionary<string, string> files) => new()
    {
        ["format"] = 1,
        ["app_version"] = version,
        ["source_commit"] = sourceCommit ?? "",
        ["profile_id"] = profile.ProfileId,
        ["account"] = profile.Account,
        ["credential_revision"] = profile.CredentialRevision,
        ["credential_scope"] = profile.CredentialScope,
        ["build_time"] = DateTimeOffset.UtcNow.ToString("O"),
        ["providers"] = new Dictionary<string, string>
        {
            ["llm"] = $"{profile.Providers.Llm.Provider} {DistributionProfile.KeyHint(profile.Providers.Llm.ApiKey)}",
            ["asr"] = $"{profile.Providers.Asr.Provider} {DistributionProfile.KeyHint(profile.Providers.Asr.ApiKey)}",
            ["tts"] = $"{profile.Providers.Tts.Provider} {DistributionProfile.KeyHint(profile.Providers.Tts.ApiKey)}",
        },
        ["files"] = files,
    };

    private static Dictionary<string, string> HashFiles(string appDir)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(appDir, "*", SearchOption.TopDirectoryOnly))
        {
            var ext = Path.GetExtension(file);
            if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            hashes[Path.GetFileName(file)] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        }
        return new Dictionary<string, string>(hashes);
    }

    private static void WriteZip(string zipPath, string profilePath, object manifest, string? appDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        var temp = zipPath + ".partial";
        File.Delete(temp);
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            if (appDir is not null)
                foreach (var file in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories))
                    zip.CreateEntryFromFile(file, Path.GetRelativePath(appDir, file).Replace('\\', '/'));
            zip.CreateEntryFromFile(profilePath, $"{DistributionProfile.DirectoryName}/{DistributionProfile.FileName}");
            var entry = zip.CreateEntry("distribution-manifest.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        File.Move(temp, zipPath, overwrite: true);
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zipPath)));
        File.WriteAllText(zipPath + ".sha256", $"{hash}  {Path.GetFileName(zipPath)}\n");
    }

    private static void Report(TextWriter stdout, string outDir, string zipName, DistributionProfile profile)
    {
        stdout.WriteLine($"已生成 {Path.Combine(outDir, zipName)}");
        stdout.WriteLine($"  {profile.Describe()}");
        stdout.WriteLine("  私下交付给对应主播；不要上传到 GitHub、Release、插件市场或 CI。");
    }

    private static string ReadVersion(string appDir)
    {
        var core = Path.Combine(appDir, "AIVTuber.Core.dll");
        try
        {
            var name = System.Reflection.AssemblyName.GetAssemblyName(core);
            return name.Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : throw new PackagerException("无法读取程序版本。");
        }
        catch (Exception ex) when (ex is not PackagerException)
        {
            throw new PackagerException($"无法从 AIVTuber.Core.dll 读取版本，请传 --app-version。（{ex.GetType().Name}）");
        }
    }

    /// <summary>Private profiles and outputs must never sit inside a git checkout of this repo.</summary>
    private static void RefuseInsideRepository(string path, string what)
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(path) ?? path); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                if (File.Exists(Path.Combine(dir.FullName, "AIVTuber.slnx")))
                    throw new PackagerException($"{what}位于代码仓库 {dir.FullName} 内。私有档案和生成的包必须放在仓库外。");
                return;
            }
        }
    }

    private static (string, Dictionary<string, string>) Parse(string[] args)
    {
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var command = "";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"缺少 {args[i]} 的值。");
                opts[args[i][2..]] = args[++i];
            }
            else if (command.Length == 0) command = args[i];
            else throw new ArgumentException($"多余的参数: {args[i]}");
        }
        return (command, opts);
    }

    private static string Required(Dictionary<string, string> opts, string name) =>
        opts.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new ArgumentException($"缺少 --{name}。");
}

public sealed class PackagerException(string message) : Exception(message);
