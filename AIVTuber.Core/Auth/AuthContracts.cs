namespace AIVTuber.Core.Auth;

/// <summary>Status codes returned by the account service (<c>status</c> field, snake_case on the wire).</summary>
public enum AuthCode
{
    Ok,
    InvalidCredentials,
    Disabled,
    Expired,
    ProfileMismatch,
    CredentialRevoked,
    SessionRevoked,
    InvalidSession,
    RateLimited,
    BadRequest,
    Unknown,
}

public sealed record AuthLoginRequest(
    string Username,
    string Password,
    string ProfileId,
    string AppVersion,
    int CredentialRevision);

/// <summary>Today's companion-time allowance as reported by the account service.</summary>
public sealed record QuotaReply(int QuotaSeconds, int RemainingSeconds, DateTimeOffset ResetsAt);

/// <summary>Login/heartbeat answer. Times are server UTC; the client only uses their
/// difference, measured on its own monotonic clock.</summary>
public sealed record AuthReply(
    AuthCode Status,
    string? SessionToken = null,
    string? AccountId = null,
    DateTimeOffset? ServerTime = null,
    DateTimeOffset? LeaseValidUntil = null,
    DateTimeOffset? AccountValidUntil = null,
    int HeartbeatSeconds = 0,
    QuotaReply? Quota = null);

/// <summary>The account service could not be reached or answered unintelligibly. Distinct
/// from an explicit denial: only this case may keep using an unexpired lease.</summary>
public sealed class AuthTransportException(string message, Exception? inner = null) : Exception(message, inner);

public interface IAuthApi
{
    Task<AuthReply> LoginAsync(AuthLoginRequest request, CancellationToken ct = default);
    Task<AuthReply> HeartbeatAsync(string token, string profileId, long activeSeconds, CancellationToken ct = default);
    Task LogoutAsync(string token, CancellationToken ct = default);
}

/// <summary>
/// What the runtime asks before starting cloud work. Reads are in-memory only (AUTH-06):
/// no call site waits on the account service.
/// </summary>
public interface ICloudAccess
{
    bool IsAllowed { get; }
    /// <summary>Changes on every grant and every revocation. Work captured under one epoch
    /// must not publish output under another.</summary>
    long Epoch { get; }
    /// <summary>Raised once per revocation with a user-facing reason.</summary>
    event Action<string>? Revoked;
}

/// <summary>Daily companion-time quota as the runtime sees it. Reads are in-memory.</summary>
public interface ICompanionQuota
{
    /// <summary>False when the service does not enforce a quota (the quota members then carry no meaning).</summary>
    bool QuotaManaged { get; }
    int QuotaSeconds { get; }
    /// <summary>Server's remaining seconds minus what this client has used since the last report;
    /// <see cref="int.MaxValue"/> when not managed.</summary>
    int QuotaRemainingSeconds { get; }
    DateTimeOffset? QuotaResetsAt { get; }
    /// <summary>Tells the license whether the companion is running (not paused); only that time is billed.</summary>
    void SetCompanionActive(bool active);
    event Action? QuotaChanged;
    /// <summary>Remaining time reached zero.</summary>
    event Action? QuotaExhausted;
    /// <summary>Remaining time became positive again after having been exhausted.</summary>
    event Action? QuotaRestored;
}

/// <summary>Public (non-distribution) builds: the user brings their own keys, no account.</summary>
public sealed class UnrestrictedCloudAccess : ICloudAccess
{
    public static readonly UnrestrictedCloudAccess Instance = new();
    private UnrestrictedCloudAccess() { }
    public bool IsAllowed => true;
    public long Epoch => 0;
    public event Action<string>? Revoked { add { } remove { } }
}
