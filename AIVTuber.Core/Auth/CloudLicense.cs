namespace AIVTuber.Core.Auth;

public enum LicenseState { SignedOut, Active, Denied }

public sealed record LicenseSnapshot(
    LicenseState State,
    string Message,
    string? Username,
    DateTimeOffset? AccountValidUntil);

public sealed record LoginOutcome(bool Success, string Message);

/// <summary>
/// Client side of the account check (AUTH-02..06). Holds a short lease granted by the
/// service, renews it in the background and closes cloud access on explicit denial, on
/// logout, or when the lease runs out without a successful renewal.
/// <para>Lease expiry is measured on the monotonic clock from the moment the request was
/// sent, using the server's own lease duration, so changing the PC's date cannot extend it.</para>
/// </summary>
public sealed class CloudLicense : ICloudAccess, IAsyncDisposable
{
    private readonly IAuthApi _api;
    private readonly string _profileId;
    private readonly int _credentialRevision;
    private readonly string _appVersion;
    private readonly TimeProvider _clock;
    private readonly bool _startBackgroundLoop;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _loopCts = new();

    private LicenseState _state = LicenseState.SignedOut;
    private string _message = "未登录";
    private string? _token;
    private string? _username;
    private DateTimeOffset? _accountValidUntil;
    private long _leaseExpiryTimestamp;
    private long _nextHeartbeatTimestamp;
    private TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(60);
    private long _generation;
    private long _epoch;
    private Task? _loop;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    public CloudLicense(IAuthApi api, string profileId, int credentialRevision, string appVersion,
        TimeProvider? clock = null, bool startBackgroundLoop = true)
    {
        _api = api;
        _profileId = profileId;
        _credentialRevision = credentialRevision;
        _appVersion = appVersion;
        _clock = clock ?? TimeProvider.System;
        _startBackgroundLoop = startBackgroundLoop;
    }

    public event Action<string>? Revoked;
    public event Action<LicenseSnapshot>? Changed;

    public bool IsAllowed
    {
        get
        {
            lock (_sync)
                return _state == LicenseState.Active && _clock.GetTimestamp() < _leaseExpiryTimestamp;
        }
    }

    public long Epoch
    {
        get { lock (_sync) return _epoch; }
    }

    public LicenseSnapshot Snapshot
    {
        get { lock (_sync) return new(_state, _message, _username, _accountValidUntil); }
    }

    public async Task<LoginOutcome> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        long generation;
        lock (_sync) generation = ++_generation;
        var sentAt = _clock.GetTimestamp();

        AuthReply reply;
        try
        {
            reply = await _api.LoginAsync(
                new AuthLoginRequest(username, password, _profileId, _appVersion, _credentialRevision), ct)
                .ConfigureAwait(false);
        }
        catch (AuthTransportException ex)
        {
            return Fail(generation, $"无法连接鉴权服务：{ex.Message}");
        }

        LicenseSnapshot snapshot;
        lock (_sync)
        {
            if (_generation != generation) return new LoginOutcome(false, "登录已取消");
            if (reply.Status != AuthCode.Ok || string.IsNullOrEmpty(reply.SessionToken))
            {
                _state = LicenseState.SignedOut;
                _message = Describe(reply.Status);
                snapshot = SnapshotLocked();
            }
            else
            {
                _state = LicenseState.Active;
                _token = reply.SessionToken;
                _username = username;
                _epoch++;
                ApplyLeaseLocked(reply, sentAt);
                _message = "已登录";
                snapshot = SnapshotLocked();
            }
        }

        Changed?.Invoke(snapshot);
        if (snapshot.State != LicenseState.Active) return new LoginOutcome(false, snapshot.Message);
        EnsureLoop();
        return new LoginOutcome(true, snapshot.Message);
    }

    /// <summary>One renewal attempt. Network failures keep the current lease; explicit
    /// denials revoke immediately; answers for a superseded session are ignored.</summary>
    public async Task HeartbeatOnceAsync(CancellationToken ct = default)
    {
        string token;
        long generation;
        lock (_sync)
        {
            if (_state != LicenseState.Active || _token is null) return;
            token = _token;
            generation = _generation;
        }
        var sentAt = _clock.GetTimestamp();

        AuthReply reply;
        try
        {
            reply = await _api.HeartbeatAsync(token, _profileId, ct).ConfigureAwait(false);
        }
        catch (AuthTransportException)
        {
            EnforceExpiry();
            return;
        }

        if (reply.Status == AuthCode.Ok)
        {
            LicenseSnapshot snapshot;
            lock (_sync)
            {
                if (_generation != generation || _state != LicenseState.Active) return;
                ApplyLeaseLocked(reply, sentAt);
                snapshot = SnapshotLocked();
            }
            Changed?.Invoke(snapshot);
            return;
        }

        lock (_sync)
            if (_generation != generation) return;
        Revoke(Describe(reply.Status), LicenseState.Denied);
    }

    /// <summary>Closes access if the lease has run out without renewal.</summary>
    public void EnforceExpiry()
    {
        bool expired;
        lock (_sync)
            expired = _state == LicenseState.Active && _clock.GetTimestamp() >= _leaseExpiryTimestamp;
        if (expired)
            Revoke("长时间无法联系鉴权服务，许可已过期，请检查网络后重新登录", LicenseState.Denied);
    }

    /// <summary>Closes cloud access locally first, then tells the service. The server call
    /// never delays the local stop.</summary>
    public async Task LogoutAsync()
    {
        string? token;
        bool wasActive;
        LicenseSnapshot snapshot;
        lock (_sync)
        {
            token = _token;
            wasActive = _state == LicenseState.Active;
            _generation++;
            if (wasActive) _epoch++;
            _state = LicenseState.SignedOut;
            _token = null;
            _message = "已退出登录";
            snapshot = SnapshotLocked();
        }

        if (wasActive) Revoked?.Invoke(snapshot.Message);
        Changed?.Invoke(snapshot);
        if (token is null) return;
        try { await _api.LogoutAsync(token).ConfigureAwait(false); }
        catch (AuthTransportException) { /* server will expire the session on its own */ }
    }

    public async ValueTask DisposeAsync()
    {
        _loopCts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _loopCts.Dispose();
    }

    internal static string Describe(AuthCode code) => code switch
    {
        AuthCode.InvalidCredentials => "账号或密码错误",
        AuthCode.Expired => "账号已到期，请联系运营续期",
        AuthCode.Disabled => "账号已被停用，请联系运营",
        AuthCode.ProfileMismatch => "这个安装包不属于这个账号（profile 不匹配），请使用发给你的专属包",
        AuthCode.CredentialRevoked => "安装包里的凭据版本已作废，请使用运营发给你的最新专属包",
        AuthCode.SessionRevoked => "登录已被注销，请重新登录",
        AuthCode.InvalidSession => "登录状态已失效，请重新登录",
        AuthCode.RateLimited => "登录失败次数过多，请稍后再试",
        AuthCode.BadRequest => "登录信息不完整",
        _ => "鉴权服务返回了无法识别的结果",
    };

    private LoginOutcome Fail(long generation, string message)
    {
        LicenseSnapshot snapshot;
        lock (_sync)
        {
            if (_generation != generation) return new LoginOutcome(false, "登录已取消");
            _message = message;
            snapshot = SnapshotLocked();
        }
        Changed?.Invoke(snapshot);
        return new LoginOutcome(false, message);
    }

    private void Revoke(string reason, LicenseState state)
    {
        LicenseSnapshot snapshot;
        lock (_sync)
        {
            if (_state != LicenseState.Active) return;
            _state = state;
            _message = reason;
            _token = null;
            _generation++;
            _epoch++;
            snapshot = SnapshotLocked();
        }
        Revoked?.Invoke(reason);
        Changed?.Invoke(snapshot);
    }

    private void ApplyLeaseLocked(AuthReply reply, long sentAt)
    {
        var remaining = reply.LeaseValidUntil is { } until && reply.ServerTime is { } serverNow
            ? until - serverNow
            : TimeSpan.Zero;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        _leaseExpiryTimestamp = sentAt + (long)(remaining.TotalSeconds * _clock.TimestampFrequency);
        _accountValidUntil = reply.AccountValidUntil;
        if (reply.HeartbeatSeconds > 0) _heartbeatInterval = TimeSpan.FromSeconds(reply.HeartbeatSeconds);
        _nextHeartbeatTimestamp = sentAt + (long)(_heartbeatInterval.TotalSeconds * _clock.TimestampFrequency);
    }

    private LicenseSnapshot SnapshotLocked() => new(_state, _message, _username, _accountValidUntil);

    private void EnsureLoop()
    {
        if (!_startBackgroundLoop) return;
        lock (_sync) _loop ??= Task.Run(() => RunLoopAsync(_loopCts.Token));
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _clock, ct).ConfigureAwait(false);
            EnforceExpiry();
            bool due;
            lock (_sync)
            {
                due = _state == LicenseState.Active && _clock.GetTimestamp() >= _nextHeartbeatTimestamp;
                // A failed attempt retries sooner; a success moves this forward in ApplyLeaseLocked.
                if (due) _nextHeartbeatTimestamp = _clock.GetTimestamp() + (long)(RetryDelay.TotalSeconds * _clock.TimestampFrequency);
            }
            if (!due) continue;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await HeartbeatOnceAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Timeouts and unexpected faults are treated like network loss: the loop must
                // survive so the lease still gets enforced when it runs out.
                AIVTuber.Core.Diagnostics.DebugLog.Write($"[鉴权] 续验失败: {ex.GetType().Name}");
                EnforceExpiry();
            }
        }
    }
}
