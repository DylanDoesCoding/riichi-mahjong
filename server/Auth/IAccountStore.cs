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
        long   Id,
        string Username,
        string PasswordHash,
        int    GamesPlayed,
        int    GamesWon,
        long   TotalPoints);

    public interface IAccountStore
    {
        /// <summary>Prepare the backing store (creates the schema if needed).</summary>
        Task InitAsync();

        /// <summary>Create an account. Returns null when the username is already taken
        /// (case-insensitive).</summary>
        Task<AccountRecord?> CreateAsync(string username, string passwordHash);

        Task<AccountRecord?> GetByUsernameAsync(string username);

        /// <summary>Add one finished game to the account's lifetime stats.</summary>
        Task RecordGameResultAsync(long accountId, bool won, int finalPoints);
    }
}
