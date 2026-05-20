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
│              ASP.NET Core Server (Fly.io)                 │
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
│       └── ServerMessage.cs         # Server → client message types
├── src/
│   └── UI/
│       └── NetworkManager.cs        # Godot WebSocketPeer client
│                                     # Fires same events as GameState
└── ...existing single-player files
```

---

## Message Protocol

All messages are JSON over WebSocket. The `type` field is the discriminator.

### Client → Server

| type | Fields | When |
|---|---|---|
| `createRoom` | `displayName` | Host creates a new room |
| `joinRoom` | `code`, `displayName` | Guest joins by room code |
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

## Running the Server Locally

```bash
cd server
dotnet run
# Server starts on http://localhost:5000
# WebSocket endpoint: ws://localhost:5000/ws
```

Connect the Godot client to `ws://localhost:5000/ws` for local testing.

---

## Deploying to Fly.io (Free Tier)

```bash
# Install Fly CLI (once)
winget install Fly.flyctl

# Login
fly auth login

# From the server/ directory
cd server
fly launch          # Creates fly.toml, provisions a free VM
fly deploy          # Builds Docker image and deploys
```

The server uses less than 1 MB/month of bandwidth per active game.  
Fly.io's free tier (3 shared VMs) is sufficient for development and small-scale play.

---

## What's Next

- [ ] **Lobby UI scene** — room code entry, player list, Copy Code button, Start button
- [ ] **`GameController` network mode** — subscribe to `NetworkManager` events instead of local `GameState`
- [ ] **Player seat rotation** — each client sees themselves at the bottom
- [ ] **Reconnection** — server holds state; client can rejoin mid-game
- [ ] **Deploy to Fly.io** — public server URL
- [ ] **Matchmaking** (Phase 2) — server auto-creates rooms for solo queue players
