# Claude Session Context — Riichi Mahjong

Paste this file at the start of a new conversation to resume work on this project.

---

## Project Overview

**Riichi Mahjong** — a Godot 4 C# game with single-player (vs CPU) and online multiplayer.  
Repo: `DylanDoesCoding/riichi-mahjong`  
Active branch: `feature/multiplayer`  
Local path: `C:\Dev\riichi-mahjong`

---

## Tech Stack

| Layer | Technology |
|---|---|
| Game engine | Godot 4.6.2, C#, .NET 8 |
| Shared library | `shared/RiichiMahjong.Core.csproj` — Core + AI, no Godot deps |
| Multiplayer server | ASP.NET Core 8 WebSocket server (`server/`) |
| Server hosting | Render.com (free tier, auto-deploys on push) |
| Live server URL | `wss://riichi-mahjong-server.onrender.com/ws` |
| Health check | `https://riichi-mahjong-server.onrender.com/health` |

---

## Project Structure

```
riichi-mahjong/
├── shared/
│   └── RiichiMahjong.Core.csproj   # Shared lib (Core + AI source via Compile globs)
│                                     # src/Core/**/*.cs + src/AI/**/*.cs
├── server/
│   ├── Program.cs                   # ASP.NET Core startup, /ws endpoint
│   ├── RoomManager.cs               # Room creation/lookup
│   ├── GameRoom.cs                  # Game loop, AI, privacy filtering, reconnection
│   ├── PlayerConnection.cs          # WebSocket wrapper
│   └── Messages/
│       ├── ClientMessage.cs
│       └── ServerMessage.cs
├── src/
│   ├── Core/                        # Game logic (Tile, Hand, GameState, etc.)
│   ├── AI/                          # AIPlayer, FuritenTracker, etc.
│   └── UI/
│       ├── NetworkManager.cs        # Godot WebSocketPeer autoload, fires C# events
│       ├── GameController.cs        # Dual-mode: local vs network game
│       ├── LobbyController.cs       # Lobby UI (create/join room, player list)
│       ├── GameSettings.cs          # PlayerName, ServerUrl, PlayerUuid, Load/Save
│       └── HUD.cs                   # In-game HUD, scoring panels
├── Scenes/
│   ├── MainMenu.tscn
│   ├── Lobby.tscn
│   └── GameTable.tscn
├── Dockerfile                        # Multi-stage .NET 8 build, repo-root context
├── render.yaml                       # Render.com IaC (free plan, Docker)
├── MULTIPLAYER.md                    # Full multiplayer architecture docs
└── export_presets.cfg                # Windows + Android export presets
```

---

## Key Architecture

### Multiplayer — Server Authoritative
- Server owns all `GameState`; clients send actions, render results
- No client can see another player's hand tiles
- CPU AI runs server-side for empty seats (up to 3 CPU opponents)
- 8-second claim window for ron/pon/chi/kan before auto-resolving

### Dual-Mode GameController
- Detects mode in `_Ready()` via `NetworkManager.Instance?.LocalSeat >= 0`
- `_humanSeat`: `0` in local mode, server-assigned in network mode
- Seat rotation: `(globalSeat - _humanSeat + 4) % 4` maps server seats to display positions
- Network-local state: `_netNames`, `_netScores`, `_netTileCounts`, `_netMyTiles`, `_netMelds[]`, etc.

### Reconnection
- Client UUID (`GameSettings.PlayerUuid`) generated once per app session
- On disconnect → reconnect overlay shown → sends `rejoinRoom` with UUID
- Server sends `gameStateSnapshot` with full board state
- Client replays snapshot to restore full visual state

### Settings Persistence
- `GameSettings.Load()` / `Save()` using Godot `ConfigFile` → `user://settings.cfg`
- Saves: player name, server URL, black tiles, music/sfx volumes
- `Load()` called in `NetworkManager._Ready()` (autoload, runs before any scene)
- `Save()` called in `LobbyController.ValidateAndConnect()` on successful connect

---

## Important Files to Read First

When picking up a task, read these files for context:

```
src/UI/GameController.cs       # Core game logic, dual-mode handling
src/UI/NetworkManager.cs       # All network events and message dispatch
src/UI/LobbyController.cs      # Lobby UI built entirely in code
src/UI/GameSettings.cs         # Settings with Load/Save
server/GameRoom.cs             # Server game loop and reconnection
server/Program.cs              # WebSocket endpoint routing
```

---

## What's Done

- [x] Lobby UI — name entry, create/join room, player list, copy code
- [x] GameController network mode — full dual-mode support
- [x] Player seat rotation — local player always at bottom
- [x] Reconnection — UUID-based mid-game rejoin
- [x] Persistent display name — remembered between sessions
- [x] Deployed to Render — auto-deploys on push to `feature/multiplayer`

## What's Next (Phase 2)

- [ ] Matchmaking — auto-create rooms for solo queue
- [ ] Dora indicators — server doesn't send dora tiles in `handDealt` yet
- [ ] Persistent accounts — replace session UUIDs with real logins

---

## Common Commands

```powershell
# Run server locally
cd C:\Dev\riichi-mahjong\server
dotnet run
# WebSocket: ws://localhost:5000/ws  |  Health: http://localhost:5000/health

# Build Godot project (C# only, no Godot editor needed)
cd C:\Dev\riichi-mahjong
dotnet build RiichiMahjong.csproj

# Build server
dotnet build server/RiichiServer.csproj

# Redeploy to Render
git push  # Render auto-deploys on push to feature/multiplayer

# Export Windows build (run from Godot editor)
# Project → Export → Windows Desktop → Export All
# Output: export/windows/RiichiMahjongBeta.exe
```

---

## Render Deployment Notes

- Free tier spins down after 15 min idle — first connection cold-starts in ~30 sec
- Auto-deploys on every push to `feature/multiplayer` via Blueprint sync
- Blueprint name on Render dashboard: **RichiiMahjongDolo**
- To redeploy manually: push any commit to the branch

---

## Git Info

```
Branch:  feature/multiplayer
Remote:  https://github.com/DylanDoesCoding/riichi-mahjong.git
```
