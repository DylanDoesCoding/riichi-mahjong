// =============================================================================
// IAccountStore.cs — persistent account storage abstraction.
//
// PostgresAccountStore is the production implementation (DATABASE_URL).
// InMemoryAccountStore backs local testing (DATABASE_URL=memory).
// When no store is configured the server runs guest-only, exactly as before.
// =============================================================================

namespace RiichiServer.Auth
{
    public record AccountRecord(
        long    Id,
        string  Username,
        string  PasswordHash,
        int     GamesPlayed,
        int     GamesWon,
        long    TotalPoints,
        string? Email = null,
        int     TokenVersion = 0);

    /// <summary>Pending password-reset code (hash at rest, bounded attempts).</summary>
    public record ResetCode(string CodeHash, DateTimeOffset ExpiresAt, int Attempts);

    /// <summary>One row of the public leaderboard.</summary>
    public record LeaderboardEntry(string Username, int GamesPlayed, int GamesWon, long TotalPoints);

    public interface IAccountStore
    {
        /// <summary>Prepare the backing store (creates the schema if needed).</summary>
        Task InitAsync();

        /// <summary>Lightweight round-trip that proves the database is reachable,
        /// without reading or writing any account data. Used by the keep-alive
        /// health check to generate the activity a free-tier Postgres needs to
        /// avoid auto-pausing. Throws if the store is unreachable.</summary>
        Task PingAsync();

        /// <summary>Create an account. Returns null when the username is already taken
        /// (case-insensitive).</summary>
        Task<AccountRecord?> CreateAsync(string username, string passwordHash);

        Task<AccountRecord?> GetByUsernameAsync(string username);

        Task<AccountRecord?> GetByIdAsync(long accountId);

        /// <summary>Add one finished game to the account's lifetime stats.</summary>
        Task RecordGameResultAsync(long accountId, bool won, int finalPoints);

        /// <summary>Top accounts that have finished at least one game, ordered by
        /// wins (desc) then total points (desc).</summary>
        Task<List<LeaderboardEntry>> GetTopAsync(int count);

        // ---- Account management ---------------------------------------------

        /// <summary>Set a new password hash and bump the token version (revoking
        /// all previously issued session tokens). Returns the new token version.</summary>
        Task<int> UpdatePasswordAsync(long accountId, string passwordHash);

        /// <summary>Attach or replace the account's email. Returns false when the
        /// address is already used by a different account (case-insensitive).</summary>
        Task<bool> SetEmailAsync(long accountId, string email);

        /// <summary>Store (or overwrite) the pending reset code for an account.</summary>
        Task SaveResetCodeAsync(long accountId, string codeHash, DateTimeOffset expiresAt);

        Task<ResetCode?> GetResetCodeAsync(long accountId);

        /// <summary>Bump the failed-attempt counter; returns the new count.</summary>
        Task<int> IncrementResetAttemptsAsync(long accountId);

        Task DeleteResetCodeAsync(long accountId);

        // ---- Cosmetics -------------------------------------------------------

        /// <summary>
        /// The account's stored cosmetic set in wire form, or null if they have never
        /// chosen one. Guests never reach here - their set lives in settings.cfg.
        /// </summary>
        Task<string?> GetCosmeticsAsync(long accountId);

        /// <summary>Store (or replace) the account's cosmetic set.</summary>
        Task SetCosmeticsAsync(long accountId, string cosmetics);
    }
}
