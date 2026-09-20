namespace AIVTuber.Core.Cortico;

/// <summary>The sidecar owns audio playback and VTS while this backend is selected.</summary>
public interface ICorticoPerformance
{
    string Prompt { get; }
    Task<string> PrepareAsync(string script, CancellationToken ct);
    Task PerformAsync(string script, Func<bool> authorize, Action started, CancellationToken ct);
}
