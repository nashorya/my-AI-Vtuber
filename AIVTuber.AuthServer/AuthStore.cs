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
    string Note);

public sealed record SessionRow(
    string TokenHash,
    string AccountId,
    string ProfileId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt);

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
            """;
        cmd.ExecuteNonQuery();
        return new AuthStore(db);
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
        if (column is not ("password_hash" or "enabled" or "valid_until" or "min_credential_revision" or "note" or "profile_id"))
            throw new ArgumentOutOfRangeException(nameof(column));
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"UPDATE accounts SET {column} = $v WHERE username = $u";
            cmd.Parameters.AddWithValue("$v", value is DateTimeOffset t ? Format(t) : value);
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
            cmd.CommandText = "SELECT token_hash, account_id, profile_id, created_at, revoked_at FROM sessions WHERE token_hash = $t";
            cmd.Parameters.AddWithValue("$t", tokenHash);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new SessionRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : Parse(reader.GetString(4)));
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

    public void Dispose() => _db.Dispose();

    private const string AccountColumns =
        "id, username, password_hash, enabled, valid_until, profile_id, min_credential_revision, note";

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
        Parse(r.GetString(4)), r.GetString(5), r.GetInt32(6), r.GetString(7));

    private static string Format(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
