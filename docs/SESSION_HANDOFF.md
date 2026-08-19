# Session handoff — 2026-08-18 (P3 polish)

Entry point for the next session. Read this, then
[`REDESIGN_REVIEW.md`](REDESIGN_REVIEW.md) (the work list, status block at the top)
and [`TEST_PLAN.md`](TEST_PLAN.md) (the test backlog).

## State

| | |
|---|---|
| Branch | `polish/redesign-review-p3`, off `master` after PR #18 merged. **Not committed / no PR yet** unless noted below. |
| Tests | 476 pass, 0 fail (the P3 work is all UI; the test project compiles Core + AI only) |
| Builds | client clean, 0 warnings / 0 errors; `smoke.sh` OK on all five scenes |
| Verified by | `ReviewShots` (all ten screens re-rendered and read back after each change) |
| PR #18 | `fix/redesign-review-p0-p1` (P0/P1/P2) **MERGED** to master, merge commit `028f7c4` |

## What this session did — the rest of P3 (items 12–22, 24–32)

All addressed; details and reasoning are in `REDESIGN_REVIEW.md`. The short version:

- **12/13 opponent hands.** `HandDisplay`: the top opponent now uses compact tiles like
  the sides (was full-size desktop cells). Side hands render as rotated *landscape* tiles
  clear of the screen edge — the 90° rotation was silently never applied because `Resized`
  fired before its handler connected; `TileNode._Ready` now applies it explicitly, and the
  side cell is sized to the rotated footprint.
- **14/15 wordmark.** New `DoloTheme.SansTracked(weight, spacing)` carries the semibold
  weight *and* the tracking on one `FontVariation` (nesting one variation in another dropped
  the weight). The lockup is now centred in its control.
- **16/17/18/26 icons.** Globe redrawn with a vertical-ellipse meridian; gear as a ring +
  8 teeth + hub; tile as a one-circle roundel; new `DoloIcon.Dot` (a drawn circle) for the
  lobby server light, which a 10px `StyleBoxFlat` had degenerated into a diamond.
- **19 waits popup.** Dolo card instead of the green panel; per-wait columns widened so the
  counts stop colliding; dead-wait face-drop + furiten hatch now actually apply (they had
  no-op'd because `SetWaitDisplay` ran before the tile node entered the tree).
- **20 countdown bar.** Dolo inset trough; brass fill warming to deep red near the end.
- **21 table buttons.** `?`/`Menu` are ghost buttons with a drawn back chevron.
- **22 cosmetics preview.** New `WedgePreview` control draws just the player's own wedge at
  1:1 (prop centred, nameplate below) instead of the whole four-wedge felt scaled into a box.
- **25 results.** First-place row is a fixed 168px again (was `ExpandFill` → ballooned ~430px).
- **27** footer drops `(official)`. **28** top three menu buttons spaced so shadows don't merge.
- **29** seat-wind glyph enlarged to 32×42 (now the primary label). **31** river hairline set
  to the mockup's `rgba(206,182,120,0.45)` so the pads read as places, not smudges.
- **30 riichi felt stick** — verified it renders (staged in `ReviewShots`, both self and a
  CPU seat confirmed); the earlier overlay-redraw fix already resolved it. No change needed.
- **24/32 blank-cream tile** — could not reproduce; all art files (incl. `*-Dora`) exist.
  Hardened with a `TileNode` texture cache that does **not** cache a null load, so a transient
  miss self-heals on the next `Refresh`.

New files: `src/UI/Design/WedgePreview.cs` (+ its `.uid`).

## Next session

1. **If this branch is not committed / no PR yet:** `git add -A`, one conventional commit
   (no co-author trailer — attribution is disabled globally), push, open PR against `master`.
   Do **not** stack PRs — Dylan merges fast without deleting branches, so open against current
   `master`.
2. **`TEST_PLAN.md`** — section A (Core cosmetics-catalogue tests) is pure additions; B and C
   each carry a small Core refactor (finished-prop set into Core; river-capacity helper).

## Harnesses (unchanged)

```bash
Godot --path . --resolution 1920x1080 res://Scenes/ReviewShots.tscn   # poses all ten screens
Godot --path . --resolution 1920x1080 res://Scenes/AutoPlay.tscn      # plays a full solo game
```

Godot lives at
`C:\Users\Dylan\Documents\godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe`
(not on `PATH`). Output goes to
`%APPDATA%/Godot/app_userdata/RiichiMahjong/review/` — read the PNGs to confirm a fix.
Build C#: `dotnet build RiichiMahjong.csproj`. Tests: `dotnet run --project
tests/RiichiMahjong.Tests.csproj` (NOT `dotnet test` — false green).
