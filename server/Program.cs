// =============================================================================
// Program.cs — ASP.NET Core WebSocket server for Riichi Mahjong multiplayer.
//
// Every player connects to  ws://host/ws
// They then send a createRoom or joinRoom message to enter the lobby.
// The host sends startGame to begin.
// All subsequent messages are game actions routed through GameRoom.
// =============================================================================

using System.Collections.Concurrent;
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

// ---- Matchmaking ----------------------------------------------------------
// pendingMatches maps a connection's PlayerId → GameRoom after the queue fires.
// Each player's receive loop checks this dict at the top of every iteration so
// game messages are routed correctly even though no roomJoined was sent first.

var pendingMatches = new ConcurrentDictionary<string, GameRoom>();
var queue          = new MatchmakingQueue(rooms, pendingMatches);

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

            // If this player was matched via matchmaking while waiting, pick up the room now
            if (room == null && pendingMatches.TryRemove(conn.PlayerId, out var matchedRoom))
                room = matchedRoom;

            if (room == null)
            {
                // ---- Pre-game: lobby messages only --------------------------
                switch (msg.Type)
                {
                    case ClientMessageType.CreateRoom:
                        conn.DisplayName = msg.DisplayName ?? "Player";
                        conn.PlayerUuid  = msg.Uuid ?? conn.PlayerId;
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
                        conn.PlayerUuid  = msg.Uuid ?? conn.PlayerId;
                        int seat = room.AddPlayer(conn);
                        if (seat < 0)
                        {
                            await conn.SendErrorAsync("Room is full.");
                            room = null;
                            break;
                        }

                        await conn.SendAsync(new ServerMessage
                        {
                            Type     = ServerMessageType.RoomJoined,
                            Code     = room.Code,
                            YourSeat = seat,
                            Players  = room.GetPlayerList(),
                        });

                        await BroadcastToRoom(room, conn, new ServerMessage
                        {
                            Type    = ServerMessageType.PlayerJoined,
                            Seat    = seat,
                            Players = room.GetPlayerList(),
                        });
                        break;

                    case ClientMessageType.RejoinRoom:
                        // Player is reconnecting with their UUID + room code (lobby or mid-game)
                        if (string.IsNullOrWhiteSpace(msg.Code) || string.IsNullOrWhiteSpace(msg.Uuid))
                        {
                            await conn.SendErrorAsync("Room code and UUID required to rejoin.");
                            break;
                        }

                        var rejoinRoom = rooms.FindRoom(msg.Code);
                        if (rejoinRoom == null)
                        {
                            await conn.SendErrorAsync($"Room '{msg.Code}' not found.");
                            break;
                        }

                        conn.PlayerUuid = msg.Uuid;

                        if (!rejoinRoom.GameStarted)
                        {
                            // ---- Lobby reconnect ----------------------------------------
                            if (!rejoinRoom.RejoinLobby(msg.Uuid, conn))
                            {
                                await conn.SendErrorAsync("Could not rejoin lobby — seat no longer available.");
                                break;
                            }
                            room = rejoinRoom;

                            // Tell rejoining player their seat + current player list
                            await conn.SendAsync(new ServerMessage
                            {
                                Type     = ServerMessageType.RoomJoined,
                                Code     = room.Code,
                                YourSeat = conn.Seat,
                                Players  = room.GetPlayerList(),
                            });

                            // Tell remaining lobby players someone returned
                            await BroadcastToRoom(room, conn, new ServerMessage
                            {
                                Type    = ServerMessageType.PlayerJoined,
                                Seat    = conn.Seat,
                                Players = room.GetPlayerList(),
                            });
                        }
                        else
                        {
                            // ---- Mid-game reconnect -------------------------------------
                            bool rejoined = await rejoinRoom.RejoinAsync(msg.Uuid, conn);
                            if (!rejoined)
                            {
                                await conn.SendErrorAsync("Could not rejoin — UUID not recognised or game ended.");
                                break;
                            }
                            room = rejoinRoom;
                        }
                        break;

                    case ClientMessageType.JoinQueue:
                        conn.DisplayName = msg.DisplayName ?? "Player";
                        conn.PlayerUuid  = msg.Uuid ?? conn.PlayerId;
                        bool added = await queue.JoinAsync(
                            conn, conn.DisplayName, conn.PlayerUuid);
                        if (added)
                        {
                            await conn.SendAsync(new ServerMessage
                            {
                                Type = ServerMessageType.QueueJoined,
                            });
                        }
                        else
                        {
                            await conn.SendErrorAsync("Already in the matchmaking queue.");
                        }
                        break;

                    case ClientMessageType.LeaveQueue:
                        queue.Leave(conn);
                        // No specific response needed — client just goes back to the connect panel
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

        // Always remove from queue in case the player disconnected while searching
        queue.Leave(conn);

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
