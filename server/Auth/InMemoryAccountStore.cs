// =============================================================================
// InMemoryAccountStore.cs — non-persistent account store for local testing.
// Activated with DATABASE_URL=memory. Accounts vanish on restart.
// =============================================================================

namespace RiichiServer.Auth
{
    public class InMemoryAccountStore : IAccountStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, AccountRecord> _byUsernameLc = new();
        private long _nextId = 1;

        public Task InitAsync()
        {
            Console.WriteLine("[Auth] WARNING: using in-memory account store — accounts are lost on restart.");
            return Task.CompletedTask;
        }

        public Task<AccountRecord?> CreateAsync(string username, string passwordHash)
        {
            lock (_lock)
            {
                var key = username.ToLowerInvariant();
                if (_byUsernameLc.ContainsKey(key)) return Task.FromResult<AccountRecord?>(null);

                var record = new AccountRecord(_nextId++, username, passwordHash, 0, 0, 0);
                _byUsernameLc[key] = record;
                return Task.FromResult<AccountRecord?>(record);
            }
        }

        public Task<AccountRecord?> GetByUsernameAsync(string username)
        {
            lock (_lock)
            {
                _byUsernameLc.TryGetValue(username.ToLowerInvariant(), out var record);
                return Task.FromResult(record);
            }
        }

        public Task<AccountRecord?> GetByIdAsync(long accountId)
        {
            lock (_lock)
                return Task.FromResult(
                    _byUsernameLc.Values.FirstOrDefault(r => r.Id == accountId));
        }

        public Task RecordGameResultAsync(long accountId, bool won, int finalPoints)
        {
            lock (_lock)
            {
                Mutate(accountId, record => record with
                {
                    GamesPlayed = record.GamesPlayed + 1,
                    GamesWon    = record.GamesWon + (won ? 1 : 0),
                    TotalPoints = record.TotalPoints + finalPoints,
                });
            }
            return Task.CompletedTask;
        }

        // =====================================================================
        // Account management
        // =====================================================================

        private readonly Dictionary<long, ResetCode> _resetCodes = new();

        public Task<int> UpdatePasswordAsync(long accountId, string passwordHash)
        {
            lock (_lock)
            {
                int newVersion = 0;
                Mutate(accountId, r =>
                {
                    newVersion = r.TokenVersion + 1;
                    return r with { PasswordHash = passwordHash, TokenVersion = newVersion };
                });
                return Task.FromResult(newVersion);
            }
        }

        public Task<bool> SetEmailAsync(long accountId, string email)
        {
            lock (_lock)
            {
                bool taken = _byUsernameLc.Values.Any(r =>
                    r.Id != accountId &&
                    string.Equals(r.Email, email, StringComparison.OrdinalIgnoreCase));
                if (taken) return Task.FromResult(false);

                Mutate(accountId, r => r with { Email = email });
                return Task.FromResult(true);
            }
        }

        public Task SaveResetCodeAsync(long accountId, string codeHash, DateTimeOffset expiresAt)
        {
            lock (_lock)
                _resetCodes[accountId] = new ResetCode(codeHash, expiresAt, 0);
            return Task.CompletedTask;
        }

        public Task<ResetCode?> GetResetCodeAsync(long accountId)
        {
            lock (_lock)
            {
                _resetCodes.TryGetValue(accountId, out var code);
                return Task.FromResult(code);
            }
        }

        public Task<int> IncrementResetAttemptsAsync(long accountId)
        {
            lock (_lock)
            {
                if (!_resetCodes.TryGetValue(accountId, out var code)) return Task.FromResult(0);
                var bumped = code with { Attempts = code.Attempts + 1 };
                _resetCodes[accountId] = bumped;
                return Task.FromResult(bumped.Attempts);
            }
        }

        public Task DeleteResetCodeAsync(long accountId)
        {
            lock (_lock)
                _resetCodes.Remove(accountId);
            return Task.CompletedTask;
        }

        /// <summary>Apply a transform to the record with the given id (caller holds _lock).</summary>
        private void Mutate(long accountId, Func<AccountRecord, AccountRecord> transform)
        {
            foreach (var (key, record) in _byUsernameLc)
            {
                if (record.Id != accountId) continue;
                _byUsernameLc[key] = transform(record);
                break;
            }
        }
    }
}
