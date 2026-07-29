# Dolo Mahjong — Client Design Handoff

**For:** a Claude design agent picking up the visual design of the game client.
**You have repo access — read the real code before designing.** Every layout number in
this document was read out of the source, but the source is the truth.

- **Local repo:** `C:\Dev\riichi-mahjong`
- **GitHub:** https://github.com/DylanDoesCoding/riichi-mahjong
- **Owner:** Dylan (indie dev, Godot 4 / C#)
- **Status:** feature-complete and live. This handoff is about *visual design*, not features.

---

## 1. The brief in one paragraph

The game plays well and is fully shipped — rules, online multiplayer, accounts,
leaderboard. What it does **not** have is an identity. Every screen is functional
programmer-art: flat dark navy cards, blue borders, emoji as icons. The job is to turn
it into **Dolo Mahjong** — a product that looks intentional — while respecting the fact
that the UI is built in C# code, not in Godot scenes. Be creative within that, but
prioritise two specific asks from Dylan (§6) over general prettiness.

---

## 2. Read these first

| File | Why it matters |
|---|---|
| `src/UI/HUD.cs` (~1900 lines) | **The game table.** Builds every table element in code: score panels, discard rivers, dora row, all overlays. The single most important file. |
| `src/UI/TileNode.cs` | One tile. All highlight states live here as stacked `Panel` overlays. |
| `src/UI/HandDisplay.cs` | A player's hand/melds; owns tile sizing per orientation. |
| `src/UI/LobbyController.cs` | Entire multiplayer lobby + account UI + leaderboard, all in code. |
| `src/UI/MainMenu.cs` + `Scenes/MainMenu.tscn` | The one screen that *is* scene-built. |
| `src/UI/GameController.cs` (~2200 lines) | Game flow; drives which UI appears when. Read for state transitions, not for layout. |
| `src/UI/SoundManager.cs` | Audio bus + SFX catalogue. |
| `MULTIPLAYER.md` | Architecture + full network protocol. Read before touching anything networked. |

---

## 3. Hard technical constraints

**The UI is built in code, not scenes.** This is the single biggest constraint. Only
`MainMenu.tscn` has real nodes; `Lobby.tscn` and `GameTable.tscn` are thin shells whose
scripts construct everything at runtime with `new Panel()`, `StyleBoxFlat`,
`AddThemeColorOverride`, and manual anchor/offset maths. **A design that assumes a
scene-based redesign is not implementable without a rewrite.** Either:
- work within it (specify colours, sizes, spacing as values to change in code), or
- explicitly propose the migration to `.tscn` + a Godot `Theme` resource as its own
  scoped piece of work, with a cost estimate.

Other constraints:

| Constraint | Value |
|---|---|
| Engine | Godot **4.6**, C# / .NET 8, `GL Compatibility` renderer (d3d12 on Windows) |
| Reference resolution | **1920 × 1080** (`viewport_width/height`) |
| Default window | 1024 × 576 |
| Stretch | `canvas_items`, aspect `expand` — layout must survive other aspect ratios |
| Main scene | `Scenes/IntroScreen.tscn` → MainMenu → Lobby / GameTable |
| Autoloads | `NetworkManager`, `SoundManager` (persist across scenes) |
| Tile art | `Assets/Tiles/riichi-mahjong-tiles-master/{Regular,Black}/*.svg` — 80 files each. The code loads the **SVGs**, not the `Export/*.png` copies. |
| Fonts | **None.** Everything is Godot's default font at per-widget `font_size` overrides. Adding a typeface is open and welcome. |
| Icons | **None.** All icons are emoji in label text (🀄 ⚡ 🏆 ⚙ ▶ ✕ ←). Fine to replace with real assets. |

---

## 4. Current screen inventory

### IntroScreen → MainMenu → (Lobby | GameTable)

**IntroScreen** (`Scenes/IntroScreen.tscn`, `src/UI/IntroScreen.cs`) — splash using
`Assets/splash.png` (559 KB), then auto-advances.

**MainMenu** (scene-built, so the easiest screen to restyle):
Background `ColorRect` `#141A26`, centred `VBoxContainer` with
title `🀄 RIICHI MAHJONG` *(← rename to Dolo)*, then buttons:
`Play vs CPU`, `🌐 Multiplayer`, `⚡ Quick Play`, a `Tile Theme` row
(`☀ Regular` / `🌙 Black`), `⚙ Options`, `Quit`.
`MainMenu.cs` additionally builds a **Settings overlay** in code (music/SFX sliders,
player name, server URL).

**Lobby** (`LobbyController.cs`, all code-built) — three panels swapped by visibility:
1. **Connect panel** (440 × 660 card): display name, server URL, an **Account** section
   (username/password, Sign In / Register / Forgot?, or "Signed in as X" + Manage +
   Sign Out), a **Manage** sub-panel (recovery email, change password), a **Forgot**
   sub-panel (reset code + new password), `⚡ Quick Play`, `＋ Create Room`,
   join-by-code row, status line, then `← Back to Menu` + `🏆 Leaderboard`.
   **This panel is doing far too much work — the strongest candidate for restructuring
   (tabs, or splitting account management into its own screen).**
2. **Waiting panel** (480 × 560): room code + Copy, four player slots, status,
   Start (host only), Leave. Doubles as the matchmaking "Searching…" view.
3. **Leaderboard panel** (540 × 600): header row + scrollable rows —
   🥇🥈🥉 then rank numbers, wins / games / win % / points, own row tinted green.

**GameTable** — see §5.

### Overlays (all in `HUD.cs`, all code-built)

| Overlay | Notes |
|---|---|
| Scoring panel | ~740 × 620 centred card: yaku list with han, Dora / **Red 5 (aka)** / Ura Dora rows, han+fu, payments, all-player standings, Next Hand / Menu |
| Ryuukyoku panel | ~600 × 400: exhaustive-draw tenpai reveal with waits |
| Win call | Full-screen flash + centred "TSUMO!" / "RON!" before the scoring panel |
| Yaku reference | `YakuReferenceOverlay.cs` — scrollable yaku list, opened from a table button |
| Countdown bar | 16 px bar above the action buttons, 20 s claim timer |
| Waits popup | Shows your waiting tiles (30 × 40 tiles) |
| Tile info line | Hover readout, e.g. `2 Sou — 1 visible on table, 2 unseen` |

---

## 5. The game table — exact current geometry

`Scenes/GameTable.tscn` provides only: a green background `ColorRect`
**`#1A4D26`** (`Color(0.10, 0.30, 0.15)`), four `HandDisplay` controls, and the `HUD`.

### Hand containers (from the scene file)

| Node | Anchor | Offsets | Tile size |
|---|---|---|---|
| `PlayerHand` (self, south) | bottom-wide | left 180, top −85, right −10, bottom −10 | 66 × 88 |
| `TopHand` (north) | top-wide | left 180, top 8, right −180, bottom 60 | 42 × 52 face-down |
| `LeftHand` (east) | centre-left | left 6, top −250, right 52, bottom 250 | 42 × 52 face-down |
| `RightHand` (west) | centre-right | left −52, top −250, right −6, bottom 250 | 42 × 52 face-down |

> ⚠️ **`PlayerHand` is 75 px tall but its tiles are 88 px** (and lift another 10 px when
> selected). Tiles overflow their container. Verify in-engine and fix as part of any
> table pass.

### Discard rivers — the four quadrants

Built in `HUD.BuildDiscardPools()`. Each is a `Control` (`ClipContents = true`) wrapping
an `HFlowContainer`, positioned by **offsets from screen centre** (`LayoutPreset.Center`),
holding 28 × 38 tiles with 2 px separation:

| Seat | Left | Top | Right | Bottom | Size |
|---|---|---|---|---|---|
| 0 — self (south) | −215 | 58 | 215 | 185 | **430 × 127** |
| 1 — right (west) | 120 | −80 | 285 | 80 | **165 × 160** |
| 2 — top (north) | −215 | −185 | 215 | −58 | **430 × 127** |
| 3 — left (east) | −285 | −80 | −120 | 80 | **165 × 160** |

Note the asymmetry: side rivers are portrait-ish (165 × 160) while top/bottom are wide
(430 × 127), so the same 20+ tiles wrap very differently per seat. Unifying this is
worth considering.

### Score panels (per seat)

`HUD.BuildScorePanels()`, min size 150 × 80, showing name / points / seat wind and a
hidden 60 × 8 riichi stick:

| Seat | Preset | Offsets (l, t, r, b) |
|---|---|---|
| 0 self | BottomLeft | 60, −105, 220, −10 |
| 1 right | CenterRight | −220, −45, −60, 45 |
| 2 top | TopRight | −220, 8, −60, 88 |
| 3 left | CenterLeft | 60, −45, 220, 45 |

### Centre panel

Round wind / honba counter / wall count / dora indicator row (up to 5 tiles at 30 × 40),
centred at offsets (−130, −68).

### Seat rotation

Every client renders itself at the bottom. Visual seat = `(globalSeat - mySeat + 4) % 4`.
**Any per-seat design must key off the *visual* index, not the server seat.**

### Tile highlight states (all in `TileNode.cs`, stacked `Panel` overlays)

| State | Current look |
|---|---|
| Selected | Gold fill 28 % + 3 px gold border, tile lifts 10 px |
| Riichi candidate | Green fill 22 % + border; non-candidates dimmed 45 % black |
| Claimable | Orange border, alpha pulsing 0.35 ↔ 1.0 every 0.45 s |
| **Live dora** | Gold border 2 px + 10 % fill (also red fives) |
| **Hover match** | Cyan fill 22 % + 3 px border on every matching tile in the rivers |
| Face-down | Solid dark blue `#264D8C`-ish panel (not art) |

---

## 6. Priority asks from Dylan

These two come **before** general visual polish.

### 6.1 Customisable per-seat quadrants ⭐

Each player should be able to customise **their own quadrant** of the table — the area
around their seat (score panel + river + nameplate region). Think of it as a personal
play mat.

Design questions to answer:
- **What is customisable?** Mat texture/colour, nameplate frame, river backdrop, a
  border motif, an avatar/emblem slot?
- **Who sees it?** Recommended: **everyone sees each player's own choice** in that
  player's quadrant — it's social and it shows off. But note each client rotates seats,
  so a customisation must travel with the *player*, not the screen position.
- **Where does it live?** Nothing in the current schema stores cosmetics. The `accounts`
  table (Postgres) and the `handDealt` / `gameStarted` protocol messages would both need
  a field. Flag this as server work; don't assume it exists.
- **Guests?** Guests have no account to persist a choice to — either local-only
  (`settings.cfg`, visible only to them) or accounts-only as a sign-in incentive.
- **Constraint:** the quadrant rects above are *tight* (side rivers only 165 × 160).
  Decorative framing must not eat tile space or push tiles under the score panels.

Deliver: a mockup of one customised table (4 different quadrant styles at once, so we
can see it not turn into noise), plus the list of customisable slots and what a "set"
contains.

### 6.2 Touch-accurate tile hit areas ⭐

**Tiles must be selectable reliably by touch** — Dylan called this out specifically and
it's the main blocker for the mobile target.

Current reality:
- Hand tiles are `Button`s at 66 × 88 px on a **1920-wide reference canvas**. With
  `canvas_items` stretch on a phone, that scales *down* — on a 1080-wide device it's
  ~37 px wide, **below the ~44 pt / 48 dp minimum touch target**.
- Hand tiles sit in an `HBoxContainer` with **4 px separation**, so there are dead gaps
  between tiles; a slightly-off tap hits nothing at all.
- Selection is **two-stage**: first tap selects and lifts the tile, second tap on the
  *same* tile discards. The lift moves the button rect, so the second tap must follow the
  tile upward — easy to mis-hit on touch.
- River / opponent tiles are deliberately non-interactive
  (`MouseFilter.Ignore`) — they don't need hit areas, but the **hover** feature (§ tile
  info) has *no touch equivalent* and needs one (long-press? tap-to-inspect mode?).

Design needs to specify: minimum on-screen tile size for touch, zero-gap hit areas with
visual-only gaps (or larger invisible hit rects), whether the two-stage select stays or
becomes drag-to-discard, and the touch replacement for hover-matching.

---

## 7. Feature areas to design (Dylan wants all four — phase them)

Sequenced by value-per-effort; each should be deliverable on its own.

1. **Animation & juice pass** — cheapest win, biggest "commercial" feeling. Riichi
   declaration flourish, win celebration, tile motion, sound-synced feedback. Deal
   animation, draw pop-in and a win flash already exist (`HandDisplay.StartDealAnimation`,
   `HUD.ShowWinCall`) — build on them rather than replacing.
2. **Profile, stats & achievements** — lifetime `gamesPlayed` / `gamesWon` /
   `totalPoints` are **already recorded per account** and the leaderboard already reads
   them, so a profile screen is mostly design work. Achievements would need new server
   storage.
3. **Tutorial / onboarding** — the biggest real barrier for new players and the top ask
   for a Steam release. Riichi is intimidating; there's a `YakuReferenceOverlay` but no
   first-run teaching. Consider an interactive first hand with prompts.
4. **Social: chat, emotes, friends** — most new server protocol of the four
   (new message types, moderation questions for chat). Emotes are the cheap subset;
   consider starting there.

---

## 8. Known rough edges (fair game to fix)

- **Emoji as icons** throughout — renders inconsistently across platforms; replace.
- **No typeface** — default Godot font everywhere.
- **The lobby connect panel is overloaded** (name + server URL + full account management
  + join + quick play + leaderboard, in one 440 × 660 card).
- **Server URL is exposed in the main UI** — fine for testing, wrong for a released game.
- **Face-down tiles are a flat coloured `Panel`**, not tile-back art (`Back.svg` exists
  but its `.import` conflicted with Godot's importer — see the comment in `TileNode.cs`).
- **`PlayerHand` container is shorter than its tiles** (§5).
- **Side rivers are a different shape from top/bottom rivers** (§5).
- **Melds are appended inline** after closed tiles in the same box — visually they read
  as part of the hand.
- **Music is a 31 MB WAV** (`Whispering_Bamboo_Garden…wav`) — should be OGG/MP3 for a
  shipped build. Only one track, plus 3 SFX files.
- **`export_presets.cfg` targets `DoloMahjongDemo.exe`** but `project.godot` still says
  `config/name="RiichiMahjong"` and the menu title still reads `RIICHI MAHJONG` — the
  rename is half-done.

---

## 9. Do not break these

- **Server protocol** (`MULTIPLAYER.md`). Any new data — cosmetics, emotes — is a
  protocol + server change, not a client-only change. Say so explicitly in the design.
- **Hand privacy.** The server sends each client only its own tiles. Never design
  something that implies seeing another player's hand.
- **No information advantage.** Dora glow and hover-matching only surface what is
  already publicly visible; keep any new affordance to that same standard, or it becomes
  an unfair-advantage issue in ranked play.
- **Both tile themes.** `Regular` and `Black` must both keep working
  (`GameSettings.UseBlackTiles`); design against both, and note that highlight colours
  need to read on both.
- **Guests must keep playing.** Sign-in is optional by design and must stay optional.

---

## 10. Open questions for Dylan

1. **Dolo identity** — is there existing branding (logo, palette, the meaning of "Dolo")
   or is that part of this work?
2. **Quadrant cosmetics** — unlockable (via achievements/wins) or free choice? That
   decides whether this needs a progression system.
3. **Mobile** — real touch build, or "must not break if resized"? Changes how hard the
   §6.2 constraints bite.
4. **Art budget** — commissioned assets/typeface, or design must stay within Godot
   primitives + the existing tile pack?
5. **Table metaphor** — keep the current flat top-down green felt, or move toward a
   perspective/3D-ish table? Big scope difference.

---

*Last updated: 2026-07-29. Repo state: all 14 PRs merged; master at the "self-healing
account store" merge. Layout values read from source at that commit — re-verify against
the code before implementing.*
