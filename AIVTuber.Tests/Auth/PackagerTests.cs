using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AIVTuber.Packager;

namespace AIVTuber.Tests.Auth;

/// <summary>DIST-01/03/04/06: private packages from one public build plus an out-of-repo profile.</summary>
public sealed class PackagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pack-{Guid.NewGuid():N}");
    private readonly string _appDir;
    private readonly string _privateDir;
    private readonly string _outDir;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public PackagerTests()
    {
        _appDir = Path.Combine(_root, "publish");
        _privateDir = Path.Combine(_root, "private");
        _outDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(_appDir);
        Directory.CreateDirectory(_privateDir);
        File.WriteAllText(Path.Combine(_appDir, "AIVTuber.exe"), "fake exe");
        File.WriteAllText(Path.Combine(_appDir, "AIVTuber.Core.dll"), "fake core");
        File.WriteAllText(Path.Combine(_appDir, "config.json.template"), """{"llm":{"api_key":"<your-llm-api-key>"}}""");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteProfile(string name, string json)
    {
        var path = Path.Combine(_privateDir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private int Pack(params string[] extra) => PackagerCli.Run(
        ["stream", "--app-dir", _appDir, "--out", _outDir, "--app-version", "0.36.0", "--source-commit", "abc1234", .. extra],
        _stdout, _stderr);

    [Fact]
    public void Stream_ProducesZipManifestAndChecksum_WithoutPrintingKeys()
    {
        var profile = WriteProfile("s017.json", DistributionProfileTests.ProfileJson(llmKey: "sk-test-llm-aaaa1111"));

        Assert.Equal(0, Pack("--profile", profile));

        var zipPath = Path.Combine(_outDir, "AIVTuber-v0.36.0-streamer-017-c2.zip");
        Assert.True(File.Exists(zipPath), _stderr.ToString());
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zipPath)));
        Assert.StartsWith(expectedHash, File.ReadAllText(zipPath + ".sha256"));

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.NotNull(zip.GetEntry("AIVTuber.exe"));
        Assert.NotNull(zip.GetEntry("distribution/profile.json"));
        using var manifestStream = zip.GetEntry("distribution-manifest.json")!.Open();
        var manifestText = new StreamReader(manifestStream).ReadToEnd();
        using var manifest = JsonDocument.Parse(manifestText);
        Assert.Equal("0.36.0", manifest.RootElement.GetProperty("app_version").GetString());
        Assert.Equal("abc1234", manifest.RootElement.GetProperty("source_commit").GetString());
        Assert.Equal("streamer-017", manifest.RootElement.GetProperty("profile_id").GetString());
        Assert.Equal(2, manifest.RootElement.GetProperty("credential_revision").GetInt32());
        Assert.True(manifest.RootElement.GetProperty("files").TryGetProperty("AIVTuber.exe", out _));
        Assert.DoesNotContain("aaaa1111", manifestText.Replace("…1111", ""));
        Assert.DoesNotContain("sk-test", manifestText);

        Assert.DoesNotContain("sk-test", _stdout.ToString() + _stderr.ToString());
    }

    [Fact]
    public void TwoStreamers_FromSameBuild_HaveSameCoreHashesAndOwnKeys()
    {
        var a = WriteProfile("a.json", DistributionProfileTests.ProfileJson(profileId: "streamer-017", llmKey: "sk-test-A-00000001"));
        var b = WriteProfile("b.json", DistributionProfileTests.ProfileJson(profileId: "streamer-018", llmKey: "sk-test-B-00000002"));

        Assert.Equal(0, Pack("--profile", a));
        Assert.Equal(0, Pack("--profile", b));

        string ReadEntry(string zip, string entry)
        {
            using var archive = ZipFile.OpenRead(Path.Combine(_outDir, zip));
            using var stream = archive.GetEntry(entry)!.Open();
            return new StreamReader(stream).ReadToEnd();
        }
        var zipA = "AIVTuber-v0.36.0-streamer-017-c2.zip";
        var zipB = "AIVTuber-v0.36.0-streamer-018-c2.zip";
        Assert.Contains("sk-test-A-00000001", ReadEntry(zipA, "distribution/profile.json"));
        Assert.DoesNotContain("sk-test-B", ReadEntry(zipA, "distribution/profile.json"));
        Assert.Contains("sk-test-B-00000002", ReadEntry(zipB, "distribution/profile.json"));
        Assert.Equal(ReadEntry(zipA, "AIVTuber.exe"), ReadEntry(zipB, "AIVTuber.exe"));
    }

    [Theory]
    [InlineData("config.json")]
    [InlineData("distribution/profile.json")]
    [InlineData("distribution-state.json")]
    [InlineData("memory.db")]
    public void PublicBuildCarryingPersonalFiles_IsRefused(string relative)
    {
        var target = Path.Combine(_appDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "{}");
        var profile = WriteProfile("s017.json", DistributionProfileTests.ProfileJson());

        Assert.NotEqual(0, Pack("--profile", profile));
        Assert.Contains(relative.Replace('/', Path.DirectorySeparatorChar), _stderr.ToString());
        Assert.False(Directory.Exists(_outDir) && Directory.EnumerateFiles(_outDir).Any());
    }

    [Fact]
    public void PublicBuildWithEmbeddedKey_IsRefused()
    {
        File.WriteAllText(Path.Combine(_appDir, "config.json.template"), """{"llm":{"api_key":"sk-test-llm-aaaa1111"}}""");
        var profile = WriteProfile("s017.json", DistributionProfileTests.ProfileJson(llmKey: "sk-test-llm-aaaa1111"));

        Assert.NotEqual(0, Pack("--profile", profile));
        Assert.Contains("疑似夹带", _stderr.ToString());
        Assert.DoesNotContain("aaaa1111", _stderr.ToString());
    }

    [Fact]
    public void ProfileInsideRepository_IsRefused()
    {
        var fakeRepo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(fakeRepo, ".git"));
        File.WriteAllText(Path.Combine(fakeRepo, "AIVTuber.slnx"), "<Solution />");
        var inside = Path.Combine(fakeRepo, "profiles", "s017.json");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllText(inside, DistributionProfileTests.ProfileJson());

        Assert.NotEqual(0, Pack("--profile", inside));
        Assert.Contains("仓库", _stderr.ToString());
    }

    [Fact]
    public void OutputInsideRepository_IsRefused()
    {
        var fakeRepo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(fakeRepo, ".git"));
        File.WriteAllText(Path.Combine(fakeRepo, "AIVTuber.slnx"), "<Solution />");
        var profile = WriteProfile("s017.json", DistributionProfileTests.ProfileJson());

        var code = PackagerCli.Run(
            ["stream", "--profile", profile, "--app-dir", _appDir, "--out", Path.Combine(fakeRepo, "artifacts"),
             "--app-version", "0.36.0", "--source-commit", "abc"], _stdout, _stderr);

        Assert.NotEqual(0, code);
        Assert.Contains("仓库", _stderr.ToString());
    }

    [Fact]
    public void InvalidProfile_IsRefusedWithReason()
    {
        var profile = WriteProfile("bad.json", DistributionProfileTests.ProfileJson(asrProvider: "local"));

        Assert.NotEqual(0, Pack("--profile", profile));
        Assert.Contains("本地 ASR", _stderr.ToString());
    }

    [Fact]
    public void CredentialsOnly_ProducesSmallUpdateWithoutProgramFiles()
    {
        var profile = WriteProfile("s017.json", DistributionProfileTests.ProfileJson(revision: 3));

        var code = PackagerCli.Run(["credentials", "--profile", profile, "--out", _outDir,
            "--app-version", "0.36.0", "--source-commit", "abc"], _stdout, _stderr);

        Assert.Equal(0, code);
        using var zip = ZipFile.OpenRead(Path.Combine(_outDir, "AIVTuber-credentials-streamer-017-c3.zip"));
        Assert.Equal(["distribution-manifest.json", "distribution/profile.json"],
            zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
}
