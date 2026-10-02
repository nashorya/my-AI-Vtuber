using System.Reflection;

namespace AIVTuber.Core;

/// <summary>Version read from the assembly (Directory.Build.props), so UI, diagnostics and
/// packaging all report the same value. The SDK appends <c>+&lt;commit&gt;</c> when built from git.</summary>
public static class AppVersion
{
    public static string Informational { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    public static string Current => Informational.Split('+')[0];

    public static string? SourceCommit => Informational.Contains('+') ? Informational[(Informational.IndexOf('+') + 1)..] : null;
}
