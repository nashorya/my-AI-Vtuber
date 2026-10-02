using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AIVTuber.AuthServer;

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

public sealed record InviteRow(
    string Code,
    string ProfileId,
    string Note,
    DateTimeOffset CreatedAt,
    string? UsedByAccountId,
    string? UsedByUsername,
    DateTimeOffset? UsedAt,
    DateTimeOffset? RevokedAt);

public enum RegisterOutcome { Ok, InvalidInvite, UsernameTaken }

/// <summary>SQLite persistence for accounts and sessions. Only password hashes and token
/// digests are stored.</summary>
public sealed class AuthStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _sync = new();

    private AuthStore(SqliteConnection db) => _db = db;

    public static AuthStore Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS accounts (
                id TEXT PRIMARY KEY,
                username TEXT NOT NULL UNIQUE COLLATE NOCASE,
                password_hash TEXT NOT NULL,
                enabled INTEGER NOT NULL,
                valid_until TEXT NOT NULL,
                profile_id TEXT NOT NULL,
                min_credential_revision INTEGER NOT NULL DEFAULT 0,
                note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sessions (
                token_hash TEXT PRIMARY KEY,
                account_id TEXT NOT NULL REFERENCES accounts(id),
                profile_id TEXT NOT NULL,
                app_version TEXT NOT NULL,
                created_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                revoked_at TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sessions_account ON sessions(account_id);
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS invites (
                code TEXT PRIMARY KEY,
                profile_id TEXT NOT NULL,
                note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                used_by_account_id TEXT NULL,
                used_at TEXT NULL,
                revoked_at TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS usage (
                account_id TEXT NOT NULL REFERENCES accounts(id),
                quota_day TEXT NOT NULL,
                used_seconds INTEGER NOT NULL,
                PRIMARY KEY (account_id, quota_day)
            );
            """;
        cmd.ExecuteNonQuery();
        AddColumnIfMissing(db, "accounts", "daily_quota_seconds", "INTEGER NULL");
        AddColumnIfMissing(db, "accounts", "bonus_seconds", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "accounts", "bonus_day", "TEXT NULL");
        AddColumnIfMissing(db, "sessions", "reported_active_seconds", "INTEGER NOT NULL DEFAULT 0");
        return new AuthStore(db);
    }

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

    public void InsertAccount(AccountRow row, DateTimeOffset now)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO accounts (id, username, password_hash, enabled, valid_until, profile_id,
                                      min_credential_revision, note, created_at)
                VALUES ($id, $u, $h, $e, $v, $p, $r, $n, $c)
                """;
            cmd.Parameters.AddWithValue("$id", row.Id);
            cmd.Parameters.AddWithValue("$u", row.Username);
            cmd.Parameters.AddWithValue("$h", row.PasswordHash);
            cmd.Parameters.AddWithValue("$e", row.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$v", Format(row.ValidUntil));
            cmd.Parameters.AddWithValue("$p", row.ProfileId);
            cmd.Parameters.AddWithValue("$r", row.MinCredentialRevision);
            cmd.Parameters.AddWithValue("$n", row.Note);
            cmd.Parameters.AddWithValue("$c", Format(now));
            try { cmd.ExecuteNonQuery(); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException($"账号 {row.Username} 已存在。", ex);
            }
        }
    }

    public AccountRow? FindAccountByUsername(string username) => QueryAccount("username = $k", username);

    public AccountRow? FindAccountById(string id) => QueryAccount("id = $k", id);

    public IReadOnlyList<AccountRow> ListAccounts()
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {AccountColumns} FROM accounts ORDER BY username";
            using var reader = cmd.ExecuteReader();
            var rows = new List<AccountRow>();
            while (reader.Read()) rows.Add(ReadAccount(reader));
            return rows;
        }
    }

    /// <summary>Updates one account column. Returns false when the username is unknown.</summary>
    public bool UpdateAccount(string username, string column, object value)
    {
        if (column is not ("password_hash" or "enabled" or "valid_until" or "min_credential_revision" or "note" or "profile_id"
            or "daily_quota_seconds" or "bonus_seconds" or "bonus_day"))
            throw new ArgumentOutOfRangeException(nameof(column));
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"UPDATE accounts SET {column} = $v WHERE username = $u";
            cmd.Parameters.AddWithValue("$v", value switch
            {
                null => DBNull.Value,
                DateTimeOffset t => Format(t),
                _ => value,
            });
            cmd.Parameters.AddWithValue("$u", username);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public void InsertSession(string tokenHash, string accountId, string profileId, string appVersion, DateTimeOffset now)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (token_hash, account_id, profile_id, app_version, created_at, last_seen_at)
                VALUES ($t, $a, $p, $v, $n, $n)
                """;
            cmd.Parameters.AddWithValue("$t", tokenHash);
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$p", profileId);
            cmd.Parameters.AddWithValue("$v", appVersion);
            cmd.Parameters.AddWithValue("$n", Format(now));
            cmd.ExecuteNonQuery();
        }
    }

    public SessionRow? FindSession(string tokenHash)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT token_hash, account_id, profile_id, created_at, revoked_at, last_seen_at, reported_active_seconds FROM sessions WHERE token_hash = $t";
            cmd.Parameters.AddWithValue("$t", tokenHash);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new SessionRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : Parse(reader.GetString(4)),
                Parse(reader.GetString(5)), reader.GetInt64(6));
        }
    }

    public void TouchSession(string tokenHash, DateTimeOffset now)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET last_seen_at = $n WHERE token_hash = $t";
            cmd.Parameters.AddWithValue("$n", Format(now));
            cmd.Parameters.AddWithValue("$t", tokenHash);
            cmd.ExecuteNonQuery();
        }
    }

    public void RevokeSession(string tokenHash, DateTimeOffset now)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET revoked_at = $n WHERE token_hash = $t AND revoked_at IS NULL";
            cmd.Parameters.AddWithValue("$n", Format(now));
            cmd.Parameters.AddWithValue("$t", tokenHash);
            cmd.ExecuteNonQuery();
        }
    }

    public void RevokeAccountSessions(string accountId, DateTimeOffset now)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET revoked_at = $n WHERE account_id = $a AND revoked_at IS NULL";
            cmd.Parameters.AddWithValue("$n", Format(now));
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.ExecuteNonQuery();
        }
    }

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

    /// <summary>Stores one invite; false when the code already exists.</summary>
    public bool InsertInvite(string code, string profileId, string note, DateTimeOffset now)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO invites (code, profile_id, note, created_at) VALUES ($c, $p, $n, $t)";
            cmd.Parameters.AddWithValue("$c", code);
            cmd.Parameters.AddWithValue("$p", profileId);
            cmd.Parameters.AddWithValue("$n", note);
            cmd.Parameters.AddWithValue("$t", Format(now));
            try { cmd.ExecuteNonQuery(); return true; }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return false; }
        }
    }

    /// <summary>One transaction: the invite must be unused, unrevoked and for this account's profile;
    /// the account is inserted and the invite consumed together. A username clash rolls everything
    /// back, so the invite stays usable.</summary>
    public RegisterOutcome RegisterWithInvite(string code, AccountRow account, DateTimeOffset now)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            using (var check = _db.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT profile_id, used_by_account_id, revoked_at FROM invites WHERE code = $c";
                check.Parameters.AddWithValue("$c", code);
                using var reader = check.ExecuteReader();
                if (!reader.Read() || !reader.IsDBNull(1) || !reader.IsDBNull(2) ||
                    !string.Equals(reader.GetString(0), account.ProfileId, StringComparison.Ordinal))
                {
                    reader.Close();
                    tx.Rollback();
                    return RegisterOutcome.InvalidInvite;
                }
            }
            using (var insert = _db.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO accounts (id, username, password_hash, enabled, valid_until, profile_id,
                                          min_credential_revision, note, created_at)
                    VALUES ($id, $u, $h, 1, $v, $p, 0, $n, $c)
                    """;
                insert.Parameters.AddWithValue("$id", account.Id);
                insert.Parameters.AddWithValue("$u", account.Username);
                insert.Parameters.AddWithValue("$h", account.PasswordHash);
                insert.Parameters.AddWithValue("$v", Format(account.ValidUntil));
                insert.Parameters.AddWithValue("$p", account.ProfileId);
                insert.Parameters.AddWithValue("$n", account.Note);
                insert.Parameters.AddWithValue("$c", Format(now));
                try { insert.ExecuteNonQuery(); }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    tx.Rollback();
                    return RegisterOutcome.UsernameTaken;
                }
            }
            using (var consume = _db.CreateCommand())
            {
                consume.Transaction = tx;
                consume.CommandText = """
                    UPDATE invites SET used_by_account_id = $a, used_at = $n
                    WHERE code = $c AND used_by_account_id IS NULL AND revoked_at IS NULL
                    """;
                consume.Parameters.AddWithValue("$a", account.Id);
                consume.Parameters.AddWithValue("$n", Format(now));
                consume.Parameters.AddWithValue("$c", code);
                if (consume.ExecuteNonQuery() != 1)
                {
                    tx.Rollback();
                    return RegisterOutcome.InvalidInvite;
                }
            }
            tx.Commit();
            return RegisterOutcome.Ok;
        }
    }

    /// <summary>Revokes an invite that is still unused; false when it is unknown, used or already revoked.</summary>
    public bool RevokeInvite(string code, DateTimeOffset now)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE invites SET revoked_at = $n WHERE code = $c AND used_by_account_id IS NULL AND revoked_at IS NULL";
            cmd.Parameters.AddWithValue("$n", Format(now));
            cmd.Parameters.AddWithValue("$c", code);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public IReadOnlyList<InviteRow> ListInvites()
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT i.code, i.profile_id, i.note, i.created_at, i.used_by_account_id, a.username, i.used_at, i.revoked_at
                FROM invites i LEFT JOIN accounts a ON a.id = i.used_by_account_id
                ORDER BY i.created_at, i.code
                """;
            using var reader = cmd.ExecuteReader();
            var rows = new List<InviteRow>();
            while (reader.Read())
                rows.Add(new InviteRow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), Parse(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : Parse(reader.GetString(7))));
            return rows;
        }
    }

    public void Dispose() => _db.Dispose();

    private const string AccountColumns =
        "id, username, password_hash, enabled, valid_until, profile_id, min_credential_revision, note, " +
        "daily_quota_seconds, bonus_seconds, bonus_day";

    private AccountRow? QueryAccount(string where, string key)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {AccountColumns} FROM accounts WHERE {where}";
            cmd.Parameters.AddWithValue("$k", key);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadAccount(reader) : null;
        }
    }

    private static AccountRow ReadAccount(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0,
        Parse(r.GetString(4)), r.GetString(5), r.GetInt32(6), r.GetString(7),
        r.IsDBNull(8) ? null : r.GetInt32(8), r.GetInt32(9), r.IsDBNull(10) ? null : r.GetString(10));

    private static string Format(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
