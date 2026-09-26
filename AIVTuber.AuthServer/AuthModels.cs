using System.Text.Json.Serialization;

namespace AIVTuber.AuthServer;

/// <summary>Outcome codes shared with the client. The wire form is the snake_case name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AuthStatus>))]
public enum AuthStatus
{
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("invalid_credentials")] InvalidCredentials,
    [JsonStringEnumMemberName("disabled")] Disabled,
    [JsonStringEnumMemberName("expired")] Expired,
    [JsonStringEnumMemberName("profile_mismatch")] ProfileMismatch,
    [JsonStringEnumMemberName("credential_revoked")] CredentialRevoked,
    [JsonStringEnumMemberName("session_revoked")] SessionRevoked,
    [JsonStringEnumMemberName("invalid_session")] InvalidSession,
    [JsonStringEnumMemberName("rate_limited")] RateLimited,
    [JsonStringEnumMemberName("bad_request")] BadRequest,
}

public sealed record LoginRequest(
    string Username,
    string Password,
    string ProfileId,
    string AppVersion,
    int CredentialRevision);

public sealed record HeartbeatRequest(string ProfileId);

/// <summary>Response for login and heartbeat. Times are UTC server time; the client
/// anchors them to its own monotonic clock using <see cref="ServerTime"/>.</summary>
public sealed record AuthResult(
    AuthStatus Status,
    string? SessionToken = null,
    string? AccountId = null,
    DateTimeOffset? ServerTime = null,
    DateTimeOffset? LeaseValidUntil = null,
    DateTimeOffset? AccountValidUntil = null,
    int HeartbeatSeconds = 0)
{
    public static AuthResult Denied(AuthStatus status, DateTimeOffset now) => new(status, ServerTime: now);
}

public sealed class AuthServerOptions
{
    public string DatabasePath { get; set; } = "auth.db";
    /// <summary>Maximum lifetime of one lease; never extends past the account's valid_until.</summary>
    public int LeaseSeconds { get; set; } = 180;
    /// <summary>Suggested client heartbeat interval.</summary>
    public int HeartbeatSeconds { get; set; } = 60;
    public int MaxFailedLogins { get; set; } = 5;
    public int FailedLoginWindowMinutes { get; set; } = 15;
}
