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

        public Task RecordGameResultAsync(long accountId, bool won, int finalPoints)
        {
            lock (_lock)
            {
                foreach (var (key, record) in _byUsernameLc)
                {
                    if (record.Id != accountId) continue;
                    _byUsernameLc[key] = record with
                    {
                        GamesPlayed = record.GamesPlayed + 1,
                        GamesWon    = record.GamesWon + (won ? 1 : 0),
                        TotalPoints = record.TotalPoints + finalPoints,
                    };
                    break;
                }
            }
            return Task.CompletedTask;
        }
    }
}
