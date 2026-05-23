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
- Network-local state: `_netNames`, `_netScores`, `_netTileCounts`, `_netMyTiles`, `_netMelds[]`, `_netMyDiscards[]`, etc.

### Reconnection
- Client UUID (`GameSettings.PlayerUuid`) generated once per app session
- On disconnect → reconnect overlay shown → sends `rejoinRoom` with UUID
- Server sends `gameStateSnapshot` with full board state
- Client replays snapshot to restore full visual state

### Settings Persistence
- `GameSettings.Load()` / `Save()` using Godot `ConfigFile` → `user://settings.cfg`
- Saves: player name, server URL, black tiles, music/sfx volumes
- `Load()` called in `NetworkManager._Ready()` (autoload, runs before any scene)
- `Save()` called from `MainMenu.CloseAndSave()` (Settings screen) and `LobbyController.ValidateAndConnect()`

### Sound System
- `SoundManager` autoload singleton (`src/UI/SoundManager.cs`)
- All SFX synthesised as PCM in code — no audio assets required
- Writes WAV files to `user://` on first launch, loads them as `AudioStream`
- `AudioStreamWAV` is not exported as a C# type in GodotSharp NuGet — must use file-based workaround
- API: `SoundManager.Instance?.Play(Sound.TileDiscard)` etc.
- Sounds: `TileDiscard`, `TileDraw`, `Riichi`, `WinTsumo`, `WinRon`, `ExhaustiveDraw`, `GameOver`, `ButtonClick`

### Furiten Indicator
- `HUD.SetFuriten(bool isFuriten, bool isPermanent)` — badge on human's score panel
- Red = permanent furiten (own discard / riichi miss), orange = temporary (missed opponent's discard)
- Only shown while in tenpai with no drawn tile (the only phase where ron is actually blocked)
- Local mode: driven directly by `FuritenTracker` state via `HudUpdateLocal()` helper
- Network mode: permanent furiten computed client-side by comparing current waits against `_netMyDiscards`

### Action Countdown Bar (Network Mode)
- 16px bar above action buttons; drains green → yellow → red over 20 seconds
- `HUD.StartCountdown` / `StopCountdown` / `UpdateCountdown` driven by `GameController._Process`
- Auto-discard (drawn tile) on expiry during discard turn; auto-pass on expiry during claim window
- Stopped immediately when the player acts (any button press or tile click)

---

## Important Files to Read First

When picking up a task, read these files for context:

```
src/UI/GameController.cs       # Core game logic, dual-mode handling
src/UI/HUD.cs                  # In-game HUD — scoring panels, furiten, countdown bar
src/UI/NetworkManager.cs       # All network events and message dispatch
src/UI/SoundManager.cs         # Procedural SFX synthesis, autoload singleton
src/UI/HandDisplay.cs          # Tile display, deal animation, pop-in animation
src/UI/MainMenu.cs             # Main menu + settings screen (name, URL, volumes)
src/UI/LobbyController.cs      # Lobby UI built entirely in code
src/UI/GameSettings.cs         # Settings with Load/Save
server/GameRoom.cs             # Server game loop and reconnection
server/Program.cs              # WebSocket endpoint routing
```

---

## What's Done

### Multiplayer & Core
- [x] Lobby UI — name entry, create/join room, player list, copy code
- [x] GameController network mode — full dual-mode support
- [x] Player seat rotation — local player always at bottom
- [x] Reconnection — UUID-based mid-game rejoin
- [x] Deployed to Render — auto-deploys on push to `feature/multiplayer`

### UI & Polish (Phase 2)
- [x] Round-end summary screen — ranked standings, yaku list, uma-adjusted net scores
- [x] Procedural SFX — 8 synthesised sounds via PCM WAV pipeline (no audio assets)
- [x] Tile deal animation — staggered scale+fade on hand deal; pop-in on single draw
- [x] Settings screen — player name + server URL fields, all settings persisted on save
- [x] Action countdown bar — 20s timer above buttons (network mode), auto-pass/discard on expiry
- [x] Furiten indicator — red/orange badge on score panel when ron is blocked

## What's Next

- [ ] Button click sounds — wire `Sound.ButtonClick` to HUD and main menu buttons
- [ ] Wall counter — remaining tiles display near centre panel
- [ ] CPU AI improvements — discard heuristics, danger tile awareness
- [ ] Lobby polish — ready state, room code copy button, player list
- [ ] Matchmaking — auto-create/join rooms for solo queue
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
