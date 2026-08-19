# Riichi Mahjong — Multiplayer

Branch: `feature/multiplayer`

## Overview

This branch adds online multiplayer using a **server-authoritative** architecture.  
A lightweight ASP.NET Core WebSocket server runs all game logic; Godot clients send actions and render results.  
No player can see another player's hand. Any player can disconnect and reconnect without ending the game.

---

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│           ASP.NET Core Server (Render.com)                │
│                                                           │
│  /ws  WebSocket endpoint                                  │
│    └── RoomManager  — creates / finds rooms by code       │
│          └── GameRoom  — owns GameState + AIPlayer[]      │
│                - drives async game loop                   │
│                - runs AI for CPU seats                    │
│                - filters hand tiles per player            │
│                - manages 8-second claim window            │
└──────────────────────────────────────────────────────────┘
         ▲ WebSocket (JSON)  ▼
┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐
│  Player A    │  │  Player B    │  │  Player C    │  │  Player D    │
│  Godot       │  │  Godot       │  │  Godot       │  │  Godot       │
│  NetworkMgr  │  │  NetworkMgr  │  │  NetworkMgr  │  │  NetworkMgr  │
└──────────────┘  └──────────────┘  └──────────────┘  └──────────────┘
```

### Key design decisions

| Decision | Choice | Reason |
|---|---|---|
| Who runs GameState | Server | Eliminates host-disconnect problems entirely |
| Hand privacy | Server sends each player only their own tiles | Other seats receive tile count only |
| Seat view | Each client rotates display so they are always at the bottom | Uses `(globalSeat - mySeat + 4) % 4` mapping |
| CPU fill | Server runs AIPlayer for any empty seats | Up to 3 CPU opponents if fewer than 4 humans join |
| Claim window | Server waits up to 8 seconds for human action, then auto-resolves | Prevents game stalling |
| Identity | Display name + client-generated UUID stored locally | No login required; real accounts can be added later |
| Reconnection | Server holds full game state; client sends UUID to reclaim seat | Mid-game disconnects don't end the game |

---

## Project Structure

```
riichi-mahjong/
├── shared/
│   └── RiichiMahjong.Core.csproj   # Shared lib — Core + AI, no Godot deps
│                                     # Referenced by both Godot and server
├── server/
│   ├── RiichiServer.csproj          # ASP.NET Core 8 WebSocket server
│   ├── Program.cs                   # Startup + /ws endpoint handler
│   ├── RoomManager.cs               # Room creation, lookup, cleanup
│   ├── GameRoom.cs                  # One room: game loop, AI, privacy filtering
│   ├── PlayerConnection.cs          # WebSocket wrapper (send / receive JSON)
│   └── Messages/
│       ├── TileDto.cs               # Tile, Meld, PlayerInfo, ScoreEntry DTOs
│       ├── ClientMessage.cs         # Client → server message types
│       └── ServerMessage.cs        # Server → client message types
├── src/
│   └── UI/
│       ├── NetworkManager.cs        # Godot WebSocketPeer client
│       │                             # Fires C# events for each server message
│       ├── GameController.cs        # Dual-mode: local vs network
│       ├── LobbyController.cs       # Lobby UI — create/join room, player list
│       └── GameSettings.cs          # PlayerName, ServerUrl, PlayerUuid
├── Dockerfile                        # Multi-stage .NET 8 build (repo-root context)
├── .dockerignore                     # Excludes Godot assets from build context
└── render.yaml                       # Render.com IaC — free plan, Docker runtime
```

---

## Message Protocol

All messages are JSON over WebSocket. The `type` field is the discriminator.

### Client → Server

| type | Fields | When |
|---|---|---|
| `createRoom` | `displayName`, `uuid` | Host creates a new room |
| `joinRoom` | `code`, `displayName`, `uuid` | Guest joins by room code |
| `rejoinRoom` | `code`, `uuid` | Player reconnects mid-game |
| `startGame` | — | Host starts (CPU fills empty seats) |
| `discard` | `tile` | Player discards a tile |
| `riichi` | `tile` | Player declares riichi and discards |
| `tsumo` | — | Player declares self-draw win |
| `pon` | — | Player claims discard for pon |
| `chi` | `t1`, `t2` | Player claims discard for chi |
| `ron` | — | Player claims discard for ron |
| `kan` | `tile?` | Ankan/kakan (with tile) or daiminkan (no tile) |
| `pass` | — | Player passes on claim window |
| `nextHand` | — | Host advances to next hand |
| `register` | `username`, `password` | Create an account (returns `authOk`) |
| `login` | `username`, `password` | Sign in (returns `authOk` with token + stats) |
| `changePassword` | `token`, `oldPassword`, `newPassword` | Requires current password; revokes all old tokens |
| `setEmail` | `token`, `email` | Attach a recovery email |
| `requestReset` | `username` | Emails a 6-digit reset code (uniform response, no enumeration) |
| `resetPassword` | `username`, `resetCode`, `newPassword` | Consume the code; returns `authOk`, revokes old tokens |
| `getLeaderboard` | — | Top 20 accounts by wins (then total points); returns `leaderboard` |

All lobby messages (`createRoom`, `joinRoom`, `rejoinRoom`, `joinQueue`) additionally accept
an optional `token`. A valid token overrides `displayName`/`uuid` with the account identity,
so reconnection works across devices and the lobby always shows the account name.

### Server → Client

| type | Key fields | Notes |
|---|---|---|
| `roomCreated` | `code`, `yourSeat`, `players` | Sent to host only |
| `roomJoined` | `code`, `yourSeat`, `players` | Sent to joining player |
| `playerJoined` | `seat`, `players` | Broadcast to existing players |
| `playerLeft` | `seat`, `players` | Broadcast on disconnect |
| `gameStarted` | `yourSeat`, `names` | Sent to each player individually |
| `handDealt` | `yourTiles`, `tileCounts`, `scores` | **Tiles filtered per player** |
| `tileDrawn` | `seat`, `tile?` | `tile` present only for the drawing player |
| `tileDiscarded` | `seat`, `tile`, `isRiichiDiscard` | Public — all players |
| `meldDeclared` | `seat`, `meld` | Public — all players |
| `riichiDeclared` | `seat` | Public — all players |
| `claimWindowOpened` | `discarderSeat`, `tile`, `canRon/Pon/Chi/Kan` | Only to eligible players |
| `handEnded` | `reason`, `winners`, `scoreBoard`, `yakuNames` | All players |
| `gameOver` | `scoreBoard` | All players |
| `gameStateSnapshot` | `yourTiles`, `tileCounts`, `scores`, `discards`, `melds`, `riichiSeats`, `currentTurn` | Sent on successful rejoin |
| `authOk` | `token`, `username`, `gamesPlayed`, `gamesWon` | Register/login/reset success — client persists the token |
| `accountOk` | `message` | Account-management confirmation (email set, code sent) |
| `leaderboard` | `leaderboard[]` (rank, name, gamesPlayed, gamesWon, totalPoints) | Only accounts with ≥1 finished game |
| `error` | `error` | Sent to the relevant player |

---

## Room & Lobby Flow

```
1. Host opens game → enters name → clicks "Create Room"
   Server generates a 6-character room code (e.g. "MJNG42")
   Host sees lobby screen with code to share

2. Guests enter the code → click "Join"
   Host sees each guest appear in the player list

3. Host clicks "Start Game" (can start with 2–4 humans, CPU fills rest)
   Server deals hands — each player receives only their own tiles

4. Game plays out with server validating every action

5. After each hand: scoring panel shown, host clicks "Next Hand"

6. Game ends when a player goes below 0 points (tobi rule) or
   all rounds complete — game-over scoreboard shown
```

---

## Reconnection Flow

```
1. Player disconnects (network drop, app crash, etc.)
   Server keeps their seat and game state intact

2. On reconnect, client detects socket closure → shows reconnect overlay
   Attempts to re-open WebSocket and sends:
     { type: "rejoinRoom", code: "<roomCode>", uuid: "<playerUuid>" }

3. Server finds the seat by UUID, swaps in the new connection,
   and replies with a gameStateSnapshot containing the full board state:
     - Player's current hand tiles
     - All seat discard rivers
     - All seat melds
     - Riichi flags
     - Current turn

4. Client replays the snapshot to restore the full visual board state
```

---

## Running the Server Locally

```bash
cd server
dotnet run
# Server starts on http://localhost:5000
# WebSocket endpoint: ws://localhost:5000/ws
```

In the Lobby UI, change the server URL field to `ws://localhost:5000/ws` for local testing.

---

## Deploying to Render

The server is hosted on [Render](https://render.com) (free tier, no credit card required).

**Live server:** `wss://riichi-mahjong-server.onrender.com/ws`  
**Health check:** `https://riichi-mahjong-server.onrender.com/health`  
**DB keep-alive:** `https://riichi-mahjong-server.onrender.com/health/db` — runs one
`SELECT 1` (200 reachable / 503 unavailable). Pinged every 3 days by
`.github/workflows/keepalive.yml` so the free-tier Supabase Postgres never hits
its ~7-day idle auto-pause.

> **Note:** Render's free tier spins down after 15 minutes of inactivity.  
> The first connection after idle takes ~30 seconds to cold-start.

### Redeployment

Render auto-deploys on every push to `feature/multiplayer` via the Blueprint sync — no manual steps needed.

### Setting up from scratch

1. Go to [render.com](https://render.com) → sign up (free, no card)
2. **New → Blueprint** → connect `DylanDoesCoding/riichi-mahjong`
3. Select branch `feature/multiplayer` → **Apply**
4. Render reads `render.yaml` and deploys automatically

---

## What's Done

- [x] **Lobby UI** — room code entry, player list, Copy Code button, Start button
- [x] **`GameController` network mode** — subscribes to `NetworkManager` events instead of local `GameState`
- [x] **Player seat rotation** — each client sees themselves at the bottom
- [x] **Reconnection** — server holds state; client rejoins mid-game via UUID
- [x] **Deploy to Render** — public server, auto-deploys on push

## What's Next (Phase 2)

- [x] **Matchmaking** — `joinQueue`/`leaveQueue`; 4 players match instantly, 30 s fill timer adds CPUs
- [x] **Dora indicators** — sent in `handDealt`, `meldDeclared`, and rinshan `tileDrawn`
- [x] **Temporary furiten** — server sends `furitenChanged`; client clears it on next draw
- [x] **Server hardening** — claim-window authorization (per-seat eligibility, all-respond
      resolution honouring simultaneous human ron), tsumo seat validation, message size/rate
      limits, serialized sends, input sanitization, room cap, double-start guard
- [x] **Persistent accounts** — optional username/password login with lifetime stats

## Accounts

Accounts are optional — guests play exactly as before. Signing in gives a persistent
name, cross-device reconnection, and lifetime stats (games played / won / total points),
which the server records automatically at game over.

- Passwords: PBKDF2-SHA256 (100k iterations), never stored in plain text.
- Sessions: HMAC-signed tokens (30-day expiry) saved in the client's `settings.cfg`.
  Tokens carry a per-account version — changing or resetting the password bumps it,
  instantly revoking every previously issued token on all devices.
- Password reset: optional recovery email + 6-digit code (hashed at rest, 15-minute
  expiry, 5 attempts max, 60 s resend cooldown, no username enumeration).
- Storage: Postgres — set `DATABASE_URL` (Supabase/Neon URI or Npgsql keyword string).
  The schema is created/migrated automatically on boot. A `steam_id` column is
  reserved for a future Steam build (session-ticket auth, no passwords).
- **Outage recovery:** the store is wrapped in `ResilientAccountStore`, which
  retries the connection on demand (at most once every 15 s) and marks itself
  unhealthy if an operation fails mid-life. A paused free-tier database — Supabase
  pauses after ~7 days idle — therefore heals itself as soon as it wakes, with no
  server restart. While it is down, guests play normally and signed-in players are
  seated as guests rather than being refused; account actions report
  "temporarily unavailable".

### Server environment variables

| Variable | Purpose |
|---|---|
| `DATABASE_URL` | Postgres connection (URI or keyword form). Unset = guest-only mode. `memory` = non-persistent in-memory store (testing). A database that is unreachable at boot does **not** disable accounts permanently — the store reconnects automatically on demand (see below). |
| `TOKEN_SIGNING_KEY` | Secret for session-token HMAC. Unset = random per-boot key (logins won't survive restarts). |
| `RESEND_API_KEY` | Resend API key — enables password-reset emails. |
| `EMAIL_FROM` | Sender address for reset emails (default `onboarding@resend.dev`). |
| `EMAIL_MODE` | `console` = dev sender that prints mails to the server log. |

### Enabling accounts on Render

1. Create a free Postgres database (e.g. [supabase.com](https://supabase.com) or [neon.tech](https://neon.tech)) and copy its connection URI.
2. Render dashboard → the service → **Environment** → add `DATABASE_URL` = the URI
   and `TOKEN_SIGNING_KEY` = any long random string (e.g. `openssl rand -hex 32`).
3. Redeploy. The startup log shows `[Auth] Account store ready.` when connected.
