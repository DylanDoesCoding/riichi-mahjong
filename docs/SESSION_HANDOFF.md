# Session handoff — 2026-08-18

Entry point for the next session. Read this, then
[`REDESIGN_REVIEW.md`](REDESIGN_REVIEW.md) (the work list, now with a status
block at the top) and [`TEST_PLAN.md`](TEST_PLAN.md) (the test backlog).

## State

| | |
|---|---|
| Branch | `fix/redesign-review-p0-p1`, off `master` after PR #17 merged. **Committed, not pushed, no PR yet.** |
| Tests | 476 pass, 0 fail (unchanged — the fixes are all UI; the test project compiles Core + AI only) |
| Builds | client + tests clean, 0 warnings / 0 errors |
| Verified by | `ReviewShots` (all ten screens) and `AutoPlay` (full solo game) re-run after each change |
| Uncommitted (Dylan's, untouched) | stashed on the old branch: `export_presets.cfg`, `Assets/beer.png`, `Assets/cig.png` — `git stash list` |

## What this session did

Worked the review list from the top. **All of P0, P1 and P2 are done**, plus two
things that came up mid-session. Details and the before/after reasoning are in
`REDESIGN_REVIEW.md`; the short version:

- **P0.1 — the headline bug.** `TileNode` is a `Button` and inherited the theme's
  20×10 content margins, inflating a 28px river tile to a 40px cell → 4 columns,
  capacity 16, silent clipping. Fixed with `StyleBoxEmpty` overrides in
  `TileNode._Ready` (`ClearButtonPadding`). `AutoPlay` now logs `held == visible`
  at every wall count, `held=18` included. The 158px river already allows 6×4=24
  once the padding is gone, so no geometry change was needed.
- **P1 3–7.** Lobby calls `DoloTheme.Apply` now; scoring panel is a
  `PanelContainer`; the four tile overlays connect `Resized += QueueRedraw` (the
  furiten hatch was drawing once at size 0 and never again); `user://` pinned via
  `custom_user_dir_name` (see the gotcha below); results chart fills the rail
  instead of forcing a 760px minimum.
- **P2 8–11.** Design calls, made as design lead and grounded in the mockup:
  felt goes to one quiet tone with only the local player's wedge tinted (and that
  tint muted 35% toward the felt); tile-back art recoloured red→rail-brown at the
  source; call buttons re-tinted into the Dolo palette with a proper
  bordered/pressed face; Settings split into two columns so nothing scrolls.
- **Mid-session, from Dylan:** dora corner wedge made proportional + dark-outlined
  ("clearer what's dora"); coffee/teapot prop art wired in (resolves P3-23), and
  `coffee.png` had a baked-in transparency checkerboard + white sticker halo that
  I stripped (flood-fill from the borders + an enclosed-checker pass for the
  handle hole). Teapot was already clean.
- **Found while fixing P0:** the "Next Hand" / "Play Again" button rendered a
  doubled caption ("NlexttHHamdd"). Root cause: it is an `IconButton` whose label
  lives in a child `Label`, but two call sites also set `button.Text`, so Godot
  drew the caption twice. Added `DoloWidgets.SetIconButtonText` and routed both
  sites through it.

## Two gotchas worth remembering

- **`use_custom_user_dir` drops the `Godot/app_userdata` prefix.** Setting it with
  a bare `"RiichiMahjong"` lands in `%APPDATA%/RiichiMahjong` — a *new third*
  folder, not the original. The original default path had to be spelled out in
  full: `custom_user_dir_name="Godot/app_userdata/RiichiMahjong"` (the field
  preserves path separators). Verified: `ReviewShots` now writes to the original
  location. Harness output therefore lives at
  `%APPDATA%/Godot/app_userdata/RiichiMahjong/review/`.
- **A multiply modulate cannot turn a colour into a different hue** — it only
  darkens per channel. The red tile backs had to be recoloured in the SVG art,
  not knocked back in code.

## Next session

1. **Open the PR** for `fix/redesign-review-p0-p1` (nothing pushed yet).
2. **P3 polish** — the remaining review items 12–22 and 24–32. Most visible:
   12 (top opponent full-size tiles), 13 (side hands clipped), 19 (waits popup
   unstyled), 20 (countdown bar unstyled), 21 (table `?`/`Menu` buttons), 22
   (cosmetics live preview). Item 20's old yellow-green bar is visible in
   `06-claim-window.png`.
3. **After the PR merges:** work `TEST_PLAN.md` — section A (cosmetics catalogue
   tests) is pure additions; B and C carry a small Core refactor each.

## Harnesses (unchanged)

```bash
Godot --path . --resolution 1920x1080 res://Scenes/ReviewShots.tscn   # poses all ten screens
Godot --path . --resolution 1920x1080 res://Scenes/AutoPlay.tscn      # plays a full solo game
```

Godot lives at
`C:\Users\Dylan\Documents\godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe`
(not on `PATH`). Output goes to `user://review/` — now the RiichiMahjong path
above. A full `AutoPlay` game runs past 7 minutes; it writes screenshots and the
occupancy log as it goes, so poll for the file rather than waiting for exit.
