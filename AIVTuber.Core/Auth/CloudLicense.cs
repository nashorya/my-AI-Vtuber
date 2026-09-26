namespace AIVTuber.Core.Auth;

public enum LicenseState { SignedOut, Active, Denied }

/// <summary>Why cloud access is closed, so the UI can give the right advice (U07).</summary>
public enum LicenseStopReason
{
    None,
    SignedOut,
    /// <summary>The account's validity period ended. Renewal, not the network, is the fix.</summary>
    AccountExpired,
    /// <summary>The lease ran out because renewals kept failing (network / service).</summary>
    VerificationLost,
    /// <summary>The service explicitly refused (disabled, revoked, wrong package, …).</summary>
    Denied,
}

public sealed record LicenseSnapshot(
    LicenseState State,
    string Message,
    string? Username,
    DateTimeOffset? AccountValidUntil,
    LicenseStopReason Reason = LicenseStopReason.None);

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
    // Account end mapped onto the monotonic clock (server's own "time left"), or null if unknown.
    private long? _accountDeadlineTimestamp;
    private LicenseStopReason _reason = LicenseStopReason.SignedOut;
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
        get { lock (_sync) return SnapshotLocked(); }
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
            AIVTuber.Core.Diagnostics.DebugLog.Write(
                $"[鉴权] 登录请求失败: {AIVTuber.Core.Diagnostics.DiagnosticRedactor.Redact(ex.Message)}");
            return Fail(generation, TransportFailureMessage);
        }

        LicenseSnapshot snapshot;
        lock (_sync)
        {
            if (_generation != generation) return new LoginOutcome(false, "登录已取消");
            if (reply.Status != AuthCode.Ok || string.IsNullOrEmpty(reply.SessionToken))
            {
                _state = LicenseState.SignedOut;
                _message = Describe(reply.Status);
                _reason = reply.Status == AuthCode.Expired ? LicenseStopReason.AccountExpired : LicenseStopReason.Denied;
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
                _reason = LicenseStopReason.None;
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
        Revoke(Describe(reply.Status), LicenseState.Denied,
            reply.Status == AuthCode.Expired ? LicenseStopReason.AccountExpired : LicenseStopReason.Denied);
    }

    public const string AccountEndedMessage = "使用期限已结束，请联系发放者续期。";
    public const string VerificationLostMessage = "暂时无法验证登录状态，陪播已暂停。请检查网络连接后重新登录。";
    public const string TransportFailureMessage = "暂时连不上登录服务，请检查网络后重试。";

    /// <summary>Closes access if the lease has run out without renewal. A lease that ends
    /// because the account itself ended says so, instead of blaming the network (U07).</summary>
    public void EnforceExpiry()
    {
        bool expired;
        bool accountEnded;
        lock (_sync)
        {
            var now = _clock.GetTimestamp();
            expired = _state == LicenseState.Active && now >= _leaseExpiryTimestamp;
            accountEnded = _accountDeadlineTimestamp is { } deadline && now >= deadline;
        }
        if (!expired) return;
        if (accountEnded) Revoke(AccountEndedMessage, LicenseState.Denied, LicenseStopReason.AccountExpired);
        else Revoke(VerificationLostMessage, LicenseState.Denied, LicenseStopReason.VerificationLost);
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
            _reason = LicenseStopReason.SignedOut;
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
        AuthCode.Expired => AccountEndedMessage,
        AuthCode.Disabled => "账号已被停用，请联系发放者",
        AuthCode.ProfileMismatch => "这个账号需要使用对应的安装包，请联系发放者",
        AuthCode.CredentialRevoked => "这个安装包已停止使用，请向发放者索取最新安装包",
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

    private void Revoke(string reason, LicenseState state, LicenseStopReason stopReason)
    {
        LicenseSnapshot snapshot;
        lock (_sync)
        {
            if (_state != LicenseState.Active) return;
            _state = state;
            _message = reason;
            _reason = stopReason;
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
        _accountDeadlineTimestamp = reply.AccountValidUntil is { } accountEnd && reply.ServerTime is { } server
            ? sentAt + (long)(Math.Max(0, (accountEnd - server).TotalSeconds) * _clock.TimestampFrequency)
            : null;
        _accountValidUntil = reply.AccountValidUntil;
        if (reply.HeartbeatSeconds > 0) _heartbeatInterval = TimeSpan.FromSeconds(reply.HeartbeatSeconds);
        _nextHeartbeatTimestamp = sentAt + (long)(_heartbeatInterval.TotalSeconds * _clock.TimestampFrequency);
    }

    private LicenseSnapshot SnapshotLocked() => new(_state, _message, _username, _accountValidUntil, _reason);

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
