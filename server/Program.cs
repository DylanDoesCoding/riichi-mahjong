// =============================================================================
// Program.cs — ASP.NET Core WebSocket server for Riichi Mahjong multiplayer.
//
// Every player connects to  ws://host/ws
// They then send a createRoom or joinRoom message to enter the lobby.
// The host sends startGame to begin.
// All subsequent messages are game actions routed through GameRoom.
// =============================================================================

using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using RiichiServer;
using RiichiServer.Messages;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RoomManager>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

var rooms   = app.Services.GetRequiredService<RoomManager>();
var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters             = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

// ---- Single WebSocket endpoint -------------------------------------------

app.MapGet("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var ws   = await context.WebSockets.AcceptWebSocketAsync();
    var conn = new PlayerConnection(ws, Guid.NewGuid().ToString("N")[..8]);

    GameRoom? room = null;

    try
    {
        // Per-connection receive loop
        while (conn.IsAlive)
        {
            var msg = await conn.ReceiveAsync(context.RequestAborted);
            if (msg == null) break;

            if (room == null)
            {
                // ---- Pre-game: lobby messages only --------------------------
                switch (msg.Type)
                {
                    case ClientMessageType.CreateRoom:
                        conn.DisplayName = msg.DisplayName ?? "Player";
                        room = rooms.CreateRoom();
                        room.AddPlayer(conn);

                        await conn.SendAsync(new ServerMessage
                        {
                            Type     = ServerMessageType.RoomCreated,
                            Code     = room.Code,
                            YourSeat = conn.Seat,
                            Players  = room.GetPlayerList(),
                        });
                        break;

                    case ClientMessageType.JoinRoom:
                        if (string.IsNullOrWhiteSpace(msg.Code))
                        {
                            await conn.SendErrorAsync("Room code required.");
                            break;
                        }

                        room = rooms.FindRoom(msg.Code);
                        if (room == null)
                        {
                            await conn.SendErrorAsync($"Room '{msg.Code}' not found.");
                            room = null;
                            break;
                        }
                        if (room.GameStarted)
                        {
                            await conn.SendErrorAsync("That game has already started.");
                            room = null;
                            break;
                        }

                        conn.DisplayName = msg.DisplayName ?? "Player";
                        int seat = room.AddPlayer(conn);
                        if (seat < 0)
                        {
                            await conn.SendErrorAsync("Room is full.");
                            room = null;
                            break;
                        }

                        // Tell the new player they joined
                        await conn.SendAsync(new ServerMessage
                        {
                            Type     = ServerMessageType.RoomJoined,
                            Code     = room.Code,
                            YourSeat = seat,
                            Players  = room.GetPlayerList(),
                        });

                        // Tell everyone else
                        await BroadcastToRoom(room, conn, new ServerMessage
                        {
                            Type    = ServerMessageType.PlayerJoined,
                            Seat    = seat,
                            Players = room.GetPlayerList(),
                        });
                        break;

                    default:
                        await conn.SendErrorAsync("Join or create a room first.");
                        break;
                }
            }
            else if (!room.GameStarted)
            {
                // ---- Lobby phase (host controls) ----------------------------
                if (msg.Type == ClientMessageType.StartGame && room.IsHost(conn))
                {
                    // Fire-and-forget — the game loop runs independently
                    _ = Task.Run(() => room.StartGameAsync());
                }
                else if (msg.Type != ClientMessageType.StartGame)
                {
                    await room.HandlePlayerActionAsync(conn, msg);
                }
            }
            else
            {
                // ---- Game running -------------------------------------------
                await room.HandlePlayerActionAsync(conn, msg);
            }
        }
    }
    finally
    {
        await conn.CloseAsync();

        if (room != null)
        {
            room.RemovePlayer(conn);

            // Notify remaining players
            await BroadcastToRoom(room, conn, new ServerMessage
            {
                Type    = ServerMessageType.PlayerLeft,
                Seat    = conn.Seat,
                Players = room.GetPlayerList(),
            });

            rooms.RemoveIfEmpty(room.Code);
        }
    }
});

// ---- Health check --------------------------------------------------------
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

app.Run();

// ---- Helpers -------------------------------------------------------------

static async Task BroadcastToRoom(GameRoom room, PlayerConnection exclude, ServerMessage msg)
{
    var players = room.GetPlayerList();
    // We only have the player list DTO — we need to send via room
    // This is handled inside GameRoom for game messages; here we just
    // need to send lobby updates. Use a simple approach: the room exposes
    // a broadcast helper.
    await room.BroadcastExceptAsync(exclude, msg);
}
