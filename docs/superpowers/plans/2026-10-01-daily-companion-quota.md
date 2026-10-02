# Daily Companion Quota Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Each streamer gets a daily companion-time quota (default 60 min, reset 06:00 Beijing time) that the account service keeps as a ledger, the client counts down locally, and the streamer console displays.

**Architecture:** The client reports a cumulative "companion was active" seconds counter on each existing 60 s heartbeat; the account service deducts the delta from a per-account, per-quota-day ledger and replies with the remaining seconds. The client counts down locally on its monotonic clock; at zero the runtime blocks new work, lets the current reply finish (max 30 s), then pauses. The quota never revokes the session.

**Tech Stack:** C# / .NET 10, ASP.NET Core Minimal API + SQLite (`Microsoft.Data.Sqlite`), xUnit, WPF host with a WebView2 page (`App/WebUi/wwwroot/streamer.*`).

**Spec:** `docs/superpowers/specs/2026-10-01-daily-companion-quota-design.md`

## Global Constraints

- Quota day: boundary at 06:00 UTC+8; `quota_day = (utc + 8h − 6h).Date`; next reset = day+1 at 06:00 UTC+8.
- Default quota 3600 s; per-account override; per-day bonus valid only for its `bonus_day`.
- Only companion-**active** time (not paused) is counted. Time is counted on the client's monotonic clock, never the system date.
- Wire format is snake_case JSON. New heartbeat field `active_seconds` (cumulative per session, long). New reply fields (on `status: "ok"`): `quota_seconds`, `quota_remaining_seconds`, `quota_resets_at`.
- Quota exhaustion is **not** a denial: `status` stays `ok`, the session stays valid, the lease is unaffected.
- Delta credited per heartbeat ≤ (seconds since the session's last seen) + 30. A cumulative value that goes down credits nothing.
- A missing/invalid quota field on an `ok` reply is a transport failure on the client (`AuthTransportException`), never an account denial.
- Wind-down: block new turns immediately; wait until `PipelineState` is not `Thinking`/`Speaking`; cap 30 s; then `SetCompanionPaused(true)` and mark exhausted. After the quota is restored the companion does **not** resume by itself.
- User-visible copy is plain Chinese; no per-second countdown (minutes only). Labels exactly: `今日剩余 N 分钟`, `今日剩余 N 分钟，快用完了`, `今日剩余不到 1 分钟`, `今日时长已用完 · 明早 6:00 恢复`.
- No Doubao, no key encryption, no cloud-function port in this plan.
- Test runs: `AIVTuber.Tests` targets x64 (`PlatformTarget`), so on an arm64 Mac `dotnet test` aborts with "install X64 .NET". Run test commands on Windows or with an x64 .NET; on the Mac use `dotnet build AIVTuber.Tests/AIVTuber.Tests.csproj` to prove it compiles. Do not commit `packages.lock.json` changes produced by a local restore.

## Review Focus

- Same heartbeat delivered twice (retry after timeout) must not deduct twice. → Task 2 test `Heartbeat_RepeatedCumulativeValue_DoesNotDeductTwice`.
- Heartbeat arriving after the quota-day boundary: usage lands in the new day, remaining refills. → Task 2 test `QuotaResetsAtSixBeijing`.
- Client reports a huge `active_seconds` right after login (tampered/buggy): credit is capped by elapsed time. → Task 2 test `Heartbeat_CreditIsCappedByElapsedTime`.
- Paused streamer must not lose time while paused, and a system-clock rollback must not add or remove time. → Task 4 tests `ActiveSeconds_*`.
- Quota hits zero while the reply is still being spoken: no new turn may start, the sentence finishes, then pause. → Task 5 tests `WindDown_*`.
- Streamer clicks 继续陪播 while exhausted (or via stale UI): refused, still paused. → Task 5 test `Resume_IsRefusedWhileExhausted_AndAllowedAfterRestore`.

---

### Task 1: Quota calendar and ledger storage

**Files:**
- Create: `AIVTuber.AuthServer/QuotaCalendar.cs`
- Modify: `AIVTuber.AuthServer/AuthStore.cs`
- Modify: `AIVTuber.AuthServer/AuthModels.cs` (options only)
- Test: `AIVTuber.Tests/Auth/QuotaStoreTests.cs` (create)

**Interfaces:**
- Produces: `QuotaCalendar.DayOf(DateTimeOffset) -> string` (`yyyy-MM-dd`), `QuotaCalendar.ResetsAfter(DateTimeOffset) -> DateTimeOffset` (UTC+8 offset, 06:00 of the next quota day).
- Produces on `AuthStore`: `string? GetSetting(string key)`, `void SetSetting(string key, string value)`, `int GetUsedSeconds(string accountId, string quotaDay)`, `void RecordReport(string tokenHash, string accountId, string quotaDay, long reportedActiveSeconds, int creditedSeconds, DateTimeOffset now)`, `IReadOnlyList<(string Day, int UsedSeconds)> ListUsage(string accountId, int days)`.
- Produces: `AccountRow` gains trailing optional `int? DailyQuotaSeconds = null, int BonusSeconds = 0, string? BonusDay = null`; `SessionRow` gains trailing `DateTimeOffset LastSeenAt, long ReportedActiveSeconds`; `UpdateAccount` accepts columns `daily_quota_seconds`, `bonus_seconds`, `bonus_day` and `null` values; `AuthServerOptions` gains `int DefaultDailyQuotaSeconds = 3600`, `int MaxCatchUpSeconds = 30`.

- [ ] **Step 1: Write the failing tests**

Create `AIVTuber.Tests/Auth/QuotaStoreTests.cs`:

```csharp
using AIVTuber.AuthServer;
using Microsoft.Data.Sqlite;

namespace AIVTuber.Tests.Auth;

public sealed class QuotaStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"quota-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    [Theory]
    [InlineData("2026-10-01T21:59:00Z", "2026-10-01")] // 05:59 Beijing, still the previous quota day
    [InlineData("2026-10-01T22:00:00Z", "2026-10-02")] // 06:00 Beijing, new quota day
    [InlineData("2026-10-02T03:00:00Z", "2026-10-02")]
    public void DayOf_SwitchesAtSixBeijing(string utc, string expectedDay)
    {
        Assert.Equal(expectedDay, QuotaCalendar.DayOf(DateTimeOffset.Parse(utc)));
    }

    [Theory]
    [InlineData("2026-10-01T21:59:00Z", "2026-10-01T22:00:00Z")]
    [InlineData("2026-10-01T22:00:00Z", "2026-10-02T22:00:00Z")]
    public void ResetsAfter_IsNextSixBeijing(string utc, string expectedUtc)
    {
        var reset = QuotaCalendar.ResetsAfter(DateTimeOffset.Parse(utc));
        Assert.Equal(DateTimeOffset.Parse(expectedUtc), reset);
        Assert.Equal(TimeSpan.FromHours(8), reset.Offset);
    }

    [Fact]
    public void Settings_RoundTripAndOverwrite()
    {
        using var store = AuthStore.Open(_db);
        Assert.Null(store.GetSetting("daily_quota_seconds"));
        store.SetSetting("daily_quota_seconds", "3600");
        store.SetSetting("daily_quota_seconds", "7200");
        Assert.Equal("7200", store.GetSetting("daily_quota_seconds"));
    }

    [Fact]
    public void RecordReport_AddsUsageAndAdvancesSession()
    {
        using var store = AuthStore.Open(_db);
        var now = new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
        store.InsertAccount(new AccountRow("acc_1", "alice", "h", true, now.AddDays(30), "p", 0, ""), now);
        store.InsertSession("tok", "acc_1", "p", "0.36.0", now);

        store.RecordReport("tok", "acc_1", "2026-09-26", reportedActiveSeconds: 55, creditedSeconds: 55, now.AddSeconds(60));
        store.RecordReport("tok", "acc_1", "2026-09-26", reportedActiveSeconds: 100, creditedSeconds: 45, now.AddSeconds(120));

        Assert.Equal(100, store.GetUsedSeconds("acc_1", "2026-09-26"));
        var session = store.FindSession("tok")!;
        Assert.Equal(100, session.ReportedActiveSeconds);
        Assert.Equal(now.AddSeconds(120), session.LastSeenAt);
        Assert.Equal([("2026-09-26", 100)], store.ListUsage("acc_1", 7));
    }

    [Fact]
    public void UpdateAccount_QuotaColumns_AcceptNull()
    {
        using var store = AuthStore.Open(_db);
        var now = new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
        store.InsertAccount(new AccountRow("acc_1", "alice", "h", true, now.AddDays(30), "p", 0, ""), now);

        Assert.True(store.UpdateAccount("alice", "daily_quota_seconds", 7200));
        Assert.Equal(7200, store.FindAccountByUsername("alice")!.DailyQuotaSeconds);
        Assert.True(store.UpdateAccount("alice", "daily_quota_seconds", null!));
        Assert.Null(store.FindAccountByUsername("alice")!.DailyQuotaSeconds);
    }

    [Fact]
    public void Open_MigratesAnOldDatabaseWithoutLosingAccounts()
    {
        using (var raw = new SqliteConnection($"Data Source={_db}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE accounts (id TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    password_hash TEXT NOT NULL, enabled INTEGER NOT NULL, valid_until TEXT NOT NULL,
                    profile_id TEXT NOT NULL, min_credential_revision INTEGER NOT NULL DEFAULT 0,
                    note TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL);
                CREATE TABLE sessions (token_hash TEXT PRIMARY KEY, account_id TEXT NOT NULL, profile_id TEXT NOT NULL,
                    app_version TEXT NOT NULL, created_at TEXT NOT NULL, last_seen_at TEXT NOT NULL, revoked_at TEXT NULL);
                INSERT INTO accounts VALUES ('acc_old','old','h',1,'2030-01-01T00:00:00.0000000Z','p',0,'','2026-01-01T00:00:00.0000000Z');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = AuthStore.Open(_db);
        var account = store.FindAccountByUsername("old")!;
        Assert.Null(account.DailyQuotaSeconds);
        Assert.Equal(0, account.BonusSeconds);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter FullyQualifiedName~QuotaStoreTests`
Expected: compile errors (`QuotaCalendar`, `GetSetting`, `RecordReport`, … do not exist).

- [ ] **Step 3: Implement**

Create `AIVTuber.AuthServer/QuotaCalendar.cs`:

```csharp
using System.Globalization;

namespace AIVTuber.AuthServer;

/// <summary>Quota days run from 06:00 to 06:00 Beijing time (UTC+8), so a late-night stream
/// belongs to the day it started on.</summary>
public static class QuotaCalendar
{
    private static readonly TimeSpan Zone = TimeSpan.FromHours(8);
    public const int ResetHour = 6;

    public static string DayOf(DateTimeOffset now) =>
        now.ToOffset(Zone).AddHours(-ResetHour).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateTimeOffset ResetsAfter(DateTimeOffset now)
    {
        var shifted = now.ToOffset(Zone).AddHours(-ResetHour);
        return new DateTimeOffset(shifted.Date.AddDays(1).AddHours(ResetHour), Zone);
    }
}
```

In `AIVTuber.AuthServer/AuthModels.cs`, add to `AuthServerOptions`:

```csharp
    /// <summary>Daily companion time when neither the account nor the global setting says otherwise.</summary>
    public int DefaultDailyQuotaSeconds { get; set; } = 3600;
    /// <summary>Slack added to the elapsed time when capping what one heartbeat may credit.</summary>
    public int MaxCatchUpSeconds { get; set; } = 30;
```

In `AIVTuber.AuthServer/AuthStore.cs`:

1. Replace the two record declarations:

```csharp
public sealed record AccountRow(
    string Id,
    string Username,
    string PasswordHash,
    bool Enabled,
    DateTimeOffset ValidUntil,
    string ProfileId,
    int MinCredentialRevision,
    string Note,
    int? DailyQuotaSeconds = null,
    int BonusSeconds = 0,
    string? BonusDay = null);

public sealed record SessionRow(
    string TokenHash,
    string AccountId,
    string ProfileId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset LastSeenAt,
    long ReportedActiveSeconds);
```

2. In `Open`, extend the CREATE script with the two new tables and, after `cmd.ExecuteNonQuery();`, run column migrations:

```csharp
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS usage (
                account_id TEXT NOT NULL REFERENCES accounts(id),
                quota_day TEXT NOT NULL,
                used_seconds INTEGER NOT NULL,
                PRIMARY KEY (account_id, quota_day)
            );
```

```csharp
        AddColumnIfMissing(db, "accounts", "daily_quota_seconds", "INTEGER NULL");
        AddColumnIfMissing(db, "accounts", "bonus_seconds", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "accounts", "bonus_day", "TEXT NULL");
        AddColumnIfMissing(db, "sessions", "reported_active_seconds", "INTEGER NOT NULL DEFAULT 0");
```

```csharp
    private static void AddColumnIfMissing(SqliteConnection db, string table, string column, string ddl)
    {
        using (var probe = db.CreateCommand())
        {
            probe.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $c";
            probe.Parameters.AddWithValue("$c", column);
            if (probe.ExecuteScalar() is not null) return;
        }
        using var alter = db.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {ddl}";
        alter.ExecuteNonQuery();
    }
```

3. `AccountColumns` and `ReadAccount`:

```csharp
    private const string AccountColumns =
        "id, username, password_hash, enabled, valid_until, profile_id, min_credential_revision, note, " +
        "daily_quota_seconds, bonus_seconds, bonus_day";

    private static AccountRow ReadAccount(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0,
        Parse(r.GetString(4)), r.GetString(5), r.GetInt32(6), r.GetString(7),
        r.IsDBNull(8) ? null : r.GetInt32(8), r.GetInt32(9), r.IsDBNull(10) ? null : r.GetString(10));
```

4. `UpdateAccount`: extend the whitelist with `"daily_quota_seconds" or "bonus_seconds" or "bonus_day"` and replace the value line with:

```csharp
            cmd.Parameters.AddWithValue("$v", value switch
            {
                null => DBNull.Value,
                DateTimeOffset t => Format(t),
                _ => value,
            });
```

5. `FindSession`: select `token_hash, account_id, profile_id, created_at, revoked_at, last_seen_at, reported_active_seconds` and build `new SessionRow(..., Parse(reader.GetString(5)), reader.GetInt64(6))`.

6. Add the new members (inside the class, before `Dispose`):

```csharp
    public string? GetSetting(string key)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    public int GetUsedSeconds(string accountId, string quotaDay)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT used_seconds FROM usage WHERE account_id = $a AND quota_day = $d";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$d", quotaDay);
            return cmd.ExecuteScalar() is long used ? (int)used : 0;
        }
    }

    /// <summary>One transaction: credit the ledger, remember the session's cumulative counter and
    /// refresh its last-seen time, so a repeated report can never deduct twice.</summary>
    public void RecordReport(string tokenHash, string accountId, string quotaDay,
        long reportedActiveSeconds, int creditedSeconds, DateTimeOffset now)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            if (creditedSeconds > 0)
            {
                using var usage = _db.CreateCommand();
                usage.Transaction = tx;
                usage.CommandText = """
                    INSERT INTO usage (account_id, quota_day, used_seconds) VALUES ($a, $d, $s)
                    ON CONFLICT(account_id, quota_day) DO UPDATE SET used_seconds = used_seconds + $s
                    """;
                usage.Parameters.AddWithValue("$a", accountId);
                usage.Parameters.AddWithValue("$d", quotaDay);
                usage.Parameters.AddWithValue("$s", creditedSeconds);
                usage.ExecuteNonQuery();
            }
            using var session = _db.CreateCommand();
            session.Transaction = tx;
            session.CommandText = "UPDATE sessions SET reported_active_seconds = $r, last_seen_at = $n WHERE token_hash = $t";
            session.Parameters.AddWithValue("$r", reportedActiveSeconds);
            session.Parameters.AddWithValue("$n", Format(now));
            session.Parameters.AddWithValue("$t", tokenHash);
            session.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public IReadOnlyList<(string Day, int UsedSeconds)> ListUsage(string accountId, int days)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT quota_day, used_seconds FROM usage WHERE account_id = $a ORDER BY quota_day DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$n", days);
            using var reader = cmd.ExecuteReader();
            var rows = new List<(string, int)>();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt32(1)));
            return rows;
        }
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter "FullyQualifiedName~QuotaStoreTests|FullyQualifiedName~AuthServiceTests|FullyQualifiedName~AdminCliTests"`
Expected: PASS (the existing auth tests still compile because new record parameters are trailing/optional; if any test constructs `SessionRow` positionally, add `DateTimeOffset.MinValue, 0` to it).

- [ ] **Step 5: Commit**

```bash
git add AIVTuber.AuthServer/QuotaCalendar.cs AIVTuber.AuthServer/AuthStore.cs AIVTuber.AuthServer/AuthModels.cs AIVTuber.Tests/Auth/QuotaStoreTests.cs
git commit -m "Auth server: quota calendar and usage ledger storage"
```

---

### Task 2: Quota accounting in login and heartbeat

**Files:**
- Modify: `AIVTuber.AuthServer/AuthModels.cs` (`HeartbeatRequest`, `AuthResult`)
- Modify: `AIVTuber.AuthServer/AuthService.cs`
- Modify: `AIVTuber.AuthServer/AuthServerApp.cs`
- Test: `AIVTuber.Tests/Auth/AuthServiceQuotaTests.cs` (create)

**Interfaces:**
- Consumes: Task 1 store/calendar API.
- Produces: `HeartbeatRequest(string ProfileId, long ActiveSeconds = 0)`; `AuthResult` gains trailing `int? QuotaSeconds = null, int? QuotaRemainingSeconds = null, DateTimeOffset? QuotaResetsAt = null` (wire: `quota_seconds`, `quota_remaining_seconds`, `quota_resets_at`); `AuthService.Heartbeat(string token, string profileId, long activeSeconds = 0)`; `AuthService.QuotaFor(AccountRow, DateTimeOffset) -> (int Quota, int Remaining, DateTimeOffset ResetsAt)`; `AuthService.DefaultDailyQuotaSeconds` (int, read from setting `daily_quota_seconds`, fallback `options.DefaultDailyQuotaSeconds`); `AuthService.SetDefaultDailyQuota(int seconds)`, `SetDailyQuota(string username, int? seconds)`, `AddTodayBonus(string username, int seconds)`, `Usage(string username, int days)`.

- [ ] **Step 1: Write the failing tests**

Create `AIVTuber.Tests/Auth/AuthServiceQuotaTests.cs`:

```csharp
using AIVTuber.AuthServer;
using Microsoft.Data.Sqlite;

namespace AIVTuber.Tests.Auth;

public sealed class AuthServiceQuotaTests : IDisposable
{
    // 2026-09-26 16:00 Beijing → quota day 2026-09-26, resets 2026-09-27 06:00 Beijing (= 22:00Z).
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"auth-quota-{Guid.NewGuid():N}.db");
    private readonly ManualClock _clock = new(Start);
    private readonly AuthStore _store;
    private readonly AuthService _service;

    public AuthServiceQuotaTests()
    {
        _store = AuthStore.Open(_db);
        _service = new AuthService(_store, _clock, new AuthServerOptions());
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddDays(60));
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    private string LoginToken()
    {
        var login = _service.Login(new LoginRequest("alice", "pw-alice-1", "streamer-017", "0.36.0", 1), "t");
        Assert.Equal(AuthStatus.Ok, login.Status);
        return login.SessionToken!;
    }

    private AuthResult Beat(string token, long active) => _service.Heartbeat(token, "streamer-017", active);

    [Fact]
    public void Login_ReturnsDefaultQuotaAndResetTime()
    {
        var login = _service.Login(new LoginRequest("alice", "pw-alice-1", "streamer-017", "0.36.0", 1), "t");

        Assert.Equal(3600, login.QuotaSeconds);
        Assert.Equal(3600, login.QuotaRemainingSeconds);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 22, 0, 0, TimeSpan.Zero), login.QuotaResetsAt);
    }

    [Fact]
    public void Heartbeat_DeductsTheReportedDelta()
    {
        var token = LoginToken();
        _clock.Advance(TimeSpan.FromSeconds(60));

        var reply = Beat(token, 55);

        Assert.Equal(AuthStatus.Ok, reply.Status);
        Assert.Equal(3545, reply.QuotaRemainingSeconds);
    }

    [Fact]
    public void Heartbeat_RepeatedCumulativeValue_DoesNotDeductTwice()
    {
        var token = LoginToken();
        _clock.Advance(TimeSpan.FromSeconds(60));
        Beat(token, 55);
        _clock.Advance(TimeSpan.FromSeconds(60));

        var reply = Beat(token, 55); // retry of the same report

        Assert.Equal(3545, reply.QuotaRemainingSeconds);
    }

    [Fact]
    public void Heartbeat_DecreasingCumulativeValue_CreditsNothing()
    {
        var token = LoginToken();
        _clock.Advance(TimeSpan.FromSeconds(60));
        Beat(token, 55);
        _clock.Advance(TimeSpan.FromSeconds(60));

        var reply = Beat(token, 40);

        Assert.Equal(3545, reply.QuotaRemainingSeconds);
    }

    [Fact]
    public void Heartbeat_CreditIsCappedByElapsedTime()
    {
        var token = LoginToken();
        _clock.Advance(TimeSpan.FromSeconds(60));

        var reply = Beat(token, 500); // 60 s elapsed + 30 s slack = at most 90 credited

        Assert.Equal(3510, reply.QuotaRemainingSeconds);
    }

    [Fact]
    public void TwoSessionsOfOneAccount_ShareOneLedger()
    {
        var first = LoginToken();
        var second = LoginToken();
        _clock.Advance(TimeSpan.FromSeconds(60));

        Beat(first, 60);
        var reply = Beat(second, 60);

        Assert.Equal(3480, reply.QuotaRemainingSeconds);
    }

    [Fact]
    public void AccountQuotaAndTodayBonus_ChangeTheTotal_BonusExpiresNextDay()
    {
        var token = LoginToken();
        _service.SetDailyQuota("alice", 7200);
        _service.AddTodayBonus("alice", 600);
        _clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(7800, Beat(token, 0).QuotaSeconds);

        _clock.Advance(TimeSpan.FromHours(20)); // past 06:00 Beijing the next morning
        Assert.Equal(7200, Beat(token, 0).QuotaSeconds);

        _service.SetDailyQuota("alice", null); // back to the global default
        Assert.Equal(3600, Beat(token, 0).QuotaSeconds);
    }

    [Fact]
    public void GlobalDefault_CanBeChanged()
    {
        _service.SetDefaultDailyQuota(1800);
        Assert.Equal(1800, LoginAndQuota());
        int LoginAndQuota() => _service.Login(new LoginRequest("alice", "pw-alice-1", "streamer-017", "0.36.0", 1), "t").QuotaSeconds!.Value;
    }

    [Fact]
    public void Exhausted_IsStillOk_AndTheSessionStaysValid()
    {
        var token = LoginToken();
        _service.SetDailyQuota("alice", 100);
        _clock.Advance(TimeSpan.FromSeconds(60));
        Beat(token, 60);
        _clock.Advance(TimeSpan.FromSeconds(60));

        var reply = Beat(token, 120);
        var next = Beat(token, 120);

        Assert.Equal(AuthStatus.Ok, reply.Status);
        Assert.Equal(0, reply.QuotaRemainingSeconds);
        Assert.Equal(AuthStatus.Ok, next.Status);
    }

    [Fact]
    public void QuotaResetsAtSixBeijing()
    {
        var token = LoginToken();
        _service.SetDailyQuota("alice", 100);
        _clock.Advance(TimeSpan.FromSeconds(60));
        Beat(token, 60);
        Assert.Equal(40, Beat(token, 60).QuotaRemainingSeconds);

        _clock.Advance(TimeSpan.FromHours(14)); // 08:00Z + 60 s + 14 h = 22:01Z = 06:01 Beijing, the next quota day
        var reply = Beat(token, 60);

        Assert.Equal(100, reply.QuotaRemainingSeconds);
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 22, 0, 0, TimeSpan.Zero), reply.QuotaResetsAt);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter FullyQualifiedName~AuthServiceQuotaTests`
Expected: compile errors (new `AuthResult` members, `Heartbeat` 3-arg overload, `SetDailyQuota`, … missing).

- [ ] **Step 3: Implement**

`AIVTuber.AuthServer/AuthModels.cs`:

```csharp
public sealed record HeartbeatRequest(string ProfileId, long ActiveSeconds = 0);
```

and extend `AuthResult` (trailing parameters, keep `Denied`):

```csharp
public sealed record AuthResult(
    AuthStatus Status,
    string? SessionToken = null,
    string? AccountId = null,
    DateTimeOffset? ServerTime = null,
    DateTimeOffset? LeaseValidUntil = null,
    DateTimeOffset? AccountValidUntil = null,
    int HeartbeatSeconds = 0,
    int? QuotaSeconds = null,
    int? QuotaRemainingSeconds = null,
    DateTimeOffset? QuotaResetsAt = null)
{
    public static AuthResult Denied(AuthStatus status, DateTimeOffset now) => new(status, ServerTime: now);
}
```

`AIVTuber.AuthServer/AuthService.cs`:

1. Replace `Granted`:

```csharp
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
```

2. Change `Heartbeat` signature and the tail (replace `store.TouchSession(tokenHash, now); return Granted(account, now);`):

```csharp
    public AuthResult Heartbeat(string token, string profileId, long activeSeconds = 0)
```

```csharp
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
```

(Remove the old `var denied = Check(...)` pair that preceded `TouchSession`; keep only one.)

3. Add the quota members:

```csharp
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
```

(`SetDailyQuota(user, null)` passes `null` through `UpdateAccount`, which now maps it to `DBNull`.)

`AIVTuber.AuthServer/AuthServerApp.cs`: change the heartbeat handler call to `auth.Heartbeat(BearerToken(http), request.ProfileId, request.ActiveSeconds)`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter "FullyQualifiedName~AuthServiceQuotaTests|FullyQualifiedName~AuthServiceTests|FullyQualifiedName~AdminCliTests"`
Expected: PASS. Note `QuotaResetsAtSixBeijing`: 08:00Z + 60 s … + 14 h = 22:01Z, which is after the reset, so the day rolls over.

- [ ] **Step 5: Commit**

```bash
git add AIVTuber.AuthServer/AuthModels.cs AIVTuber.AuthServer/AuthService.cs AIVTuber.AuthServer/AuthServerApp.cs AIVTuber.Tests/Auth/AuthServiceQuotaTests.cs
git commit -m "Auth server: deduct companion time from a daily ledger on each heartbeat"
```

---

### Task 3: Operator commands

**Files:**
- Modify: `AIVTuber.AuthServer/AdminCli.cs`
- Modify: `AIVTuber.Tests/Auth/AdminCliTests.cs` (append tests)

**Interfaces:**
- Consumes: `AuthService.SetDefaultDailyQuota/SetDailyQuota/AddTodayBonus/Usage/QuotaFor`, `AuthStore.GetUsedSeconds`.
- Produces: admin commands `set-default-daily --minutes N`, `set-daily --username U (--minutes N | --default)`, `add-today --username U --minutes N`, `usage --username U [--days 7]`; `list` lines gain `today_used_min=X today_quota_min=Y`. `--default` is a bare flag.

- [ ] **Step 1: Write the failing tests** (append inside `AdminCliTests`)

```csharp
    private void CreateAlice() =>
        Run("secret-1\n", "create", "--username", "alice", "--profile", "streamer-017", "--days", "30", "--password-stdin");

    [Fact]
    public void SetDefaultDaily_ChangesEveryoneWithoutAnOverride()
    {
        CreateAlice();
        Assert.Equal(0, Run("", "set-default-daily", "--minutes", "30").Code);

        Assert.Equal(1800, Login("secret-1").QuotaSeconds);
    }

    [Fact]
    public void SetDaily_OverridesOneAccount_AndDefaultFlagRestores()
    {
        CreateAlice();
        Run("", "set-default-daily", "--minutes", "30");

        Assert.Equal(0, Run("", "set-daily", "--username", "alice", "--minutes", "120").Code);
        Assert.Equal(7200, Login("secret-1").QuotaSeconds);

        Assert.Equal(0, Run("", "set-daily", "--username", "alice", "--default").Code);
        Assert.Equal(1800, Login("secret-1").QuotaSeconds);
    }

    [Fact]
    public void AddToday_AddsToTodaysQuota()
    {
        CreateAlice();

        Assert.Equal(0, Run("", "add-today", "--username", "alice", "--minutes", "30").Code);

        Assert.Equal(3600 + 1800, Login("secret-1").QuotaSeconds);
    }

    [Fact]
    public void Usage_ListsDaysAndList_ShowsTodayColumns()
    {
        CreateAlice();
        var login = Login("secret-1");
        _clock.Advance(TimeSpan.FromSeconds(60));
        using (var store = AuthStore.Open(_db))
        {
            var service = new AuthService(store, _clock, new AuthServerOptions());
            service.Heartbeat(login.SessionToken!, "streamer-017", 60);
        }
        SqliteConnection.ClearAllPools();

        var usage = Run("", "usage", "--username", "alice", "--days", "7");
        var list = Run("", "list");

        Assert.Equal(0, usage.Code);
        Assert.Contains("2026-09-26", usage.Out);
        Assert.Contains("1.0", usage.Out); // minutes
        Assert.Contains("today_used_min=1.0", list.Out);
        Assert.Contains("today_quota_min=60.0", list.Out);
    }

    [Fact]
    public void QuotaCommands_RejectNonPositiveMinutes()
    {
        CreateAlice();
        Assert.Equal(1, Run("", "set-default-daily", "--minutes", "0").Code);
        Assert.Equal(1, Run("", "add-today", "--username", "alice", "--minutes", "-5").Code);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter FullyQualifiedName~AdminCliTests`
Expected: new tests FAIL (unknown command → usage text, exit code 2).

- [ ] **Step 3: Implement**

In `Usage`, add under `list`:

```
          set-default-daily --minutes N                          （所有账号的默认每日时长）
          set-daily         --username U (--minutes N | --default)
          add-today         --username U --minutes N             （今天临时加时，明早 6 点作废）
          usage             --username U [--days 7]
```

In `Parse`, treat `--default` like `--password-stdin`: `if (name is "password-stdin" or "default") { opts[name] = "true"; continue; }`.

Add cases before `case "list"`:

```csharp
                case "set-default-daily":
                {
                    var minutes = ParseMinutes(opts);
                    service.SetDefaultDailyQuota(minutes * 60);
                    stdout.WriteLine($"default daily quota = {minutes} min");
                    return 0;
                }
                case "set-daily":
                {
                    var username = Required(opts, "username");
                    if (opts.ContainsKey("default"))
                    {
                        service.SetDailyQuota(username, null);
                        stdout.WriteLine($"{username} daily quota = default");
                    }
                    else
                    {
                        var minutes = ParseMinutes(opts);
                        service.SetDailyQuota(username, minutes * 60);
                        stdout.WriteLine($"{username} daily quota = {minutes} min");
                    }
                    return 0;
                }
                case "add-today":
                {
                    var username = Required(opts, "username");
                    var minutes = ParseMinutes(opts);
                    service.AddTodayBonus(username, minutes * 60);
                    stdout.WriteLine($"{username} +{minutes} min for today");
                    return 0;
                }
                case "usage":
                {
                    var username = Required(opts, "username");
                    var days = int.Parse(opts.GetValueOrDefault("days") ?? "7", CultureInfo.InvariantCulture);
                    foreach (var (day, used) in service.Usage(username, days))
                        stdout.WriteLine($"{day}\t{used / 60.0:F1} min");
                    return 0;
                }
```

Replace the `list` body so each line ends with the quota columns:

```csharp
                case "list":
                    foreach (var a in store.ListAccounts())
                    {
                        var (quota, remaining, _) = service.QuotaFor(a, now);
                        stdout.WriteLine(
                            $"{a.Id}\t{a.Username}\tprofile={a.ProfileId}\tenabled={a.Enabled}\t" +
                            $"valid_until={a.ValidUntil:O}\tmin_rev={a.MinCredentialRevision}\t" +
                            $"today_used_min={(quota - remaining) / 60.0:F1}\ttoday_quota_min={quota / 60.0:F1}\t{a.Note}");
                    }
                    return 0;
```

Add helper:

```csharp
    private static int ParseMinutes(Dictionary<string, string> opts)
    {
        var minutes = int.Parse(Required(opts, "minutes"), CultureInfo.InvariantCulture);
        if (minutes <= 0) throw new ArgumentException("--minutes 必须大于 0。");
        return minutes;
    }
```

(`ArgumentException` is already mapped to exit code 1 by the existing catch.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter FullyQualifiedName~AdminCliTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add AIVTuber.AuthServer/AdminCli.cs AIVTuber.Tests/Auth/AdminCliTests.cs
git commit -m "Auth server: admin commands for daily quota, bonus and usage"
```

---

### Task 4: Client contracts, API client and local quota in CloudLicense

**Files:**
- Modify: `AIVTuber.Core/Auth/AuthContracts.cs`
- Modify: `AIVTuber.Core/Auth/AuthApiClient.cs`
- Modify: `AIVTuber.Core/Auth/CloudLicense.cs`
- Modify: `AIVTuber.Tests/Auth/CloudLicenseTests.cs` (`FakeAuthApi` + new tests)
- Modify: `AIVTuber.Tests/Auth/AuthEndToEndTests.cs` (one assertion)

**Interfaces:**
- Produces: `record QuotaReply(int QuotaSeconds, int RemainingSeconds, DateTimeOffset ResetsAt)`; `AuthReply` gains trailing `QuotaReply? Quota = null`; `IAuthApi.HeartbeatAsync(string token, string profileId, long activeSeconds, CancellationToken ct = default)`.
- Produces: `interface ICompanionQuota { bool QuotaManaged { get; } int QuotaSeconds { get; } int QuotaRemainingSeconds { get; } DateTimeOffset? QuotaResetsAt { get; } void SetCompanionActive(bool active); event Action? QuotaChanged; event Action? QuotaExhausted; event Action? QuotaRestored; }` implemented by `CloudLicense`; plus `CloudLicense.ActiveSeconds` (long) and `CloudLicense.CheckQuota()`.
- `QuotaRemainingSeconds` is `int.MaxValue` when `!QuotaManaged`.

- [ ] **Step 1: Write the failing tests**

In `AIVTuber.Tests/Auth/CloudLicenseTests.cs` update `FakeAuthApi`:

```csharp
    public long LastHeartbeatActiveSeconds { get; private set; }

    Task<AuthReply> IAuthApi.HeartbeatAsync(string token, string profileId, long activeSeconds, CancellationToken ct)
    {
        LastHeartbeatToken = token;
        LastHeartbeatActiveSeconds = activeSeconds;
        return Heartbeat(token, ct);
    }
```

Append to `CloudLicenseTests` (same class, uses existing `_clock`, `_api`, `NewLicense`, `ServerStart`):

```csharp
    private AuthReply OkWithQuota(int quota = 3600, int remaining = 3600, DateTimeOffset? serverNow = null)
    {
        var now = serverNow ?? ServerStart;
        return new AuthReply(AuthCode.Ok, "tok-1", "acc_1", now, now.AddSeconds(180), now.AddDays(1), 60,
            new QuotaReply(quota, remaining, now.AddHours(14)));
    }

    private async Task<CloudLicense> LoginWithQuotaAsync(int remaining = 3600)
    {
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(OkWithQuota(remaining: remaining));
        await license.LoginAsync("alice", "pw");
        return license;
    }

    [Fact]
    public async Task ActiveSeconds_CountOnlyWhileTheCompanionIsActive()
    {
        var license = await LoginWithQuotaAsync();

        _clock.Advance(TimeSpan.FromSeconds(90));
        license.SetCompanionActive(false);
        _clock.Advance(TimeSpan.FromSeconds(600));
        license.SetCompanionActive(true);
        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(100, license.ActiveSeconds);
    }

    [Fact]
    public async Task ActiveSeconds_IgnoreSystemClockChanges()
    {
        var license = await LoginWithQuotaAsync();

        _clock.SetWall(ServerStart.AddDays(-3));
        _clock.AdvanceMonotonicOnly(TimeSpan.FromSeconds(30));

        Assert.Equal(30, license.ActiveSeconds);
    }

    [Fact]
    public async Task Heartbeat_ReportsTheCumulativeActiveSeconds()
    {
        var license = await LoginWithQuotaAsync();
        _api.Heartbeat = (_, _) => Task.FromResult(OkWithQuota(remaining: 3540, serverNow: ServerStart.AddSeconds(60)));
        _clock.Advance(TimeSpan.FromSeconds(60));

        await license.HeartbeatOnceAsync();

        Assert.Equal(60, _api.LastHeartbeatActiveSeconds);
    }

    [Fact]
    public async Task LocalRemaining_CountsDownOnlyWhileActive()
    {
        var license = await LoginWithQuotaAsync(remaining: 100);

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(70, license.QuotaRemainingSeconds);

        license.SetCompanionActive(false);
        _clock.Advance(TimeSpan.FromSeconds(500));
        Assert.Equal(70, license.QuotaRemainingSeconds);
    }

    [Fact]
    public async Task Exhausted_FiresOnce_ThenRestoredWhenTheServerRefills()
    {
        var license = await LoginWithQuotaAsync(remaining: 50);
        var exhausted = 0;
        var restored = 0;
        license.QuotaExhausted += () => exhausted++;
        license.QuotaRestored += () => restored++;

        _clock.Advance(TimeSpan.FromSeconds(50));
        license.CheckQuota();
        license.CheckQuota();
        Assert.Equal(1, exhausted);

        license.SetCompanionActive(false);
        _api.Heartbeat = (_, _) => Task.FromResult(OkWithQuota(remaining: 3600, serverNow: ServerStart.AddHours(14)));
        await license.HeartbeatOnceAsync();

        Assert.Equal(1, restored);
        Assert.Equal(3600, license.QuotaRemainingSeconds);
    }

    [Fact]
    public async Task ExhaustedQuota_DoesNotRevokeTheLicense()
    {
        var license = await LoginWithQuotaAsync(remaining: 0);
        license.CheckQuota();

        Assert.True(license.IsAllowed);
        Assert.Empty(_revocations);
    }

    [Fact]
    public async Task ReplyWithoutQuota_MeansNoQuotaIsEnforced()
    {
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(Ok());
        await license.LoginAsync("alice", "pw");

        Assert.False(license.QuotaManaged);
        Assert.Equal(int.MaxValue, license.QuotaRemainingSeconds);
    }
```

In `AIVTuber.Tests/Auth/AuthEndToEndTests.cs`, in an existing happy-path test that logs in through the real `AuthApiClient`, add after the successful login assertion: `Assert.True(license.QuotaManaged); Assert.Equal(3600, license.QuotaRemainingSeconds);` (use the variable name the test uses for its `CloudLicense`).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter "FullyQualifiedName~CloudLicense|FullyQualifiedName~AuthEndToEnd"`
Expected: compile errors (`QuotaReply`, `SetCompanionActive`, … missing).

- [ ] **Step 3: Implement**

`AIVTuber.Core/Auth/AuthContracts.cs`:

```csharp
/// <summary>Today's companion-time allowance as reported by the account service.</summary>
public sealed record QuotaReply(int QuotaSeconds, int RemainingSeconds, DateTimeOffset ResetsAt);
```

Add trailing `QuotaReply? Quota = null` to `AuthReply`. Change `IAuthApi`:

```csharp
    Task<AuthReply> HeartbeatAsync(string token, string profileId, long activeSeconds, CancellationToken ct = default);
```

Add:

```csharp
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
```

`AIVTuber.Core/Auth/AuthApiClient.cs`:
- `HeartbeatAsync(string token, string profileId, long activeSeconds, ...)` sends `new { profile_id = profileId, active_seconds = activeSeconds }`.
- Add to `WireReply`: `int? QuotaSeconds, int? QuotaRemainingSeconds, DateTimeOffset? QuotaResetsAt`.
- After the `wire?.Status is null` check, replace the return with:

```csharp
            var status = ParseStatus(wire.Status);
            QuotaReply? quota = null;
            if (status == AuthCode.Ok)
            {
                // The service always sends the quota on success; a reply without it is a broken
                // proxy or an outdated service, not an account verdict.
                if (wire.QuotaSeconds is not { } total || wire.QuotaRemainingSeconds is not { } left || wire.QuotaResetsAt is not { } resets)
                    throw new AuthTransportException("鉴权服务缺少时长额度信息");
                quota = new QuotaReply(total, Math.Max(0, left), resets);
            }
            return new AuthReply(status, wire.SessionToken, wire.AccountId, wire.ServerTime,
                wire.LeaseValidUntil, wire.AccountValidUntil, wire.HeartbeatSeconds, quota);
```

`AIVTuber.Core/Auth/CloudLicense.cs` — declare `public sealed class CloudLicense : ICloudAccess, ICompanionQuota, IAsyncDisposable` and add:

Fields:

```csharp
    private bool _companionActive = true;
    private long _activeAccumTicks;
    private long? _activeSinceTimestamp;
    private bool _quotaManaged;
    private int _quotaSeconds;
    private int _quotaRemainingAtReport;
    private long _activeSecondsAtReport;
    private DateTimeOffset? _quotaResetsAt;
    private bool _exhaustedRaised;
```

Members:

```csharp
    public event Action? QuotaChanged;
    public event Action? QuotaExhausted;
    public event Action? QuotaRestored;

    public bool QuotaManaged { get { lock (_sync) return _quotaManaged; } }
    public int QuotaSeconds { get { lock (_sync) return _quotaSeconds; } }
    public DateTimeOffset? QuotaResetsAt { get { lock (_sync) return _quotaResetsAt; } }
    public long ActiveSeconds { get { lock (_sync) return ActiveSecondsLocked(); } }

    public int QuotaRemainingSeconds { get { lock (_sync) return RemainingLocked(); } }

    public void SetCompanionActive(bool active)
    {
        lock (_sync)
        {
            if (_companionActive == active) return;
            if (!active) StopCountingLocked();
            _companionActive = active;
            if (active) StartCountingLocked();
        }
    }

    /// <summary>Raises <see cref="QuotaExhausted"/> / <see cref="QuotaRestored"/> on the edges.
    /// Called every second by the background loop and after every reply.</summary>
    public void CheckQuota()
    {
        var raise = false;
        var restore = false;
        lock (_sync)
        {
            if (!_quotaManaged || _state != LicenseState.Active) return;
            var remaining = RemainingLocked();
            if (remaining <= 0 && !_exhaustedRaised) { _exhaustedRaised = true; raise = true; }
            else if (remaining > 0 && _exhaustedRaised) { _exhaustedRaised = false; restore = true; }
        }
        if (raise) QuotaExhausted?.Invoke();
        if (restore) QuotaRestored?.Invoke();
    }

    private long ActiveSecondsLocked()
    {
        var ticks = _activeAccumTicks;
        if (_activeSinceTimestamp is { } since) ticks += _clock.GetTimestamp() - since;
        return ticks / _clock.TimestampFrequency;
    }

    private int RemainingLocked()
    {
        if (!_quotaManaged) return int.MaxValue;
        var usedSinceReport = ActiveSecondsLocked() - _activeSecondsAtReport;
        return (int)Math.Max(0, _quotaRemainingAtReport - usedSinceReport);
    }

    private void StartCountingLocked()
    {
        if (_companionActive && _state == LicenseState.Active && _activeSinceTimestamp is null)
            _activeSinceTimestamp = _clock.GetTimestamp();
    }

    private void StopCountingLocked()
    {
        if (_activeSinceTimestamp is { } since)
        {
            _activeAccumTicks += _clock.GetTimestamp() - since;
            _activeSinceTimestamp = null;
        }
    }
```

Wiring inside existing methods:
- `LoginAsync` success branch (where `_state = LicenseState.Active;`): before `ApplyLeaseLocked`, reset the counter: `_activeAccumTicks = 0; _activeSinceTimestamp = null; _activeSecondsAtReport = 0; _exhaustedRaised = false; StartCountingLocked();` — `StartCountingLocked` must run **after** `_state` is set to Active (it is), then call `ApplyLeaseLocked(reply, sentAt, activeAtSend: 0)`.
- `ApplyLeaseLocked(AuthReply reply, long sentAt, long activeAtSend)`: add at the end:

```csharp
        if (reply.Quota is { } q)
        {
            _quotaManaged = true;
            _quotaSeconds = q.QuotaSeconds;
            _quotaRemainingAtReport = q.RemainingSeconds;
            _activeSecondsAtReport = activeAtSend;
            _quotaResetsAt = q.ResetsAt;
        }
```

- `HeartbeatOnceAsync`: capture `long activeAtSend;` in the first `lock` (`activeAtSend = ActiveSecondsLocked();`), call `_api.HeartbeatAsync(token, _profileId, activeAtSend, ct)`, pass `activeAtSend` to `ApplyLeaseLocked`, and after `Changed?.Invoke(snapshot);` add `QuotaChanged?.Invoke(); CheckQuota();`. After a successful `LoginAsync` (after `Changed?.Invoke(snapshot)` for Active) add `QuotaChanged?.Invoke(); CheckQuota();` as well.
- `Revoke(...)` and `LogoutAsync` (inside their `lock`): `StopCountingLocked(); _exhaustedRaised = false;`.
- `RunLoopAsync`: after `EnforceExpiry();` add `CheckQuota();`.

`Revoke` with `_state` no longer Active followed by `SetCompanionActive(true)` is safe: `StartCountingLocked` checks `_state == Active`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter "FullyQualifiedName~CloudLicense|FullyQualifiedName~AuthEndToEnd|FullyQualifiedName~AccountViewModel"`
Expected: PASS. If `AccountViewModelTests`/`StreamerConsoleTests` fail to compile, they implement `HeartbeatAsync` themselves — switch them to `FakeAuthApi` or add the `long activeSeconds` parameter.

- [ ] **Step 5: Commit**

```bash
git add AIVTuber.Core/Auth AIVTuber.Tests/Auth
git commit -m "Client: report active companion time and count the daily quota down locally"
```

---

### Task 5: Runtime wind-down and resume guard

**Files:**
- Modify: `AIVTuber.Core/Runtime/BotRuntime.cs`
- Test: `AIVTuber.Tests/Auth/RuntimeQuotaTests.cs` (create)

**Interfaces:**
- Consumes: `ICompanionQuota` (Task 4), `BotRuntime.StateTracker` (public `PipelineStateTracker`, `SpeakingStarted(long)`, `SpeakingStopped()`).
- Produces: `BotRuntime.Quota` (`ICompanionQuota?`), `BotRuntime.QuotaExhausted` (bool), `internal TimeSpan QuotaWindDownMax` (default 30 s); `SetCompanionPaused(false)` is a no-op while `QuotaExhausted`.

- [ ] **Step 1: Write the failing tests**

Create `AIVTuber.Tests/Auth/RuntimeQuotaTests.cs`:

```csharp
using AIVTuber.Core.Auth;
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests.Auth;

public sealed class RuntimeQuotaTests
{
    private sealed class FakeQuotaAccess : ICloudAccess, ICompanionQuota
    {
        public bool IsAllowed => true;
        public long Epoch => 1;
        public event Action<string>? Revoked { add { } remove { } }
        public bool QuotaManaged => true;
        public int QuotaSeconds => 3600;
        public int QuotaRemainingSeconds { get; set; } = 100;
        public DateTimeOffset? QuotaResetsAt => null;
        public List<bool> ActiveCalls { get; } = [];
        public void SetCompanionActive(bool active) => ActiveCalls.Add(active);
        public event Action? QuotaChanged { add { } remove { } }
        public event Action? QuotaExhausted;
        public event Action? QuotaRestored;
        public void RaiseExhausted() => QuotaExhausted?.Invoke();
        public void RaiseRestored() => QuotaRestored?.Invoke();
    }

    private static (BotRuntime Runtime, FakeQuotaAccess Access) NewRuntime()
    {
        var runtime = new BotRuntime(new AppConfig(), Path.GetTempPath());
        var access = new FakeQuotaAccess();
        runtime.UseCloudAccess(access);
        return (runtime, access);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(20);
    }

    [Fact]
    public async Task Exhausted_WhileIdle_PausesAtOnce()
    {
        var (runtime, access) = NewRuntime();
        await using var _ = runtime;

        access.RaiseExhausted();
        await WaitUntil(() => runtime.CompanionPaused);

        Assert.True(runtime.CompanionPaused);
        Assert.True(runtime.QuotaExhausted);
        Assert.Contains(false, access.ActiveCalls); // time stops being billed
    }

    [Fact]
    public async Task WindDown_WaitsForTheReplyToFinish_ThenPauses()
    {
        var (runtime, access) = NewRuntime();
        await using var _ = runtime;
        runtime.StateTracker.SpeakingStarted(Environment.TickCount64);

        access.RaiseExhausted();
        await Task.Delay(400);
        Assert.False(runtime.CompanionPaused); // still speaking: the sentence is not cut

        runtime.StateTracker.SpeakingStopped();
        await WaitUntil(() => runtime.CompanionPaused);

        Assert.True(runtime.CompanionPaused);
        Assert.True(runtime.QuotaExhausted);
    }

    [Fact]
    public async Task WindDown_IsCappedWhenTheReplyNeverEnds()
    {
        var (runtime, access) = NewRuntime();
        await using var _ = runtime;
        runtime.QuotaWindDownMax = TimeSpan.FromMilliseconds(300);
        runtime.StateTracker.SpeakingStarted(Environment.TickCount64); // never stops

        access.RaiseExhausted();
        await WaitUntil(() => runtime.CompanionPaused);

        Assert.True(runtime.CompanionPaused);
    }

    [Fact]
    public async Task Resume_IsRefusedWhileExhausted_AndAllowedAfterRestore()
    {
        var (runtime, access) = NewRuntime();
        await using var _ = runtime;
        access.RaiseExhausted();
        await WaitUntil(() => runtime.CompanionPaused);

        runtime.SetCompanionPaused(false);
        Assert.True(runtime.CompanionPaused);

        access.RaiseRestored();
        Assert.False(runtime.QuotaExhausted);
        Assert.True(runtime.CompanionPaused); // restored quota never resumes by itself

        runtime.SetCompanionPaused(false);
        Assert.False(runtime.CompanionPaused);
    }

    [Fact]
    public async Task ManualPause_ThenExhausted_StillMarksExhausted()
    {
        var (runtime, access) = NewRuntime();
        await using var _ = runtime;
        runtime.SetCompanionPaused(true);

        access.RaiseExhausted();

        Assert.True(runtime.QuotaExhausted);
        runtime.SetCompanionPaused(false);
        Assert.True(runtime.CompanionPaused);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter FullyQualifiedName~RuntimeQuotaTests`
Expected: compile errors (`Quota`, `QuotaExhausted`, `QuotaWindDownMax` missing).

- [ ] **Step 3: Implement** (all in `AIVTuber.Core/Runtime/BotRuntime.cs`)

1. Fields/properties next to `_companionPaused` (line ~161):

```csharp
    private ICompanionQuota? _quota;
    private int _quotaWindDown;            // 1 while the current reply is allowed to finish
    private volatile bool _quotaExhausted;

    /// <summary>The daily companion quota, when the account service enforces one.</summary>
    public ICompanionQuota? Quota => _quota;
    /// <summary>True from the moment today's time ran out until the service reports time again.
    /// While set, the streamer cannot resume the companion.</summary>
    public bool QuotaExhausted => _quotaExhausted;
    /// <summary>How long a reply in progress may keep going after the quota ran out.</summary>
    internal TimeSpan QuotaWindDownMax { get; set; } = TimeSpan.FromSeconds(30);
```

2. `CanStartCloudWork`:

```csharp
    private bool CanStartCloudWork =>
        _cloud.IsAllowed && !_companionPaused && Volatile.Read(ref _quotaWindDown) == 0;
```

3. `SetCompanionPaused`: first line `if (!paused && _quotaExhausted) return;`, and right after `_companionPaused = paused;` add `_quota?.SetCompanionActive(!paused);`.

4. `UseCloudAccess`: after `_cloud.Revoked += OnCloudRevoked;` add

```csharp
        if (_quota is not null)
        {
            _quota.QuotaExhausted -= OnQuotaExhausted;
            _quota.QuotaRestored -= OnQuotaRestored;
        }
        _quota = access as ICompanionQuota;
        if (_quota is not null)
        {
            _quota.QuotaExhausted += OnQuotaExhausted;
            _quota.QuotaRestored += OnQuotaRestored;
            _quota.SetCompanionActive(!_companionPaused);
        }
```

5. Handlers (after `SetCompanionPaused`):

```csharp
    private void OnQuotaExhausted()
    {
        if (_companionPaused)
        {
            _quotaExhausted = true;
            Notify(CompanionPausedChanged, nameof(CompanionPausedChanged));
            return;
        }
        if (Interlocked.Exchange(ref _quotaWindDown, 1) == 1) return;
        ReportTurnStatus("今日陪播时长已用完，说完这句后暂停");
        _ = Task.Run(FinishQuotaWindDownAsync);
    }

    private async Task FinishQuotaWindDownAsync()
    {
        // New turns are already blocked (CanStartCloudWork). Let the reply in progress end,
        // but never wait longer than QuotaWindDownMax.
        var deadline = Environment.TickCount64 + (long)QuotaWindDownMax.TotalMilliseconds;
        while (_stateTracker.State is PipelineState.Thinking or PipelineState.Speaking
               && Environment.TickCount64 < deadline)
            await Task.Delay(100).ConfigureAwait(false);

        _quotaExhausted = true;
        SetCompanionPaused(true); // cuts whatever is left after the cap
        Volatile.Write(ref _quotaWindDown, 0);
        Notify(CompanionPausedChanged, nameof(CompanionPausedChanged));
    }

    private void OnQuotaRestored()
    {
        _quotaExhausted = false;
        Notify(CompanionPausedChanged, nameof(CompanionPausedChanged));
    }
```

(Check that `Notify(EventHandler?, string)` is the overload used at line ~187; reuse it exactly as `SetCompanionPaused` does.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter "FullyQualifiedName~RuntimeQuotaTests|FullyQualifiedName~RuntimeCloudGateTests|FullyQualifiedName~RuntimeAsrHealthTests"`
Expected: PASS. If `BotRuntime` needs a different disposal pattern in the tests, mirror `RuntimeAsrHealthTests`.

- [ ] **Step 5: Commit**

```bash
git add AIVTuber.Core/Runtime/BotRuntime.cs AIVTuber.Tests/Auth/RuntimeQuotaTests.cs
git commit -m "Runtime: let the current reply finish, then pause when the daily quota runs out"
```

---

### Task 6: Streamer console state and page

**Files:**
- Modify: `AIVTuber.Core/ViewModels/StreamerConsoleController.cs`
- Modify: `App/WebUi/wwwroot/streamer.html`
- Modify: `App/WebUi/wwwroot/streamer.js`
- Modify: `App/WebUi/wwwroot/streamer.css`
- Test: `AIVTuber.Tests/Distribution/StreamerConsoleTests.cs` (append)

**Interfaces:**
- Consumes: `BotRuntime.Quota`, `BotRuntime.QuotaExhausted`.
- Produces: state JSON `quota: { managed, totalSeconds, remainingSeconds, exhausted, resetsAt }` (`resetsAt` ISO string or `""`); issue `code: "quota_low"` when `0 < remaining ≤ 600`, dismissible via the existing `dismissIssue`.

- [ ] **Step 1: Write the failing tests** (append to `StreamerConsoleTests`; `SignInAsync` sets a reply without quota, so add a helper)

```csharp
    private async Task SignInWithQuotaAsync(int remaining)
    {
        _api.Login = (_, _) => Task.FromResult(new AuthReply(AuthCode.Ok, "tok", "acc", ServerStart,
            ServerStart.AddSeconds(180), ServerStart.AddDays(30), 60,
            new QuotaReply(3600, remaining, ServerStart.AddHours(14))));
        await _account.LoginAsync("pw");
    }

    [Fact]
    public async Task State_CarriesTheDailyQuota()
    {
        await SignInWithQuotaAsync(3000);

        var state = Serialize(_controller.BuildState());

        Assert.Contains("\"quota\":{\"managed\":true", state);
        Assert.Contains("\"totalSeconds\":3600", state);
        Assert.Contains("\"remainingSeconds\":3000", state);
        Assert.Contains("\"exhausted\":false", state);
    }

    [Fact]
    public async Task State_WithoutAQuota_SaysNotManaged()
    {
        await SignInAsync();

        Assert.Contains("\"quota\":{\"managed\":false}", Serialize(_controller.BuildState()));
    }

    [Fact]
    public async Task LowQuota_RaisesADismissibleNotice_AndRefillClearsIt()
    {
        await SignInWithQuotaAsync(500);
        Assert.Contains("quota_low", Serialize(_controller.BuildState()));

        await _controller.HandleAsync("dismissIssue", Json("{}"));
        Assert.DoesNotContain("quota_low", Serialize(_controller.BuildState()));
    }

    [Fact]
    public async Task PlentyOfQuota_RaisesNoNotice()
    {
        await SignInWithQuotaAsync(3000);

        Assert.DoesNotContain("quota_low", Serialize(_controller.BuildState()));
    }

    [Fact]
    public void Page_HasTheQuotaTagAndTheAccountRows()
    {
        var html = File.ReadAllText(Path.Combine(Wwwroot(), "streamer.html"));
        var js = File.ReadAllText(Path.Combine(Wwwroot(), "streamer.js"));

        Assert.Contains("id=\"quotaTag\"", html);
        Assert.Contains("id=\"accQuota\"", html);
        Assert.Contains("今日时长已用完 · 明早 6:00 恢复", js);
        Assert.Contains("今日剩余不到 1 分钟", js);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter FullyQualifiedName~StreamerConsoleTests`
Expected: new tests FAIL (`quota` missing from the state, ids missing).

- [ ] **Step 3: Implement**

`StreamerConsoleController.cs`:

1. Field `private bool _quotaLowDismissed;` next to `_dismissedError`.
2. Subscribe in the constructor, after `_runtime.CompanionPausedChanged += OnRuntimeChangedPlain;`:

```csharp
        if (_runtime.Quota is { } quota) quota.QuotaChanged += OnQuotaChanged;
```

and in `Dispose` before `_preview.Dispose();`: `if (_runtime.Quota is { } quota) quota.QuotaChanged -= OnQuotaChanged;`. Add the handler next to `OnRuntimeChangedPlain`:

```csharp
    private void OnQuotaChanged() => StateInvalidated?.Invoke(this, EventArgs.Empty);
```

3. In `BuildState()`, add `quota = BuildQuota(),` to the anonymous object (next to `account = BuildAccount()`), and:

```csharp
    private object BuildQuota()
    {
        if (_runtime.Quota is not { QuotaManaged: true } quota) return new { managed = false };
        return new
        {
            managed = true,
            totalSeconds = quota.QuotaSeconds,
            remainingSeconds = quota.QuotaRemainingSeconds,
            exhausted = _runtime.QuotaExhausted,
            resetsAt = quota.QuotaResetsAt?.ToString("O") ?? "",
        };
    }
```

4. In `BuildIssues`, before `return issues;`:

```csharp
        if (signedIn && _runtime.Quota is { QuotaManaged: true } q)
        {
            var left = q.QuotaRemainingSeconds;
            if (left > 600) _quotaLowDismissed = false;
            else if (left > 0 && !_quotaLowDismissed)
                issues.Add(new
                {
                    area = ErrorArea.Account, code = "quota_low",
                    message = $"今天的陪播时长只剩约 {Math.Max(1, (left + 59) / 60)} 分钟了，用完后会自动暂停，明早 6:00 恢复。",
                    action = "知道了", diagnosticId = "",
                });
        }
```

5. In `HandleAsync` case `"dismissIssue"` add `_quotaLowDismissed = true;` before `PushState();`.

`streamer.html` — in `.hero-status`, after `<p class="sub" id="activitySub"></p>` add `<span class="quota" id="quotaTag" hidden></span>`; in the account card after the `有效期` row add:

```html
          <p class="kv"><span>今日已用</span><b id="accQuota">—</b></p>
          <p class="kv"><span>刷新时间</span><b>每天早上 6:00</b></p>
```

`streamer.js`:

1. Above `renderState`:

```js
  let quotaTimer = null;
  function renderQuota(q, c) {
    const tag = $("quotaTag");
    clearInterval(quotaTimer);
    if (!q || !q.managed || !c.signedIn) { tag.hidden = true; return; }
    let remaining = q.remainingSeconds;
    const paint = () => {
      const out = q.exhausted || remaining <= 0;
      const minutes = Math.ceil(remaining / 60);
      tag.hidden = false;
      tag.className = "quota" + (out ? " out" : remaining <= 600 ? " low" : "");
      tag.textContent = out ? "今日时长已用完 · 明早 6:00 恢复"
        : remaining < 60 ? "今日剩余不到 1 分钟"
        : remaining <= 600 ? `今日剩余 ${minutes} 分钟，快用完了`
        : `今日剩余 ${minutes} 分钟`;
    };
    paint();
    // Between pushes the page counts down on its own while the companion runs; every push re-syncs it.
    if (c.running && !q.exhausted) quotaTimer = setInterval(() => { remaining = Math.max(0, remaining - 1); paint(); }, 1000);
  }
```

2. In `renderState`, after `$("btnSignIn").hidden = !!c.signedIn;` add:

```js
    const q = s.quota || {};
    renderQuota(q, c);
    if (q.managed && q.exhausted) {
      btn.disabled = true;
      text($("activitySub"), "今天的陪播时长用完了，明早 6:00 恢复。");
    } else {
      btn.disabled = false;
    }
```

3. After `text($("accValid"), a.validUntil || "—");` add:

```js
    const aq = (s.quota || {});
    text($("accQuota"), aq.managed
      ? `${Math.floor((aq.totalSeconds - aq.remainingSeconds) / 60)} 分钟 / 共 ${Math.round(aq.totalSeconds / 60)} 分钟`
      : "—");
```

(`a` and `s` are the names already in scope in that function; if the account block lives in a separate function that only receives `a`, pass `s.quota` into it.)

`streamer.css` (append near the hero rules):

```css
.quota { grid-column: 2; justify-self: start; margin-top: 6px; font-size: 12px; padding: 3px 10px; border-radius: 999px; background: rgba(122, 108, 240, .14); color: #5b3fc4; border: 1px solid rgba(122, 108, 240, .3); }
.quota.low { background: rgba(255, 240, 214, .9); color: var(--warn); border-color: #f0d3a0; }
.quota.out { background: rgba(253, 227, 232, .9); color: var(--bad); border-color: #f4b3c0; }
```

- [ ] **Step 4: Run to verify pass, then check the page**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter FullyQualifiedName~StreamerConsoleTests`
Expected: PASS.

Static preview: `.claude/launch.json` defines `webui-static` (port 8765, serves `App/WebUi/wwwroot`). Open `http://localhost:8765/streamer.html` and confirm the page loads without console errors and `document.getElementById("quotaTag")` exists (it stays hidden because no state arrives outside the app). The four label states and the countdown are checked against real states on Windows in Task 7 step 3.

- [ ] **Step 5: Commit**

```bash
git add AIVTuber.Core/ViewModels/StreamerConsoleController.cs App/WebUi/wwwroot AIVTuber.Tests/Distribution/StreamerConsoleTests.cs
git commit -m "Streamer console: show the daily companion quota and warn when it runs low"
```

---

### Task 7: Documentation and full verification

**Files:**
- Modify: `docs/auth/README.md`

- [ ] **Step 1: Update the README**

In `docs/auth/README.md`:
- In 「它做什么，不做什么」 replace the last bullet (`AUTH-TIME-01` not implemented) with: `每天陪播时长额度（默认 60 分钟，北京时间每天 06:00 刷新）由服务端记账，客户端借心跳上报；账号有效期仍是独立的"有效到某日"。`
- In 「管理账号」's command table add the four rows `set-default-daily`, `set-daily`, `add-today`, `usage` exactly as in the Task 3 usage text, and note that `list` now shows `today_used_min` / `today_quota_min`.
- In 「客户端行为」 add: `陪播进行中才计时；暂停不计。额度用完时，正在进行的这一轮回复说完（最多 30 秒）后自动暂停，账号保持登录，设置与帮助仍可用；额度恢复后需要主播手动点"继续陪播"。`
- In 「状态码」 add a note: 额度用完不是拒绝状态，`status` 仍是 `ok`，`quota_remaining_seconds` 为 0。
- Add a short 「额度协议」 section listing the heartbeat field `active_seconds` and the three reply fields `quota_seconds`, `quota_remaining_seconds`, `quota_resets_at`.

- [ ] **Step 2: Whole-suite verification**

Run (Windows or x64 .NET): `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --verbosity minimal`
Expected: all pass.

Run (any machine): `dotnet build App/App.csproj -c Release -p:EnableWindowsTargeting=true --verbosity minimal`
Expected: 0 errors.

- [ ] **Step 3: Windows acceptance by hand** (record results in the PR description)

With a distribution package pointed at a local `AIVTuber.AuthServer`:
1. `admin set-daily --username <u> --minutes 2` and log in: the home page shows `今日剩余 2 分钟，快用完了` (orange) and a one-time notice.
2. Talk to the AI; let the quota hit zero mid-reply: the sentence finishes, the companion pauses, the tag reads `今日时长已用完 · 明早 6:00 恢复`, 继续陪播 is disabled, settings still work.
3. `admin add-today --username <u> --minutes 5`: within about 60 s the tag refills; 继续陪播 becomes clickable but the companion does not restart on its own.
4. Pause for a minute: the displayed minutes do not drop.
5. Stop the server for 2 minutes and restart it: after reconnect the used time includes the offline period.

- [ ] **Step 4: Commit**

```bash
git add docs/auth/README.md
git commit -m "Docs: daily companion quota protocol, commands and client behaviour"
```
