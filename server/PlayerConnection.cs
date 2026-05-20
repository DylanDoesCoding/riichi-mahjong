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
        private readonly WebSocket _ws;
        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
            Converters                  = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        public string      PlayerId    { get; }   // Unique connection ID
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
            try
            {
                var json  = JsonSerializer.Serialize(msg, _jsonOpts);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            catch { /* Connection already closed */ }
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

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
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
