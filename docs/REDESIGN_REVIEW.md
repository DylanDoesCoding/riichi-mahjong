# Dolo redesign — visual review findings

Review of the ten-pass redesign (PR #17) against
`C:\Dev\Riichi mahjong redesign\design_handoff_dolo_ui\README.md`.

Every item below is a *visual* or *behavioural* defect that only shows up when
the client is actually rendered and played — each one passed the headless smoke
check and the 476 unit tests.

## Status — updated 2026-08-18 (branch `fix/redesign-review-p0-p1`)

**Done and verified** against the rendered screens via `ReviewShots` / `AutoPlay`:

- **P0.1** rivers clip discards → `TileNode` empty styleboxes; `AutoPlay` now logs
  `held == visible` everywhere (incl. `held=18`). Also fixes P0.2 (discards small).
- **P1 3–7** all five: lobby now themed (3), scoring card fills its width (4),
  overlays redraw so the furiten hatch renders (5), `user://` pinned back to the
  original path (6), results rail fits 1920 (7).
- **P2 8–11** the design calls: felt is one quiet tone, only the local player's
  wedge tints (8); tile backs recoloured red→rail-brown in the art (9); call
  buttons re-tinted into the Dolo palette (10); Settings is a two-column card,
  no scroll (11).
- **P3 23** default prop `coffee` now has art (Dylan added coffee + teapot);
  wired into `CosmeticVisuals`, `coffee.png` background cleaned up.
- Plus: dora corner wedge made proportional + outlined (Dylan's "clearer dora"),
  and the **"Next Hand" button double-caption** bug found and fixed.

**Not committed to `master` yet** — awaiting PR. **Remaining: the rest of P3**
(items 12–22, 24–32). Test backlog captured in [`TEST_PLAN.md`](TEST_PLAN.md),
scheduled for after this PR.

---

## How this was produced

Two development harnesses, both committed under `tools/`:

| Harness | Scene | What it does |
|---|---|---|
| `ReviewShots.cs` | `Scenes/ReviewShots.tscn` | Poses all ten screens at 1920 x 1080 and saves a PNG of each. Screens that are minutes of play away (scoring, draw, results) are staged with representative data. |
| `AutoPlay.cs` | `Scenes/AutoPlay.tscn` | Plays a complete solo game by pressing real `TileNode`s and real HUD buttons, capturing at hand ends and logging per-seat river occupancy against what the river can actually show. |

```bash
Godot --path . --resolution 1920x1080 res://Scenes/ReviewShots.tscn
Godot --path . --resolution 1920x1080 res://Scenes/AutoPlay.tscn
```

Output lands in `user://review/` — on Windows,
`%APPDATA%\Godot\app_userdata\Dolo Mahjong\review\`.

These exist because the repo has no CI and no visual tests, and the headless
smoke check (`scripts/smoke.sh`) only proves a scene *constructs*. Every finding
below passed that check.

`AutoPlay` was run twice, playing **14 hands in total** through wins, exhaustive
draws, claim windows and hand transitions with no hang and no script error. The
river clipping below reproduced identically in both runs.

Evidence committed under `docs/review/`:

| File | Shows |
|---|---|
| `rivers-clipped.png` | rivers at their fullest — four columns where the design specifies six |
| `table.png` | the table at the start of a hand — four-colour wedges, oversized red backs |
| `lobby-unthemed.png` | the lobby with every theme variation silently no-ops |
| `scoring-collapsed.png` | the scoring card squeezed into its left third |
| `autoplay.log` | the per-seat river occupancy measurements |

---

## P0 — the two Dylan reported

### 1. Rivers silently drop discards (capacity 16, not 24)

Measured across a full game by `AutoPlay`:

```
wall=4   seat0: held=16 visible=16   seat2: held=17 visible=16  CLIPPED
wall=0   seat0: held=17 visible=16  CLIPPED   seat1: held=17 visible=16  CLIPPED
                seat2: held=18 visible=16  CLIPPED   seat3: held=17 visible=16  CLIPPED
```

By an exhaustive draw **every seat loses one to two discards**, with no
indication. The river is `ClipContents = true`, so they just vanish — which
matters for play, not only for looks: a player counting another player's river
to read their hand is reading a river that is lying to them.

**Root cause.** The geometry is right on paper: a 178 x 158 river with 28 x 38
tiles at 2px separation fits exactly 6 columns x 4 rows = 24. It only fits 16
because **`TileNode` is a `Button`, and the shared theme gives every `Button` a
stylebox with 20px horizontal and 10px vertical content margins**
(`DoloStyles.SetButtonPadding`, applied through `DoloTheme.BuildButtons`). A
Button's minimum size includes its stylebox margins, so each river tile's
minimum width becomes 40px rather than 28px:

- columns: `(178 + 2) / (40 + 2)` = **4**, not 6
- rows: `(158 + 2) / (38 + 2)` = 4
- capacity: **4 x 4 = 16** — exactly what was measured

`auto-rivers-full.png` confirms it visually: every river renders **four tiles
across**, where the design specifies six.

**Fix.** Give `TileNode` styleboxes with no content margins — either its own
theme type variation, or explicit `StyleBoxEmpty` overrides in `TileNode._Ready`.
`Flat = true` is already set but only affects drawing, not minimum size. Re-run
`AutoPlay` afterwards; the log should show `held == visible` at `wall=0`.

**But restoring 6 columns is not enough on its own.** The README expects
"desktop's 18" tiles (6 x 3). A hand played to exhaustion produces **17–18
discards per seat**, and a riichi declaration rotates a tile to 38px wide, eating
a further column in its row. So the spec's own capacity is marginal by one or two
tiles in exactly the situation where a full river matters most. Sizing the river
for ~22 (6 x 4, which the 158px height already allows once the padding is fixed)
gives it real headroom.

### 2. Discards look too small

Two separate causes, and the first is the one that actually reads as "small":

- The same 40px minimum width above means 28px of artwork sits in a 40px cell,
  so the tiles look small *and* oddly far apart. Fixing P0.1 fixes most of this.
- 28 x 38 is the design's own figure, but it was specified for a river that
  showed 6 across. If the river keeps 6 columns after the padding fix and still
  reads small, the honest options are a larger river rect or larger tiles —
  a decision for Dylan, not a bug.

---

## P1 — broken, one obvious fix each

### 3. The entire lobby is unthemed

`LobbyController` never calls `DoloTheme.Apply`. `HUD`, `MainMenu`,
`CosmeticsScreen` and `ResultsScreen` all do; the lobby does not, so every
`ThemeTypeVariation` on that screen silently resolves to nothing.

Most visible consequence: **Quick Play, the primary action, renders as a
near-invisible dark ghost** instead of brass. Anything styled by direct
`DoloStyles` calls (the identity strip, the fields) still looks right, which is
why it is easy to miss.

**Fix.** One line in `LobbyController._Ready`.

### 4. Scoring card collapses into its left third

`_scoringPanel` is a `Panel` (not a `PanelContainer`) with an unanchored
`VBoxContainer` child, so the column shrinks to its minimum width and overflows
vertically. Consequences: ~half the 740px card is empty, the 38px yaku rows are
squeezed to ~20px, and the "Next Hand" button clips to "Next Han". The draw
layout escapes it only because its rows are naturally wider.

**Fix.** Make it a `PanelContainer`, or anchor the column `FullRect` with the
card's padding as offsets.

### 5. Tile overlays never redraw after layout

None of `DoraCornerWedge`, `HatchOverlay`, `DashedRing` or `CountdownRing`
connect `Resized += QueueRedraw`, so each draws once at size 0 and never again.

**The furiten hatch therefore never appears at all.** The dora corner wedge only
works by accident: the dora glow is re-applied on every HUD update, which
happens after layout.

**Fix.** Connect `Resized` in each overlay's `_Ready`, as `DoloIconRect`,
`PointsChart` and `TableFelt` already do.

### 6. `user://` moved — existing players lose everything

Setting `config/name="Dolo Mahjong"` repointed the user directory from
`app_userdata/RiichiMahjong` to `app_userdata/Dolo Mahjong`. On upgrade every
existing player loses their settings, their persistent reconnection UUID and
their saved login.

**Fix.** Either set `config/custom_user_dir_name="RiichiMahjong"` in
`project.godot`, or migrate `settings.cfg` across on first run. This is the only
finding with consequences outside the client.

### 7. Results right rail overflows the viewport

"Rematch" and "Menu" run past x=1920 and clip. The chart card's 760px minimum
width pushes the rail wider than the column the page gives it.

---

## P2 — design contradictions to resolve (Dylan's call)

### 8. The felt is a four-colour pinwheel — and the mockup already ruled on this

Pass 10 says the four per-seat tints collapse to one quiet tone. Pass 04 gives
every player — including CPU seats — a cosmetic surface. Pass 04 currently wins
globally, so a solo game renders oxblood / slate / tatami / green wedges.

This is **not** an open question: `Dolo Table.dc.html` states the reasoning
outright in the colourblind pass —

> Dropping the four seat tints to one felt tone also removes the pass 02 problem
> where a wedge tint competed with a dora border for the same attention. The felt
> goes quiet and the tiles carry the state.

So the current build has reintroduced the exact problem pass 10 existed to
remove, and it is why the dora borders and the gold corner wedges read weakly
against the table.

**Resolution:** CPU seats sit on the shared quiet felt. Only a human's chosen
surface tints their own wedge, and even then it wants to be far closer to
`#2b3a2a` than the current swatches. Worth raising with Dylan: the cosmetics
"wedge surface" slot is in genuine tension with pass 10, and the customisation
may belong in the nameplate frame, the prop and the emblem rather than in the
felt colour at all.

### 9. Opponent tile backs are bright red

`Assets/Tiles/.../Regular/Back.svg` is `#ff3737` / `#a53c3c` / `#822600`. The
design says to use the real back "knocked back to 0.62/0.52/0.46", but multiply
blending a red by that is still red, so three hands of vivid red dominate the
screen. The design's premise does not survive this particular art.

**Options:** a much heavier knock-back toward the rail brown; recolour the back
art; or reinstate a styled panel (which the design asked to delete) in the Dolo
palette.

### 10. Call buttons kept the old palette

RON / PON / CHI / KAN / PASS still carry the pre-redesign cobalt, magenta and
orange. The handoff does say the five hues are deliberate — but *these* hues are
from the old palette and read as pasted in from another application. They want
re-tinting inside the Dolo palette while staying mutually distinguishable.

### 11. Settings holds more than its card

The card is the design's 530 x 590, but the content is roughly 690px tall, so
the body scrolls and cuts off mid-section. Either the card grows, the content
splits into two columns, or some rows move elsewhere.

---

## Tile highlight states — checked against the mockup

Imported from the Claude Design project
(`3099062e-7488-43d5-908e-32819075f560`, `Dolo Table.dc.html`) via the
`DesignSync` MCP. The colourblind pass in that file is the authoritative spec for
these six states, and it closes with the rule they all serve:

> Hue stays where it is — the states keep their existing colours for players who
> can see them. What changes is that no state depends on colour to be identified.

| State | Mockup requires | Built | Verdict |
|---|---|---|---|
| **Dora** | gold border + corner wedge | gold border + `DoraCornerWedge` | **Correct** — verified rendering on hand tiles and on the scoring card's dora rows |
| **Hover match** | non-hue cue (pass 02) | dashed ring (`DashedRing`) | **Correct in design, unverified in play** — needs the P1.5 `Resized` fix before it can be trusted |
| **Riichi** | rotated discard + white stick on the felt | both implemented | **Rotation confirmed; the felt stick was not observed** in a full game despite CPU riichi declarations — needs checking against `TableFelt.SetRiichiStick` wiring |
| **Furiten** | strike through the word + 135° hatch across the wait tiles | strike implemented, hatch implemented | **Half broken** — the strike renders, the hatch never does (P1.5) |
| **Dead wait** | count (`0 left`) + face dropped for an outline | both implemented | **Correct** — confirmed in the waits popup |
| **Seat wind** | "the wind character … moves onto the seat plate **at full size**" | 20 x 27 tile image + a letter | **Deviates** — the glyph is small and secondary rather than the primary label |

Two follow-ups from this table:

- **Seat wind (item 29 below).** The mockup wants the wind character at full size
  as *the* label. The build renders a 20 x 27 tile thumbnail next to a one-letter
  abbreviation, which is legible but reads as decoration. Drawing the same tile
  SVG much larger satisfies the spec without needing a CJK font, which was the
  original reason for the substitution.
- **Riichi stick (item 30 below).** Worth an explicit test: declare riichi and
  confirm a stick appears on the felt for that seat, since a full auto-played
  game with multiple CPU riichi declarations never showed one.

The mockup also specifies the **river backdrop hairline at
`rgba(206,182,120,0.45)`**; the build uses `HairlineFaint` (0.14), which is why
the river pads read as barely-there smudges rather than as defined places on the
table.

---

## P3 — polish

| # | Finding |
|---|---|
| 12 | **Top opponent uses full-size hand tiles.** `HandDisplay.IsSideHand` covers `Left`/`Right` only, so `Top` gets 70 x 100 desktop hand cells instead of 42 x 52. |
| 13 | **Side hands render as clipped slivers** at the screen edges — `SetSideways` rotates the artwork but the layout box stays 42px wide, so the rotated art overflows its cell. |
| 14 | **Wordmark renders light, not semibold.** `DoloWordmark.WordmarkFont` nests a `FontVariation` (for tracking) inside `DoloTheme.SansSemiBold` (itself a `FontVariation` for weight); the outer one drops the weight axis. Set both `SpacingGlyph` and the `wght` axis on a single variation over the base `FontFile`. |
| 15 | **Wordmark lockup sits ~78px left of centre** — it is drawn from x=0 in a control wider than the drawn content, so it does not centre against the button column. |
| 16 | **Globe icon reads as a back-arrow in a circle**; the inner meridian arc is wrong. |
| 17 | **Gear icon reads as a diamond** at 18px — ring too small, teeth detached. |
| 18 | **Tile icon reads as a lined document**; three internal rules is a list, not a tile. |
| 19 | **Waits popup never restyled** — still the pre-redesign green-bordered panel, and the "3 left / 1 left / 0 left" labels collide because they are wider than the 30px tiles above them. |
| 20 | **Countdown bar never restyled** — old yellow-green, full width, unthemed label. |
| 21 | **Table `?` and `← Menu` buttons never restyled.** |
| 22 | **Cosmetics live preview is wrong** — it draws the whole four-wedge felt scaled into a 420px box, with the prop pocket at desktop offsets, instead of the player's own wedge at 1:1. It is the centrepiece of that screen. |
| 23 | **Default prop is `coffee`, which has no art** — every new player's table ships showing a dashed placeholder. Default to `none`, or make one of the two finished props free. |
| 24 | **Centre-plaque dora indicator renders as a blank cream tile.** |
| 25 | **Results first-place row balloons to ~430px** — `SizeFlagsVertical = ExpandFill` plus a 1.35 stretch ratio against a 150px minimum. |
| 26 | **Server status dot renders as a diamond**, not a circle. |
| 27 | Menu footer says `GODOT 4.6.2-stable (official)`; the `(official)` is noise. |
| 28 | Menu buttons visually merge — their shadows fill the 4px gap, so three buttons read as one segmented control. |
| 29 | **Seat wind glyph is too small.** The mockup wants the wind character at full size on the plate; the build shows a 20 x 27 thumbnail beside a letter. |
| 30 | **Riichi felt stick not observed in play** despite CPU riichi declarations across a full game. Check the `HUD.ShowRiichiStick` -> `TableFelt.SetRiichiStick` path and the stick's felt offsets. |
| 31 | **River backdrop hairline too faint** — build uses 0.14 alpha, the mockup specifies `rgba(206,182,120,0.45)`. |
| 32 | One river tile rendered as a blank cream face in `auto-rivers-full.png` (CPU 1, last discard). Possibly a load race on the tile texture — worth confirming it is not the same fault as the centre-plaque dora blank. |

---

## What works, and should not be disturbed

- **The results screen.** The chart separates its four lines three ways — stroke
  width, dash pattern, and a label at each line's own end — so it survives
  greyscale and needs no legend. Placement arithmetic reconciles on screen:
  `+19.1 · uma +20 · oka +20` → `+59.1`.
- **The draw layout** correctly *removes* the yaku and winning-hand regions
  rather than emptying them, which was pass 07's whole point.
- **The payout block** showing its arithmetic rather than only a total.
- **Dora**: gold border plus corner wedge, both rendering on hand tiles.
- **Dead waits**: face dropped for a brass outline, with the count spelled out.
- **Cosmetics swatches**, the dashed placeholders for unmade props, and the
  locked markers.
- **Room codes** in mono at wide tracking.
- **Fonts, card / inset / field styling, and the brass-primary hierarchy.**
- **The full-game loop is sound** — `AutoPlay` played nine consecutive hands,
  through wins, exhaustive draws, claim windows and hand transitions, with no
  hang and no script error.

---

## Suggested order for next session

1. P0.1 (`TileNode` stylebox padding) — one fix, resolves both of Dylan's
   reports, and re-running `AutoPlay` proves it.
2. P1 items 3–7 — all small, all unambiguous.
3. Put items 8–11 to Dylan before touching them.
4. P3 in whatever order suits; 12, 13, 19, 20, 21 and 22 are the visible ones.
