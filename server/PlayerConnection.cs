// =============================================================================
// PlayerConnection.cs
// Wraps a single player's WebSocket — handles serialisation and sending.
// =============================================================================

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RiichiServer.Messages;

namespace RiichiServer
{
    public class PlayerConnection
    {
        // A single game message is tiny (a few hundred bytes). Anything near this
        // cap is not a legitimate client — drop the connection rather than buffer it.
        private const int MaxMessageBytes = 16 * 1024;

        // Rate limit: no legitimate client sends more than a few messages per second.
        private const int RateLimitWindowSeconds = 5;
        private const int RateLimitMaxMessages   = 40;

        private readonly WebSocket _ws;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private DateTime _rateWindowStart = DateTime.UtcNow;
        private int      _rateWindowCount = 0;
        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
            Converters                  = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        public string      PlayerId    { get; }            // Server-generated connection ID
        public string      PlayerUuid  { get; set; } = ""; // Client-provided UUID (for reconnection)
        public string      DisplayName { get; set; } = "Player";
        public int         Seat        { get; set; } = -1;
        public bool        IsAlive     => _ws.State == WebSocketState.Open;

        public PlayerConnection(WebSocket ws, string playerId)
        {
            _ws      = ws;
            PlayerId = playerId;
        }

        // ---- Sending --------------------------------------------------------

        public async Task SendAsync(ServerMessage msg, CancellationToken ct = default)
        {
            if (!IsAlive) return;
            // Serialize sends — the game loop and per-connection receive loops can
            // both send to the same socket, and WebSocket forbids concurrent sends.
            await _sendLock.WaitAsync(ct);
            try
            {
                if (!IsAlive) return;
                var json  = JsonSerializer.Serialize(msg, _jsonOpts);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            catch { /* Connection already closed */ }
            finally { _sendLock.Release(); }
        }

        public async Task SendErrorAsync(string message, CancellationToken ct = default)
            => await SendAsync(new ServerMessage { Type = ServerMessageType.Error, Error = message }, ct);

        // ---- Receiving ------------------------------------------------------

        /// <summary>
        /// Read a single complete message from the WebSocket.
        /// Returns null when the connection closes.
        /// </summary>
        public async Task<ClientMessage?> ReceiveAsync(CancellationToken ct = default)
        {
            var buffer = new byte[4096];
            var sb     = new StringBuilder();
            int totalBytes = 0;

            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                }
                catch
                {
                    return null;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    return null;

                totalBytes += result.Count;
                if (totalBytes > MaxMessageBytes)
                {
                    await CloseAsync();
                    return null;
                }

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    if (RateLimitExceeded())
                    {
                        await CloseAsync();
                        return null;
                    }

                    var json = sb.ToString();
                    sb.Clear();
                    try
                    {
                        return JsonSerializer.Deserialize<ClientMessage>(json, _jsonOpts);
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
        }

        private bool RateLimitExceeded()
        {
            var now = DateTime.UtcNow;
            if ((now - _rateWindowStart).TotalSeconds > RateLimitWindowSeconds)
            {
                _rateWindowStart = now;
                _rateWindowCount = 0;
            }
            return ++_rateWindowCount > RateLimitMaxMessages;
        }

        public async Task CloseAsync()
        {
            if (!IsAlive) return;
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch { }
        }
    }
}
