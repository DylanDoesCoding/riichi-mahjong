// =============================================================================
// PostgresAccountStore.cs — account storage on Postgres (Supabase/Neon/etc.).
//
// Accepts DATABASE_URL in either form:
//   postgres://user:pass@host:port/dbname?sslmode=require   (URI, Render/Supabase style)
//   Host=...;Username=...;Password=...;Database=...          (Npgsql keyword string)
// SSL defaults to Require for URI form since every hosted Postgres needs it.
// =============================================================================

using Npgsql;

namespace RiichiServer.Auth
{
    public class PostgresAccountStore : IAccountStore
    {
        private readonly NpgsqlDataSource _db;

        public PostgresAccountStore(string databaseUrl)
        {
            _db = NpgsqlDataSource.Create(ToConnectionString(databaseUrl));
        }

        public static string ToConnectionString(string url)
        {
            if (!url.Contains("://")) return url;   // already keyword format

            var uri      = new Uri(url);
            var userInfo = uri.UserInfo.Split(':', 2);
            var b = new NpgsqlConnectionStringBuilder
            {
                Host     = uri.Host,
                Port     = uri.Port > 0 ? uri.Port : 5432,
                Username = Uri.UnescapeDataString(userInfo[0]),
                Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
                Database = uri.AbsolutePath.TrimStart('/'),
                SslMode  = SslMode.Require,
            };

            foreach (var kv in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = kv.Split('=', 2);
                if (p.Length == 2 && p[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase)
                    && Enum.TryParse<SslMode>(p[1].Replace("-", ""), true, out var mode))
                    b.SslMode = mode;
            }

            return b.ConnectionString;
        }

        public async Task InitAsync()
        {
            // email: optional, for password recovery. steam_id: reserved for a
            // future Steam build (session-ticket auth keys accounts by SteamID).
            const string sql = """
                CREATE TABLE IF NOT EXISTS accounts (
                    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    username      TEXT        NOT NULL,
                    username_lc   TEXT        NOT NULL UNIQUE,
                    password_hash TEXT        NOT NULL,
                    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
                    games_played  INT         NOT NULL DEFAULT 0,
                    games_won     INT         NOT NULL DEFAULT 0,
                    total_points  BIGINT      NOT NULL DEFAULT 0
                );
                ALTER TABLE accounts ADD COLUMN IF NOT EXISTS email         TEXT;
                ALTER TABLE accounts ADD COLUMN IF NOT EXISTS email_lc      TEXT;
                ALTER TABLE accounts ADD COLUMN IF NOT EXISTS steam_id      TEXT;
                ALTER TABLE accounts ADD COLUMN IF NOT EXISTS token_version INT NOT NULL DEFAULT 0;
                CREATE UNIQUE INDEX IF NOT EXISTS accounts_email_lc_idx
                    ON accounts (email_lc) WHERE email_lc IS NOT NULL;
                CREATE UNIQUE INDEX IF NOT EXISTS accounts_steam_id_idx
                    ON accounts (steam_id) WHERE steam_id IS NOT NULL;
                CREATE TABLE IF NOT EXISTS password_resets (
                    account_id BIGINT PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
                    code_hash  TEXT        NOT NULL,
                    expires_at TIMESTAMPTZ NOT NULL,
                    attempts   INT         NOT NULL DEFAULT 0
                );
                """;
            await using var cmd = _db.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<AccountRecord?> CreateAsync(string username, string passwordHash)
        {
            const string sql = """
                INSERT INTO accounts (username, username_lc, password_hash)
                VALUES (@u, lower(@u), @h)
                ON CONFLICT (username_lc) DO NOTHING
                RETURNING id;
                """;
            await using var cmd = _db.CreateCommand(sql);
            cmd.Parameters.AddWithValue("u", username);
            cmd.Parameters.AddWithValue("h", passwordHash);

            var id = await cmd.ExecuteScalarAsync();
            if (id == null) return null;   // username taken

            return new AccountRecord((long)id, username, passwordHash, 0, 0, 0);
        }

        public async Task<AccountRecord?> GetByUsernameAsync(string username)
        {
            const string sql = """
                SELECT id, username, password_hash, games_played, games_won, total_points, email, token_version
                FROM accounts WHERE username_lc = lower(@u);
                """;
            await using var cmd = _db.CreateCommand(sql);
            cmd.Parameters.AddWithValue("u", username);
            return await ReadAccountAsync(cmd);
        }

        public async Task<AccountRecord?> GetByIdAsync(long accountId)
        {
            const string sql = """
                SELECT id, username, password_hash, games_played, games_won, total_points, email, token_version
                FROM accounts WHERE id = @id;
                """;
            await using var cmd = _db.CreateCommand(sql);
            cmd.Parameters.AddWithValue("id", accountId);
            return await ReadAccountAsync(cmd);
        }

        private static async Task<AccountRecord?> ReadAccountAsync(NpgsqlCommand cmd)
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            return new AccountRecord(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7));
        }

        public async Task RecordGameResultAsync(long accountId, bool won, int finalPoints)
        {
            const string sql = """
                UPDATE accounts SET
                    games_played = games_played + 1,
                    games_won    = games_won + @w,
                    total_points = total_points + @p
                WHERE id = @id;
                """;
            await using var cmd = _db.CreateCommand(sql);
            cmd.Parameters.AddWithValue("w", won ? 1 : 0);
            cmd.Parameters.AddWithValue("p", (long)finalPoints);
            cmd.Parameters.AddWithValue("id", accountId);
            await cmd.ExecuteNonQueryAsync();
        }

        // =====================================================================
        // Account management
        // =====================================================================

        public async Task<int> UpdatePasswordAsync(long accountId, string passwordHash)
        {
            // Bumping token_version revokes every previously issued session token.
            await using var cmd = _db.CreateCommand("""
                UPDATE accounts SET password_hash = @h, token_version = token_version + 1
                WHERE id = @id RETURNING token_version;
                """);
            cmd.Parameters.AddWithValue("h", passwordHash);
            cmd.Parameters.AddWithValue("id", accountId);
            var result = await cmd.ExecuteScalarAsync();
            return result is int v ? v : 0;
        }

        public async Task<bool> SetEmailAsync(long accountId, string email)
        {
            // The partial unique index on email_lc rejects duplicates atomically.
            const string sql = """
                UPDATE accounts SET email = @e, email_lc = lower(@e)
                WHERE id = @id
                  AND NOT EXISTS (
                      SELECT 1 FROM accounts WHERE email_lc = lower(@e) AND id <> @id);
                """;
            await using var cmd = _db.CreateCommand(sql);
            cmd.Parameters.AddWithValue("e", email);
            cmd.Parameters.AddWithValue("id", accountId);
            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        public async Task SaveResetCodeAsync(long accountId, string codeHash, DateTimeOffset expiresAt)
        {
            const string sql = """
                INSERT INTO password_resets (account_id, code_hash, expires_at, attempts)
                VALUES (@id, @h, @exp, 0)
                ON CONFLICT (account_id) DO UPDATE
                    SET code_hash = @h, expires_at = @exp, attempts = 0;
                """;
            await using var cmd = _db.CreateCommand(sql);
            cmd.Parameters.AddWithValue("id", accountId);
            cmd.Parameters.AddWithValue("h", codeHash);
            cmd.Parameters.AddWithValue("exp", expiresAt);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<ResetCode?> GetResetCodeAsync(long accountId)
        {
            await using var cmd = _db.CreateCommand(
                "SELECT code_hash, expires_at, attempts FROM password_resets WHERE account_id = @id;");
            cmd.Parameters.AddWithValue("id", accountId);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            return new ResetCode(
                reader.GetString(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetInt32(2));
        }

        public async Task<int> IncrementResetAttemptsAsync(long accountId)
        {
            await using var cmd = _db.CreateCommand(
                "UPDATE password_resets SET attempts = attempts + 1 WHERE account_id = @id RETURNING attempts;");
            cmd.Parameters.AddWithValue("id", accountId);
            var result = await cmd.ExecuteScalarAsync();
            return result is int n ? n : 0;
        }

        public async Task DeleteResetCodeAsync(long accountId)
        {
            await using var cmd = _db.CreateCommand(
                "DELETE FROM password_resets WHERE account_id = @id;");
            cmd.Parameters.AddWithValue("id", accountId);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
