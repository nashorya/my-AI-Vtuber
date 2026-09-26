using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace AIVTuber.AuthServer;

/// <summary>
/// Account validity checks. The service never sees provider keys, audio or text; it only
/// answers "may this installation keep using the cloud for the next few minutes".
/// </summary>
public sealed class AuthService(AuthStore store, TimeProvider clock, AuthServerOptions options)
{
    private readonly PasswordHasher<string> _hasher = new();
    private readonly ConcurrentDictionary<string, FailureWindow> _failures = new(StringComparer.OrdinalIgnoreCase);

    private sealed record FailureWindow(int Count, DateTimeOffset Since);

    public string CreateAccount(string username, string password, string profileId, DateTimeOffset validUntil, string note = "")
    {
        RequireText(username, nameof(username));
        RequireText(password, nameof(password));
        RequireText(profileId, nameof(profileId));
        var id = "acc_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        store.InsertAccount(new AccountRow(id, username.Trim(), _hasher.HashPassword(username, password),
            Enabled: true, validUntil, profileId.Trim(), MinCredentialRevision: 0, note), clock.GetUtcNow());
        return id;
    }

    public void SetPassword(string username, string password)
    {
        RequireText(password, nameof(password));
        var account = RequireAccount(username);
        store.UpdateAccount(account.Username, "password_hash", _hasher.HashPassword(account.Username, password));
        store.RevokeAccountSessions(account.Id, clock.GetUtcNow());
    }

    public void SetEnabled(string username, bool enabled)
    {
        var account = RequireAccount(username);
        store.UpdateAccount(account.Username, "enabled", enabled ? 1 : 0);
        if (!enabled) store.RevokeAccountSessions(account.Id, clock.GetUtcNow());
    }

    public void SetValidUntil(string username, DateTimeOffset validUntil)
        => store.UpdateAccount(RequireAccount(username).Username, "valid_until", validUntil);

    public void SetMinCredentialRevision(string username, int revision)
        => store.UpdateAccount(RequireAccount(username).Username, "min_credential_revision", revision);

    public AuthResult Login(LoginRequest request, string remoteKey)
    {
        var now = clock.GetUtcNow();
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password) ||
            string.IsNullOrWhiteSpace(request.ProfileId))
            return AuthResult.Denied(AuthStatus.BadRequest, now);

        var username = request.Username.Trim();
        if (IsRateLimited(username, now)) return AuthResult.Denied(AuthStatus.RateLimited, now);

        var account = store.FindAccountByUsername(username);
        var verdict = account is null
            ? PasswordVerificationResult.Failed
            : _hasher.VerifyHashedPassword(account.Username, account.PasswordHash, request.Password);
        if (account is null || verdict == PasswordVerificationResult.Failed)
        {
            NoteFailure(username, now);
            return AuthResult.Denied(AuthStatus.InvalidCredentials, now);
        }

        _failures.TryRemove(username, out _);
        if (verdict == PasswordVerificationResult.SuccessRehashNeeded)
            store.UpdateAccount(account.Username, "password_hash", _hasher.HashPassword(account.Username, request.Password));

        var denied = Check(account, request.ProfileId, now);
        if (denied is not null) return AuthResult.Denied(denied.Value, now);
        if (request.CredentialRevision < account.MinCredentialRevision)
            return AuthResult.Denied(AuthStatus.CredentialRevoked, now);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        store.InsertSession(HashToken(token), account.Id, account.ProfileId, request.AppVersion ?? "", now);
        return Granted(account, now) with { SessionToken = token };
    }

    public AuthResult Heartbeat(string token, string profileId)
    {
        var now = clock.GetUtcNow();
        if (string.IsNullOrEmpty(token)) return AuthResult.Denied(AuthStatus.InvalidSession, now);
        var tokenHash = HashToken(token);
        var session = store.FindSession(tokenHash);
        if (session is null) return AuthResult.Denied(AuthStatus.InvalidSession, now);
        var account = store.FindAccountById(session.AccountId);
        if (account is null) return AuthResult.Denied(AuthStatus.InvalidSession, now);
        if (session.RevokedAt is not null)
        {
            // Disabling revokes sessions too; report the account state so the client can say why.
            if (!account.Enabled) return AuthResult.Denied(AuthStatus.Disabled, now);
            if (account.ValidUntil <= now) return AuthResult.Denied(AuthStatus.Expired, now);
            return AuthResult.Denied(AuthStatus.SessionRevoked, now);
        }
        if (!string.Equals(session.ProfileId, profileId, StringComparison.Ordinal))
            return AuthResult.Denied(AuthStatus.ProfileMismatch, now);

        var denied = Check(account, profileId, now);
        if (denied is not null) return AuthResult.Denied(denied.Value, now);
        store.TouchSession(tokenHash, now);
        return Granted(account, now);
    }

    public void Logout(string token)
    {
        if (!string.IsNullOrEmpty(token)) store.RevokeSession(HashToken(token), clock.GetUtcNow());
    }

    private AuthStatus? Check(AccountRow account, string profileId, DateTimeOffset now)
    {
        if (!account.Enabled) return AuthStatus.Disabled;
        if (account.ValidUntil <= now) return AuthStatus.Expired;
        if (!string.Equals(account.ProfileId, profileId?.Trim(), StringComparison.Ordinal))
            return AuthStatus.ProfileMismatch;
        return null;
    }

    private AuthResult Granted(AccountRow account, DateTimeOffset now)
    {
        var lease = now.AddSeconds(options.LeaseSeconds);
        if (lease > account.ValidUntil) lease = account.ValidUntil;
        return new AuthResult(AuthStatus.Ok, AccountId: account.Id, ServerTime: now,
            LeaseValidUntil: lease, AccountValidUntil: account.ValidUntil,
            HeartbeatSeconds: options.HeartbeatSeconds);
    }

    private bool IsRateLimited(string username, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(username, out var window)) return false;
        if (now - window.Since > TimeSpan.FromMinutes(options.FailedLoginWindowMinutes))
        {
            _failures.TryRemove(username, out _);
            return false;
        }
        return window.Count >= options.MaxFailedLogins;
    }

    private void NoteFailure(string username, DateTimeOffset now) =>
        _failures.AddOrUpdate(username, _ => new FailureWindow(1, now),
            (_, w) => now - w.Since > TimeSpan.FromMinutes(options.FailedLoginWindowMinutes)
                ? new FailureWindow(1, now)
                : w with { Count = w.Count + 1 });

    private AccountRow RequireAccount(string username) =>
        store.FindAccountByUsername(username?.Trim() ?? "")
        ?? throw new InvalidOperationException($"账号 {username} 不存在。");

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} 不能为空。", name);
    }

    internal static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
