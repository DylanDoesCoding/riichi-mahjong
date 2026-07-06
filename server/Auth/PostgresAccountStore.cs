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
                SELECT id, username, password_hash, games_played, games_won, total_points
                FROM accounts WHERE username_lc = lower(@u);
                """;
            await using var cmd = _db.CreateCommand(sql);
            cmd.Parameters.AddWithValue("u", username);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            return new AccountRecord(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt64(5));
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
    }
}
