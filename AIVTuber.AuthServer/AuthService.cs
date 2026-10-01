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

    public AuthResult Heartbeat(string token, string profileId, long activeSeconds = 0)
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

        var delta = activeSeconds - session.ReportedActiveSeconds;
        if (delta > 0)
        {
            // What one report may credit is bounded by real elapsed time (+ a little slack for
            // timer jitter), so a buggy or tampered counter cannot burn or invent hours.
            var elapsed = (long)Math.Max(0, (now - session.LastSeenAt).TotalSeconds);
            var credited = (int)Math.Min(delta, elapsed + options.MaxCatchUpSeconds);
            store.RecordReport(tokenHash, account.Id, QuotaCalendar.DayOf(now), activeSeconds, credited, now);
        }
        else
        {
            store.TouchSession(tokenHash, now);
        }
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
        var (quota, remaining, resetsAt) = QuotaFor(account, now);
        return new AuthResult(AuthStatus.Ok, AccountId: account.Id, ServerTime: now,
            LeaseValidUntil: lease, AccountValidUntil: account.ValidUntil,
            HeartbeatSeconds: options.HeartbeatSeconds,
            QuotaSeconds: quota, QuotaRemainingSeconds: remaining, QuotaResetsAt: resetsAt);
    }

    public int DefaultDailyQuotaSeconds =>
        int.TryParse(store.GetSetting("daily_quota_seconds"), out var seconds) && seconds > 0
            ? seconds
            : options.DefaultDailyQuotaSeconds;

    public (int Quota, int Remaining, DateTimeOffset ResetsAt) QuotaFor(AccountRow account, DateTimeOffset now)
    {
        var day = QuotaCalendar.DayOf(now);
        var quota = (account.DailyQuotaSeconds ?? DefaultDailyQuotaSeconds)
            + (account.BonusDay == day ? account.BonusSeconds : 0);
        var used = store.GetUsedSeconds(account.Id, day);
        return (quota, Math.Max(0, quota - used), QuotaCalendar.ResetsAfter(now));
    }

    public void SetDefaultDailyQuota(int seconds)
    {
        if (seconds <= 0) throw new ArgumentException("每日时长必须大于 0。");
        store.SetSetting("daily_quota_seconds", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void SetDailyQuota(string username, int? seconds)
    {
        if (seconds is <= 0) throw new ArgumentException("每日时长必须大于 0。");
        store.UpdateAccount(RequireAccount(username).Username, "daily_quota_seconds", seconds!);
    }

    public void AddTodayBonus(string username, int seconds)
    {
        if (seconds <= 0) throw new ArgumentException("加时必须大于 0。");
        var account = RequireAccount(username);
        var today = QuotaCalendar.DayOf(clock.GetUtcNow());
        var total = (account.BonusDay == today ? account.BonusSeconds : 0) + seconds;
        store.UpdateAccount(account.Username, "bonus_seconds", total);
        store.UpdateAccount(account.Username, "bonus_day", today);
    }

    public IReadOnlyList<(string Day, int UsedSeconds)> Usage(string username, int days) =>
        store.ListUsage(RequireAccount(username).Id, days);

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
