// =============================================================================
// RoomManager.cs
// Creates, finds, and cleans up game rooms.  Thread-safe via lock.
// =============================================================================

namespace RiichiServer
{
    public class RoomManager
    {
        private readonly Dictionary<string, GameRoom> _rooms = new();
        private readonly object _lock = new();
        private readonly Random _rng  = new();
        private readonly Auth.IAccountStore? _accounts;   // stats recording; null = guest-only

        public RoomManager(Auth.IAccountStore? accounts = null)
        {
            _accounts = accounts;
        }

        // Characters used for room codes — no O/0/I/1 to avoid confusion
        private const string CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        // Hard cap on concurrent rooms so a client opening many sockets
        // cannot exhaust server memory by spamming createRoom.
        private const int MaxRooms = 200;

        /// <summary>Create a new room, or null if the server is at capacity.</summary>
        public GameRoom? CreateRoom()
        {
            lock (_lock)
            {
                if (_rooms.Count >= MaxRooms) return null;

                string code;
                do { code = GenerateCode(); } while (_rooms.ContainsKey(code));

                var room = new GameRoom(code, _accounts);
                _rooms[code] = room;
                return room;
            }
        }

        public GameRoom? FindRoom(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length > 12) return null;
            lock (_lock)
                return _rooms.TryGetValue(code.ToUpperInvariant(), out var r) ? r : null;
        }

        public void RemoveIfEmpty(string code)
        {
            lock (_lock)
            {
                if (_rooms.TryGetValue(code, out var room) && room.IsEmpty)
                    _rooms.Remove(code);
            }
        }

        private string GenerateCode()
        {
            var chars = new char[6];
            for (int i = 0; i < chars.Length; i++)
                chars[i] = CodeChars[_rng.Next(CodeChars.Length)];
            return new string(chars);
        }
    }
}
