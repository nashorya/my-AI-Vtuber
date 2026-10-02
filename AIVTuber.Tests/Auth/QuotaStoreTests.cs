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
