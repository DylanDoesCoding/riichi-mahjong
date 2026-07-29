// =============================================================================
// ResilientAccountStore.cs
// Decorator that makes an IAccountStore survive a database outage.
//
// Why this exists: the account store used to be connected once at startup, and
// if that failed (e.g. a free-tier Postgres paused for inactivity) accounts
// stayed disabled for the entire life of the process — recovering needed a
// manual server restart even after the database came back.
//
// This wrapper instead treats "connected" as a state that can be lost and
// regained:
//   • Schema init is retried lazily, on the next account operation.
//   • Failed attempts are rate-limited by RetryCooldown so a down database
//     isn't hammered once per message.
//   • If an operation fails after a successful init (database went away
//     mid-life), the store is marked unhealthy so the next call re-inits.
//
// Callers see AccountStoreUnavailableException while the store is down. Game
// flow must never break because of it — see ApplyIdentityAsync in Program.cs,
// which degrades a token-bearing player to guest rather than refusing play.
// =============================================================================

namespace RiichiServer.Auth
{
    /// <summary>Thrown when the account store is configured but not reachable right now.</summary>
    public class AccountStoreUnavailableException : Exception
    {
        public AccountStoreUnavailableException(string message, Exception? inner = null)
            : base(message, inner) { }
    }

    public class ResilientAccountStore : IAccountStore
    {
        /// <summary>Minimum gap between connection attempts while unhealthy.</summary>
        public static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(15);

        private readonly IAccountStore  _inner;
        private readonly SemaphoreSlim  _initLock = new(1, 1);
        private readonly TimeSpan       _cooldown;
        private readonly Func<DateTimeOffset> _now;

        private bool            _ready;
        private DateTimeOffset? _lastAttempt;
        private string          _lastError = "not connected yet";

        /// <summary>True when the store is currently believed to be usable.</summary>
        public bool IsReady { get { lock (_initLock) return _ready; } }

        public ResilientAccountStore(
            IAccountStore inner,
            TimeSpan? retryCooldown = null,
            Func<DateTimeOffset>? nowProvider = null)
        {
            _inner    = inner;
            _cooldown = retryCooldown ?? RetryCooldown;
            _now      = nowProvider ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Try to connect. Never throws — a failure here just leaves the store
        /// unhealthy so it retries on first use. Call at startup.
        /// </summary>
        public async Task InitAsync()
        {
            try
            {
                await EnsureReadyAsync();
                Console.WriteLine("[Auth] Account store ready.");
            }
            catch (AccountStoreUnavailableException ex)
            {
                Console.WriteLine(
                    $"[Auth] Account store unreachable at startup — will retry automatically " +
                    $"on demand (every {_cooldown.TotalSeconds:N0}s). {ex.Message}");
            }
        }

        // =====================================================================
        // Health management
        // =====================================================================

        /// <summary>
        /// Ensure the inner store's schema is initialised, retrying at most once
        /// per cooldown window. Throws AccountStoreUnavailableException if down.
        /// </summary>
        private async Task EnsureReadyAsync()
        {
            if (IsReady) return;

            await _initLock.WaitAsync();
            try
            {
                if (_ready) return;

                // Rate-limit attempts so a dead database isn't dialled on every message
                if (_lastAttempt != null && _now() - _lastAttempt < _cooldown)
                    throw new AccountStoreUnavailableException(_lastError);

                _lastAttempt = _now();
                try
                {
                    await _inner.InitAsync();
                    _ready     = true;
                    _lastError = "";
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    throw new AccountStoreUnavailableException(ex.Message, ex);
                }
            }
            finally { _initLock.Release(); }
        }

        /// <summary>
        /// Run an operation, re-initialising first if needed. If the operation
        /// itself fails the store is marked unhealthy so the next call reconnects
        /// (covers the database disappearing after a successful init).
        /// </summary>
        private async Task<T> RunAsync<T>(Func<Task<T>> operation)
        {
            await EnsureReadyAsync();
            try
            {
                return await operation();
            }
            catch (Exception ex)
            {
                MarkUnhealthy(ex);
                throw new AccountStoreUnavailableException(ex.Message, ex);
            }
        }

        private async Task RunAsync(Func<Task> operation)
            => await RunAsync<bool>(async () => { await operation(); return true; });

        private void MarkUnhealthy(Exception ex)
        {
            lock (_initLock)
            {
                _ready     = false;
                _lastError = ex.Message;
                // Allow an immediate reconnect attempt on the next call: the
                // failure itself is fresh evidence, not a dial we should delay.
                _lastAttempt = null;
            }
            Console.WriteLine($"[Auth] Account operation failed — will reconnect on next use. {ex.Message}");
        }

        // =====================================================================
        // IAccountStore — every call goes through RunAsync
        // =====================================================================

        public Task<AccountRecord?> CreateAsync(string username, string passwordHash)
            => RunAsync(() => _inner.CreateAsync(username, passwordHash));

        public Task<AccountRecord?> GetByUsernameAsync(string username)
            => RunAsync(() => _inner.GetByUsernameAsync(username));

        public Task<AccountRecord?> GetByIdAsync(long accountId)
            => RunAsync(() => _inner.GetByIdAsync(accountId));

        public Task RecordGameResultAsync(long accountId, bool won, int finalPoints)
            => RunAsync(() => _inner.RecordGameResultAsync(accountId, won, finalPoints));

        public Task<List<LeaderboardEntry>> GetTopAsync(int count)
            => RunAsync(() => _inner.GetTopAsync(count));

        public Task<int> UpdatePasswordAsync(long accountId, string passwordHash)
            => RunAsync(() => _inner.UpdatePasswordAsync(accountId, passwordHash));

        public Task<bool> SetEmailAsync(long accountId, string email)
            => RunAsync(() => _inner.SetEmailAsync(accountId, email));

        public Task SaveResetCodeAsync(long accountId, string codeHash, DateTimeOffset expiresAt)
            => RunAsync(() => _inner.SaveResetCodeAsync(accountId, codeHash, expiresAt));

        public Task<ResetCode?> GetResetCodeAsync(long accountId)
            => RunAsync(() => _inner.GetResetCodeAsync(accountId));

        public Task<int> IncrementResetAttemptsAsync(long accountId)
            => RunAsync(() => _inner.IncrementResetAttemptsAsync(accountId));

        public Task DeleteResetCodeAsync(long accountId)
            => RunAsync(() => _inner.DeleteResetCodeAsync(accountId));
    }
}
