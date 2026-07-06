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
using RiichiServer.Auth;
using RiichiServer.Messages;

// ---- Accounts (optional) --------------------------------------------------
// DATABASE_URL unset  → guest-only mode, register/login return a friendly error.
// DATABASE_URL=memory → in-memory store (local testing only).
// otherwise           → Postgres (Supabase/Neon/Render URI or keyword string).
IAccountStore? accounts = null;
var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
if (!string.IsNullOrWhiteSpace(dbUrl))
{
    try
    {
        accounts = dbUrl.Trim().Equals("memory", StringComparison.OrdinalIgnoreCase)
            ? new InMemoryAccountStore()
            : new PostgresAccountStore(dbUrl.Trim());
        await accounts.InitAsync();
        Console.WriteLine("[Auth] Account store ready.");
    }
    catch (Exception ex)
    {
        // The game must stay playable for guests even if the DB is down.
        Console.WriteLine($"[Auth] ERROR: account store init failed — accounts disabled. {ex.Message}");
        accounts = null;
    }
}
else
{
    Console.WriteLine("[Auth] DATABASE_URL not set — running guest-only (accounts disabled).");
}

var tokens = new TokenService(Environment.GetEnvironmentVariable("TOKEN_SIGNING_KEY"));

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(new RoomManager(accounts));

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
    int failedLogins = 0;

    // Resolve the connection's identity for a lobby message: a valid session
    // token wins (account name + stable "acct:<id>" uuid so reconnection works
    // across devices); otherwise fall back to the guest displayName/uuid.
    void ApplyIdentity(ClientMessage msg)
    {
        if (!string.IsNullOrEmpty(msg.Token)
            && tokens.TryValidate(msg.Token, out long acctId, out string acctName))
        {
            conn.AccountId   = acctId;
            conn.DisplayName = acctName;
            conn.PlayerUuid  = "acct:" + acctId;
        }
        else
        {
            conn.AccountId   = null;
            conn.DisplayName = CleanDisplayName(msg.DisplayName);
            conn.PlayerUuid  = CleanUuid(msg.Uuid) ?? conn.PlayerId;
        }
    }

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
                        ApplyIdentity(msg);
                        room = rooms.CreateRoom();
                        if (room == null)
                        {
                            await conn.SendErrorAsync("Server is full — try again later.");
                            break;
                        }
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

                        ApplyIdentity(msg);
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
                    {
                        // Player is reconnecting with their identity + room code (lobby or
                        // mid-game). Identity = session token when present, else guest UUID.
                        bool hasToken = !string.IsNullOrEmpty(msg.Token);
                        if (string.IsNullOrWhiteSpace(msg.Code)
                            || (!hasToken && string.IsNullOrWhiteSpace(msg.Uuid)))
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

                        if (hasToken && tokens.TryValidate(msg.Token!, out long rjId, out string rjName))
                        {
                            conn.AccountId   = rjId;
                            conn.DisplayName = rjName;
                            conn.PlayerUuid  = "acct:" + rjId;
                        }
                        else
                        {
                            var rjUuid = CleanUuid(msg.Uuid);
                            if (rjUuid == null)
                            {
                                await conn.SendErrorAsync("Room code and UUID required to rejoin.");
                                break;
                            }
                            conn.PlayerUuid = rjUuid;
                        }

                        if (!rejoinRoom.GameStarted)
                        {
                            // ---- Lobby reconnect ----------------------------------------
                            if (!rejoinRoom.RejoinLobby(conn.PlayerUuid, conn))
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
                            bool rejoined = await rejoinRoom.RejoinAsync(conn.PlayerUuid, conn);
                            if (!rejoined)
                            {
                                await conn.SendErrorAsync("Could not rejoin — UUID not recognised or game ended.");
                                break;
                            }
                            room = rejoinRoom;
                        }
                        break;
                    }

                    case ClientMessageType.JoinQueue:
                        ApplyIdentity(msg);
                        // JoinAsync sends queueJoined itself (before any match fires)
                        bool added = await queue.JoinAsync(
                            conn, conn.DisplayName, conn.PlayerUuid);
                        if (!added)
                            await conn.SendErrorAsync("Already in the matchmaking queue.");
                        break;

                    case ClientMessageType.LeaveQueue:
                        queue.Leave(conn);
                        // No specific response needed — client just goes back to the connect panel
                        break;

                    case ClientMessageType.Register:
                    {
                        if (accounts == null)
                        {
                            await conn.SendErrorAsync("Accounts are not enabled on this server.");
                            break;
                        }

                        var uname = msg.Username?.Trim() ?? "";
                        if (!System.Text.RegularExpressions.Regex.IsMatch(uname, "^[A-Za-z0-9_-]{3,20}$"))
                        {
                            await conn.SendErrorAsync("Username must be 3-20 characters: letters, numbers, _ or -.");
                            break;
                        }
                        if (msg.Password is not { Length: >= 8 and <= 72 })
                        {
                            await conn.SendErrorAsync("Password must be 8-72 characters.");
                            break;
                        }

                        var created = await accounts.CreateAsync(uname, PasswordHasher.Hash(msg.Password));
                        if (created == null)
                        {
                            await conn.SendErrorAsync("That username is already taken.");
                            break;
                        }

                        conn.AccountId   = created.Id;
                        conn.DisplayName = created.Username;
                        conn.PlayerUuid  = "acct:" + created.Id;
                        await conn.SendAsync(new ServerMessage
                        {
                            Type     = ServerMessageType.AuthOk,
                            Token    = tokens.Create(created.Id, created.Username),
                            Username = created.Username,
                        });
                        break;
                    }

                    case ClientMessageType.Login:
                    {
                        if (accounts == null)
                        {
                            await conn.SendErrorAsync("Accounts are not enabled on this server.");
                            break;
                        }

                        var uname = msg.Username?.Trim() ?? "";
                        var acct  = uname.Length > 0 ? await accounts.GetByUsernameAsync(uname) : null;
                        if (acct == null || msg.Password == null
                            || !PasswordHasher.Verify(msg.Password, acct.PasswordHash))
                        {
                            // Same message for unknown user and wrong password (no enumeration),
                            // small delay + strike-out to blunt brute force.
                            failedLogins++;
                            await Task.Delay(400);
                            await conn.SendErrorAsync("Invalid username or password.");
                            if (failedLogins >= 5) await conn.CloseAsync();
                            break;
                        }

                        conn.AccountId   = acct.Id;
                        conn.DisplayName = acct.Username;
                        conn.PlayerUuid  = "acct:" + acct.Id;
                        await conn.SendAsync(new ServerMessage
                        {
                            Type        = ServerMessageType.AuthOk,
                            Token       = tokens.Create(acct.Id, acct.Username),
                            Username    = acct.Username,
                            GamesPlayed = acct.GamesPlayed,
                            GamesWon    = acct.GamesWon,
                        });
                        break;
                    }

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

// ---- Suppress favicon.ico 404 log spam from browsers ---------------------
app.MapGet("/favicon.ico", () => Results.NoContent());

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

// Display names are echoed to every player in the room — cap the length and
// strip control characters so a client can't inject junk into other UIs.
static string CleanDisplayName(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return "Player";
    var cleaned = new string(raw.Trim().Where(c => !char.IsControl(c)).ToArray());
    if (cleaned.Length == 0) return "Player";
    return cleaned.Length <= 20 ? cleaned : cleaned[..20];
}

// UUIDs are client-generated opaque identity tokens — bound the length so they
// can't be abused as a storage channel.
static string? CleanUuid(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    var trimmed = raw.Trim();
    return trimmed.Length <= 64 ? trimmed : trimmed[..64];
}
