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

        // Characters used for room codes — no O/0/I/1 to avoid confusion
        private const string CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        public GameRoom CreateRoom()
        {
            lock (_lock)
            {
                string code;
                do { code = GenerateCode(); } while (_rooms.ContainsKey(code));

                var room = new GameRoom(code);
                _rooms[code] = room;
                return room;
            }
        }

        public GameRoom? FindRoom(string code)
        {
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
