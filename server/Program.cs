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
        IAccountStore inner = dbUrl.Trim().Equals("memory", StringComparison.OrdinalIgnoreCase)
            ? new InMemoryAccountStore()
            : new PostgresAccountStore(dbUrl.Trim());

        // Wrapped so a database outage (e.g. a free-tier Postgres paused for
        // inactivity) is recovered from automatically on the next account
        // operation — no server restart needed. InitAsync never throws; it
        // logs whether the store came up or will keep retrying.
        var resilient = new ResilientAccountStore(inner);
        await resilient.InitAsync();
        accounts = resilient;
    }
    catch (Exception ex)
    {
        // Only an unusable DATABASE_URL lands here (the store could not even be
        // constructed). That needs a config fix, so there is nothing to retry.
        Console.WriteLine($"[Auth] ERROR: DATABASE_URL is unusable — accounts disabled. {ex.Message}");
        accounts = null;
    }
}
else
{
    Console.WriteLine("[Auth] DATABASE_URL not set — running guest-only (accounts disabled).");
}

var tokens = new TokenService(Environment.GetEnvironmentVariable("TOKEN_SIGNING_KEY"));
var ResetCodeLifetime = TimeSpan.FromMinutes(15);

// ---- Email (optional — enables password reset) ----------------------------
// RESEND_API_KEY set → Resend HTTP API (set EMAIL_FROM for a custom sender).
// EMAIL_MODE=console → dev sender that prints the mail to the server log.
IEmailSender? emailSender = null;
var resendKey = Environment.GetEnvironmentVariable("RESEND_API_KEY");
if (!string.IsNullOrWhiteSpace(resendKey))
{
    emailSender = new ResendEmailSender(resendKey, Environment.GetEnvironmentVariable("EMAIL_FROM"));
    Console.WriteLine("[Email] Resend sender configured — password reset enabled.");
}
else if (Environment.GetEnvironmentVariable("EMAIL_MODE") == "console")
{
    emailSender = new ConsoleEmailSender();
    Console.WriteLine("[Email] Console sender (dev) — reset codes appear in this log.");
}
else
{
    Console.WriteLine("[Email] No sender configured — password reset disabled.");
}

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
    // The token's embedded version must match the account's current version —
    // password changes bump it, revoking all previously issued tokens.
    /// <summary>
    /// Read an account's stored table, tolerating a database that is briefly away.
    /// A missing set is not an error - it just means they have never chosen one.
    /// </summary>
    static async Task<string> LoadCosmeticsAsync(IAccountStore store, long accountId)
    {
        try   { return await store.GetCosmeticsAsync(accountId) ?? ""; }
        catch (AccountStoreUnavailableException) { return ""; }
    }

    async Task ApplyIdentityAsync(ClientMessage msg)
    {
        conn.AccountId = null;
        if (accounts != null && !string.IsNullOrEmpty(msg.Token)
            && tokens.TryValidate(msg.Token, out long acctId, out _, out int tver))
        {
            try
            {
                var acct = await accounts.GetByIdAsync(acctId);
                if (acct != null && acct.TokenVersion == tver)
                {
                    conn.AccountId   = acctId;
                    conn.DisplayName = acct.Username;
                    conn.PlayerUuid  = "acct:" + acctId;

                    // Load their table so it is ready to relay at game start. A failure
                    // here must not cost them their seat, so it falls back to the
                    // default set rather than propagating.
                    try
                    {
                        conn.Cosmetics = await accounts.GetCosmeticsAsync(acctId) ?? "";
                    }
                    catch (AccountStoreUnavailableException)
                    {
                        conn.Cosmetics = "";
                    }
                    return;
                }
            }
            catch (AccountStoreUnavailableException)
            {
                // Database is down: we cannot confirm the token, but refusing to
                // seat the player would break play for a transient outage.
                // Fall through to guest identity — they keep playing, just
                // without account stats for this session.
            }
        }
        conn.DisplayName = CleanDisplayName(msg.DisplayName);
        conn.PlayerUuid  = CleanUuid(msg.Uuid) ?? conn.PlayerId;
    }

    // Shared guard for account-management actions: needs accounts enabled and a
    // current (non-revoked) session token. Sends the error itself on failure.
    async Task<AccountRecord?> RequireAccountAsync(ClientMessage m)
    {
        if (accounts == null)
        {
            await conn.SendErrorAsync("Accounts are not enabled on this server.");
            return null;
        }
        // Account changes genuinely need the database — unlike seating a player,
        // there is no safe degraded behaviour, so report it and let them retry.
        if (accounts is ResilientAccountStore { IsReady: false })
        {
            // Probe once so a recovered database is picked up immediately rather
            // than reporting stale unavailability.
            try   { await accounts.GetByIdAsync(0); }
            catch (AccountStoreUnavailableException)
            {
                await conn.SendErrorAsync(
                    "Accounts are temporarily unavailable — please try again in a moment.");
                return null;
            }
        }
        if (string.IsNullOrEmpty(m.Token)
            || !tokens.TryValidate(m.Token, out long id, out _, out int ver))
        {
            await conn.SendErrorAsync("Sign in first.");
            return null;
        }
        var acct = await accounts.GetByIdAsync(id);
        if (acct == null || acct.TokenVersion != ver)
        {
            await conn.SendErrorAsync("Session expired — sign in again.");
            return null;
        }
        return acct;
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

            // Safety net: any account operation may find the database down.
            // Report it as transient instead of dropping the connection — the
            // store reconnects on its own, so a retry usually just works.
            try
            {
                if (room == null)
                {
                    // ---- Pre-game: lobby messages only --------------------------
                    switch (msg.Type)
                    {
                        case ClientMessageType.CreateRoom:
                            await ApplyIdentityAsync(msg);
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

                            await ApplyIdentityAsync(msg);
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

                            await ApplyIdentityAsync(msg);
                            if (conn.AccountId == null && string.IsNullOrWhiteSpace(msg.Uuid))
                            {
                                await conn.SendErrorAsync("Room code and UUID required to rejoin.");
                                break;
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
                            await ApplyIdentityAsync(msg);
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
                                Token    = tokens.Create(created.Id, created.Username, created.TokenVersion),
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
                                Token       = tokens.Create(acct.Id, acct.Username, acct.TokenVersion),
                                Username    = acct.Username,
                                GamesPlayed = acct.GamesPlayed,
                                GamesWon    = acct.GamesWon,

                                // The account's table comes back with the sign-in, so it
                                // follows the player to a device that has never seen it.
                                // Sent as a one-element array to reuse the same field the
                                // per-seat relay uses rather than adding a second one.
                                Cosmetics   = new[] { await LoadCosmeticsAsync(accounts, acct.Id) },
                            });
                            break;
                        }

                        case ClientMessageType.ChangePassword:
                        {
                            var acct = await RequireAccountAsync(msg);
                            if (acct == null) break;

                            // A session token alone must not be enough to take over an
                            // account — the current password is required as well.
                            if (msg.OldPassword == null
                                || !PasswordHasher.Verify(msg.OldPassword, acct.PasswordHash))
                            {
                                failedLogins++;
                                await Task.Delay(400);
                                await conn.SendErrorAsync("Current password is incorrect.");
                                if (failedLogins >= 5) await conn.CloseAsync();
                                break;
                            }
                            if (msg.NewPassword is not { Length: >= 8 and <= 72 })
                            {
                                await conn.SendErrorAsync("New password must be 8-72 characters.");
                                break;
                            }

                            int newVer = await accounts!.UpdatePasswordAsync(
                                acct.Id, PasswordHasher.Hash(msg.NewPassword));
                            await conn.SendAsync(new ServerMessage
                            {
                                Type        = ServerMessageType.AuthOk,
                                Token       = tokens.Create(acct.Id, acct.Username, newVer),
                                Username    = acct.Username,
                                GamesPlayed = acct.GamesPlayed,
                                GamesWon    = acct.GamesWon,
                            });
                            break;
                        }

                        case ClientMessageType.SetCosmetics:
                        {
                            var acct = await RequireAccountAsync(msg);
                            if (acct == null) break;

                            // Validated against the shared catalogue rather than stored
                            // as received: this string is drawn on three other players'
                            // screens, so an unrecognised id must become a default here
                            // rather than at each client.
                            var parsed = RiichiMahjong.Core.CosmeticSet
                                .Deserialise(msg.Cosmetics).Serialise();

                            await accounts!.SetCosmeticsAsync(acct.Id, parsed);
                            conn.Cosmetics = parsed;

                            await conn.SendAsync(new ServerMessage
                            {
                                Type    = ServerMessageType.AccountOk,
                                Message = "Table saved.",
                            });
                            break;
                        }

                        case ClientMessageType.SetEmail:
                        {
                            var acct = await RequireAccountAsync(msg);
                            if (acct == null) break;

                            var email = msg.Email?.Trim() ?? "";
                            if (email.Length > 254
                                || !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                            {
                                await conn.SendErrorAsync("That doesn't look like a valid email address.");
                                break;
                            }

                            if (!await accounts!.SetEmailAsync(acct.Id, email))
                            {
                                await conn.SendErrorAsync("Could not attach that email — it may already be in use.");
                                break;
                            }
                            await conn.SendAsync(new ServerMessage
                            {
                                Type    = ServerMessageType.AccountOk,
                                Message = $"Recovery email set to {email}.",
                            });
                            break;
                        }

                        case ClientMessageType.RequestReset:
                        {
                            if (accounts == null || emailSender == null)
                            {
                                await conn.SendErrorAsync("Password reset is not available on this server.");
                                break;
                            }

                            // The response is identical whether the account exists or has
                            // an email (no enumeration), and the actual send happens off
                            // this request path so timing doesn't leak either.
                            var uname = msg.Username?.Trim() ?? "";
                            var acct  = uname.Length > 0 ? await accounts.GetByUsernameAsync(uname) : null;
                            if (acct?.Email is string emailTo)
                            {
                                // 60 s resend cooldown per account blunts email bombing
                                var existing = await accounts.GetResetCodeAsync(acct.Id);
                                var issuedAt = existing?.ExpiresAt - ResetCodeLifetime;
                                if (existing == null || issuedAt < DateTimeOffset.UtcNow.AddSeconds(-60))
                                {
                                    var code = System.Security.Cryptography.RandomNumberGenerator
                                        .GetInt32(0, 1_000_000).ToString("D6");
                                    await accounts.SaveResetCodeAsync(
                                        acct.Id, PasswordHasher.Hash(code),
                                        DateTimeOffset.UtcNow.Add(ResetCodeLifetime));

                                    var (sender, user) = (emailSender, acct.Username);
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            await sender.SendAsync(emailTo,
                                                "Riichi Mahjong password reset",
                                                $"Hi {user}, your password reset code is {code}. " +
                                                "It expires in 15 minutes. If you didn't request this, you can ignore this email.");
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"[Email] send failed: {ex.Message}");
                                        }
                                    });
                                }
                            }

                            await conn.SendAsync(new ServerMessage
                            {
                                Type    = ServerMessageType.AccountOk,
                                Message = "If that account has a recovery email, a reset code has been sent.",
                            });
                            break;
                        }

                        case ClientMessageType.ResetPassword:
                        {
                            if (accounts == null)
                            {
                                await conn.SendErrorAsync("Accounts are not enabled on this server.");
                                break;
                            }

                            // One uniform failure message for every wrong path.
                            const string bad = "Invalid or expired reset code.";
                            var uname = msg.Username?.Trim() ?? "";
                            var acct  = uname.Length > 0 ? await accounts.GetByUsernameAsync(uname) : null;
                            var reset = acct != null ? await accounts.GetResetCodeAsync(acct.Id) : null;

                            if (acct == null || reset == null || reset.ExpiresAt < DateTimeOffset.UtcNow)
                            {
                                if (acct != null) await accounts.DeleteResetCodeAsync(acct.Id);
                                failedLogins++;
                                await Task.Delay(400);
                                await conn.SendErrorAsync(bad);
                                if (failedLogins >= 5) await conn.CloseAsync();
                                break;
                            }

                            // Count the attempt BEFORE verifying so racing guesses
                            // cannot exceed the cap; 5 misses burns the code.
                            int attempts = await accounts.IncrementResetAttemptsAsync(acct.Id);
                            if (attempts > 5 || msg.ResetCode == null
                                || !PasswordHasher.Verify(msg.ResetCode, reset.CodeHash))
                            {
                                if (attempts >= 5) await accounts.DeleteResetCodeAsync(acct.Id);
                                failedLogins++;
                                await Task.Delay(400);
                                await conn.SendErrorAsync(bad);
                                if (failedLogins >= 5) await conn.CloseAsync();
                                break;
                            }

                            if (msg.NewPassword is not { Length: >= 8 and <= 72 })
                            {
                                await conn.SendErrorAsync("New password must be 8-72 characters.");
                                break;
                            }

                            int rpVer = await accounts.UpdatePasswordAsync(
                                acct.Id, PasswordHasher.Hash(msg.NewPassword));
                            await accounts.DeleteResetCodeAsync(acct.Id);
                            await conn.SendAsync(new ServerMessage
                            {
                                Type        = ServerMessageType.AuthOk,
                                Token       = tokens.Create(acct.Id, acct.Username, rpVer),
                                Username    = acct.Username,
                                GamesPlayed = acct.GamesPlayed,
                                GamesWon    = acct.GamesWon,
                            });
                            break;
                        }

                        case ClientMessageType.GetLeaderboard:
                        {
                            if (accounts == null)
                            {
                                await conn.SendErrorAsync("Accounts are not enabled on this server.");
                                break;
                            }

                            var top = await accounts.GetTopAsync(20);
                            await conn.SendAsync(new ServerMessage
                            {
                                Type        = ServerMessageType.Leaderboard,
                                Leaderboard = top.Select((e, i) => new LeaderboardEntryDto
                                {
                                    Rank        = i + 1,
                                    Name        = e.Username,
                                    GamesPlayed = e.GamesPlayed,
                                    GamesWon    = e.GamesWon,
                                    TotalPoints = e.TotalPoints,
                                }).ToList(),
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
            catch (AccountStoreUnavailableException)
            {
                await conn.SendErrorAsync(
                    "Accounts are temporarily unavailable — please try again in a moment.");
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

// ---- Database keep-alive --------------------------------------------------
// Runs one trivial query so an external scheduler (.github/workflows/
// keepalive.yml) can generate the database activity a free-tier Postgres
// needs to avoid auto-pausing after ~7 days idle. The result is cached for a
// minute so repeated hits can't be used to hammer the database.
// 200 = reachable, 503 = unavailable (or accounts disabled).
var dbHealthGate = new object();
var dbHealthAt   = DateTime.MinValue;
var dbHealthOk   = false;
var dbHealthWhy  = "not checked yet";
app.MapGet("/health/db", async () =>
{
    lock (dbHealthGate)
    {
        if (DateTime.UtcNow - dbHealthAt < TimeSpan.FromSeconds(60))
            return DbHealthResult(dbHealthOk, dbHealthWhy, cached: true);
    }

    bool ok;
    string why;
    if (accounts == null)
    {
        ok = false;
        why = "accounts disabled (no DATABASE_URL)";
    }
    else
    {
        try   { await accounts.PingAsync(); ok = true;  why = "select 1 ok"; }
        catch (Exception ex) { ok = false; why = ex.Message; }
    }

    lock (dbHealthGate) { dbHealthAt = DateTime.UtcNow; dbHealthOk = ok; dbHealthWhy = why; }
    return DbHealthResult(ok, why, cached: false);

    static IResult DbHealthResult(bool ok, string why, bool cached)
    {
        var body = new { db = ok ? "ok" : "unavailable", detail = why, cached, time = DateTime.UtcNow };
        return ok ? Results.Ok(body) : Results.Json(body, statusCode: 503);
    }
});

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
