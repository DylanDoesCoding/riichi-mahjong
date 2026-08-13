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

> **Gap: there are no screenshots of the current game anywhere in this doc or the repo.**
> This is a *visual* brief with no images of what it's redesigning, which forces the
> designer to reconstruct the look from ~4000 lines of layout code. Before starting, either
> run the game and capture MainMenu / Lobby / GameTable mid-hand / scoring panel, or ask
> Dylan for them. Adding them to `docs/` would make this the single biggest improvement to
> this handoff.

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

**Dylan's steer (2026-08-13):** he wants to know *"what's customizable for each quadrant —
such as an ashtray, or a beer, or coffee."* Read that as the target feeling: the quadrant
should read as **a real person's spot at a real table**, with their stuff on it — not as a
recoloured rectangle. Props are the point, not decoration around the edge. See the art-budget
conflict in §10 before deciding how the props get made.

Design questions to answer:
- **What is customisable?** Propose a concrete slot list. Starting point, to be argued with:
  | Slot | Examples |
  |---|---|
  | Mat surface | felt colour/weave, wood grain, tatami |
  | Personal prop | ashtray, beer, coffee cup, teapot, snack bowl — Dylan's ask |
  | Nameplate frame | plain, brass, neon, carved |
  | River backdrop | subtle tint or pattern behind the discards |
  | Emblem | small badge/sigil in a corner |
  Say which slots are free and which are unlocked (the model is **mixed** — see §10).
- **Where does a prop physically go?** This is the hard part and the reason to answer it
  early: the quadrant rects are *tight* (side rivers only 165 × 160) and the layout is
  fixed (§5, and the flat top-down metaphor is confirmed). A prop must sit somewhere that
  is **never** occupied by tiles at maximum hand/river size — otherwise it either overlaps
  gameplay or forces a layout change that was explicitly ruled out. Identify the safe
  pockets first, then design props to fit them.
- **Who sees it?** Recommended: **everyone sees each player's own choice** in that
  player's quadrant — it's social and it shows off. But note each client rotates seats,
  so a customisation must travel with the *player*, not the screen position.
- **Where does it live?** Nothing in the current schema stores cosmetics. The `accounts`
  table (Postgres) and the `handDealt` / `gameStarted` protocol messages would both need
  a field. Flag this as server work; don't assume it exists. **Note:** RLS is now enabled
  on `public.accounts` with zero policies (server connects as `postgres`, which bypasses
  RLS) — adding a cosmetics column is fine, but do not add permissive RLS policies to
  "make it work".
- **Guests?** Guests have no account to persist a choice to — either local-only
  (`settings.cfg`, visible only to them) or accounts-only as a sign-in incentive. With the
  mixed unlock model, guests plausibly get the free set locally and unlocks require an
  account — which doubles as the sign-in incentive.
- **Constraint:** decorative framing must not eat tile space or push tiles under the score
  panels.

Deliver: a mockup of one customised table (4 different quadrant styles at once, so we
can see it not turn into noise), plus the list of customisable slots, which are free vs
unlocked, and what a "set" contains.

### 6.2 Touch-accurate tile hit areas ⭐

**Tiles must be selectable reliably by touch** — Dylan called this out specifically and
it's the main blocker for the mobile target.

> **Confirmed 2026-08-13: mobile is a real touch build, not "must not break if resized".**
> That makes this section a **hard blocker and the first deliverable**, ahead of §6.1 and
> ahead of all general polish. Nothing below is optional.

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
- **Colour must not be the only channel for rules-relevant information.** This follows
  directly from the rule above. Today the gold dora glow, the cyan hover-match highlight,
  and red-5 (aka) tiles all convey scoring-relevant state *purely through hue*. A
  red/green or blue/yellow colourblind player cannot read them, which is the same
  information asymmetry the previous bullet forbids — just applied to a subset of players.
  Every state that affects scoring needs a second channel: outline weight, a corner badge,
  a shape, a pattern. Check the palette against deuteranopia/protanopia/tritanopia, and
  check it **on both tile themes** (`Regular` and `Black`), since a highlight that reads on
  one may vanish on the other.
- **Both tile themes.** `Regular` and `Black` must both keep working
  (`GameSettings.UseBlackTiles`); design against both, and note that highlight colours
  need to read on both.
- **Guests must keep playing.** Sign-in is optional by design and must stay optional.

---

## 10. Decisions (answered by Dylan 2026-08-13)

**All five questions are now answered. Nothing in this doc is blocked on Dylan.**

| # | Question | Decision |
|---|---|---|
| 1 | Dolo identity | **Greenfield.** "Dolo" is Dylan's own gamertag; no existing brand assets. See below. |
| 2 | Quadrant cosmetics | **Mix — some free, some unlocked.** A free starter set plus unlockables. |
| 3 | Mobile | **Real touch build.** Phones/tablets are a shipping target. |
| 4 | Art budget | **Godot primitives + the existing tile pack** — no commissions. *(See the conflict below.)* |
| 5 | Table metaphor | **Keep the flat top-down felt and restyle it.** Same geometry, better execution. |

**What each decision means for scope:**

- **Mobile is real → §6.2 is a hard blocker, not a nice-to-have.** Minimum touch size,
  zero-gap hit areas, the two-stage select, and a touch replacement for hover-matching all
  have to be solved before this ships on a phone. Treat §6.2 as the first deliverable.
- **Mixed unlock model → the progression system is in scope.** A free-only model would have
  needed just a cosmetics field; "some unlocked" needs unlock criteria, server storage for
  what each account owns, and a UI to browse/equip. Cost is close to fully-unlockable — plan
  for it rather than discovering it later.
- **Flat top-down stays → every layout number in §5 remains valid.** No perspective rework,
  no forced `.tscn` migration. Lowest-risk path.

### ⚠ Unresolved conflict: art budget vs. the quadrant prop idea

Dylan's answer to Q4 was *"option 1, but ask for more details on what's customizable for
each quadrant — such as an ashtray, or a beer, or coffee."*

Those two halves pull against each other and the design must resolve it explicitly:

- **Option 1** means the design stays inside Godot primitives (`StyleBoxFlat`, rounded
  rects, borders, colour) plus the existing tile SVGs.
- **An ashtray, a beer, a coffee cup are illustrated objects.** They cannot be drawn
  convincingly with primitives, and there is currently **no icon set, no typeface, and no
  prop art** in the project (§3).

So the design should come back with one of:
1. **Abstract-primitive props** — the vibe delivered through colour, mat pattern, border
   motif and simple geometric emblems, no representational objects. Fits the budget as
   stated.
2. **Open-licence prop sprites** — a small set of free-licence 2D props (CC0/CC-BY). Costs
   nothing but sourcing time, and gets the literal ashtray/beer/coffee Dylan is picturing.
3. **A costed exception** — a short list of props worth commissioning, with a price, for
   Dylan to approve or decline.

**Recommend option 2**, and say so with examples. Dylan's instinct here is a good one: the
props are what turn "a colour swatch" into "someone's actual table", which is the whole
point of §6.1. Do not silently downgrade it to colour swatches.

### Q1: Dolo identity — answered 2026-08-13

**"Dolo" is Dylan's own username — the handle he uses across a lot of the games he plays.**

What that means for the design:

- **There is no existing brand to match.** No logo, no wordmark, no palette, no type
  choice. Identity is **greenfield and in scope** — you are creating it, not applying it.
- **It is a personal handle, not a studio or product name.** "Dolo Mahjong" reads as
  *Dolo's mahjong table*, the way a regular's name ends up on their seat at a club. That
  is a much warmer, more specific brief than a generic mahjong app, and it should push the
  identity toward personal and lived-in rather than corporate or sleek.
- **It dovetails with §6.1.** The personal-play-mat direction — your own mat, your own
  props, your ashtray and your coffee — *is* the brand expressed as a feature. Design the
  identity and the quadrant customisation as one idea, not two. If the game is named after
  a person's handle, then "make the table yours" is the thesis, and the cosmetics system
  is the product, not a bolt-on.
- **Unblocks the half-done rename.** `project.godot` (`config/name="RiichiMahjong"`) and
  the MainMenu title still say RIICHI MAHJONG, while `export_presets.cfg` already targets
  `DoloMahjongDemo.exe` (§8). Settling the wordmark lets that be finished in one pass —
  and note the title is currently `🀄 RIICHI MAHJONG`, an emoji standing in for a logo,
  which is exactly what the identity work should replace.

**Open sub-question for Dylan (nice-to-have, not blocking):** does "Dolo" carry a meaning
you want the identity to lean on (e.g. the "solo dolo" sense of going it alone), or is it
simply your handle with no story attached? Either is fine — it only changes whether the
brand has a concept to hang on or is purely a name and a look.

---

*Last updated: 2026-08-13 (Dylan's answers folded into §10, plus §6.1/§6.2 scope, the
accessibility constraint in §9, and the screenshots gap in §2).*

*Repo state: all 15 PRs merged; master `063b1be`. Layout values were re-verified against
source on 2026-08-13 and are accurate: `TileNode.cs:31-32` (66 × 88), `HandDisplay.cs:71`
(4 px hand separation), `HandDisplay.cs:30,32` (42 × 52 side tiles), `project.godot:27-33`
(1920 × 1080 reference, 1024 × 576 window). Still re-check against the code before
implementing.*
