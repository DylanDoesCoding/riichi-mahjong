// =============================================================================
// MatchmakingQueue.cs
// Thread-safe queue that collects players for automatic matchmaking.
//
// Behaviour:
//   • When 4 players are present the queue matches them immediately.
//   • On the first enqueue a 30-second fill timer is started.  If the timer
//     fires before 4 humans are found, whatever players are in the queue are
//     matched and remaining seats are filled with CPU bots by GameRoom.
//   • A matched player's GameRoom is placed in `pendingMatches` so that their
//     per-connection receive loop in Program.cs can pick it up and route game
//     actions correctly (the receive loop is blocked on ReceiveAsync, not on
//     any matchmaking await).
// =============================================================================

using System.Collections.Concurrent;

namespace RiichiServer
{
    public class MatchmakingQueue
    {
        // ---- Queue entry -------------------------------------------------------
        public record QueueEntry(PlayerConnection Conn, string DisplayName, string Uuid);

        // ---- State -------------------------------------------------------------
        private readonly List<QueueEntry>                     _queue   = new();
        private readonly object                               _lock    = new();
        private          CancellationTokenSource?             _timerCts;

        // ---- Dependencies ------------------------------------------------------
        private readonly RoomManager                          _rooms;
        private readonly ConcurrentDictionary<string, GameRoom> _pending;

        private const int TimeoutSeconds = 30;

        // =====================================================================
        public MatchmakingQueue(
            RoomManager rooms,
            ConcurrentDictionary<string, GameRoom> pendingMatches)
        {
            _rooms   = rooms;
            _pending = pendingMatches;
        }

        // =====================================================================
        // Public API
        // =====================================================================

        /// <summary>
        /// Add a player to the queue.  Immediately fires a match if 4 players
        /// are present; starts the fill timer on the first enqueue.
        /// Returns false if the player is already in the queue.
        /// </summary>
        public async Task<bool> JoinAsync(
            PlayerConnection conn, string displayName, string uuid)
        {
            List<QueueEntry>? toMatch    = null;
            bool              startTimer = false;

            lock (_lock)
            {
                if (_queue.Any(q => q.Conn == conn)) return false;

                _queue.Add(new QueueEntry(conn, displayName, uuid));

                if (_queue.Count == 4)
                {
                    // Full house — match straight away
                    toMatch = new List<QueueEntry>(_queue);
                    _queue.Clear();
                    _timerCts?.Cancel();
                    _timerCts = null;
                }
                else if (_queue.Count == 1)
                {
                    // First player in — start the fill-with-CPU countdown
                    startTimer = true;
                    _timerCts  = new CancellationTokenSource();
                }
            }

            if (toMatch != null)
            {
                await MatchAsync(toMatch);
            }
            else if (startTimer)
            {
                var cts = _timerCts!;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(TimeoutSeconds), cts.Token);
                        await FireTimerAsync();
                    }
                    catch (OperationCanceledException) { /* cancelled — queue was cleared */ }
                });
            }

            return true;
        }

        /// <summary>
        /// Remove a player from the queue (called on disconnect or leaveQueue).
        /// If the queue becomes empty, the fill timer is cancelled.
        /// </summary>
        public void Leave(PlayerConnection conn)
        {
            lock (_lock)
            {
                _queue.RemoveAll(q => q.Conn == conn);
                if (_queue.Count == 0)
                {
                    _timerCts?.Cancel();
                    _timerCts = null;
                }
            }
        }

        /// <summary>Current number of players waiting in the queue.</summary>
        public int Count
        {
            get { lock (_lock) return _queue.Count; }
        }

        // =====================================================================
        // Private helpers
        // =====================================================================

        /// <summary>Called by the fill timer when it expires.</summary>
        private async Task FireTimerAsync()
        {
            List<QueueEntry>? players;
            lock (_lock)
            {
                if (_queue.Count == 0) return;
                players   = new List<QueueEntry>(_queue);
                _queue.Clear();
                _timerCts = null;
            }
            await MatchAsync(players);
        }

        /// <summary>
        /// Create a room, seat all queued players, fill remaining seats with CPU
        /// (GameRoom handles this automatically), and start the game.
        /// Each matched player's room is registered in the pendingMatches dict
        /// so Program.cs can route their subsequent game messages correctly.
        /// </summary>
        private async Task MatchAsync(List<QueueEntry> players)
        {
            var room = _rooms.CreateRoom();

            foreach (var p in players)
            {
                p.Conn.DisplayName = p.DisplayName;
                p.Conn.PlayerUuid  = p.Uuid;
                room.AddPlayer(p.Conn);
            }

            // Publish the room so each player's receive loop can claim it
            foreach (var p in players)
                _pending[p.Conn.PlayerId] = room;

            // StartGameAsync sends gameStarted (with Code) + handDealt to everyone
            await room.StartGameAsync();
        }
    }
}
