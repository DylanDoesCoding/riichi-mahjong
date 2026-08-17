# Session handoff — 2026-08-17

Entry point for the next session. Read this, then
[`REDESIGN_REVIEW.md`](REDESIGN_REVIEW.md), which is the actual work list.

## State

| | |
|---|---|
| Branch | `feature/dolo-redesign` — 10 commits, PR #17 open against `master` |
| Tests | 476 pass (443 existing + 33 new placement-scoring) |
| Builds | client, server and tests all clean, no warnings |
| Scenes | all six boot headless, in both layouts and both tile themes |
| Verified by play | 14 hands auto-played across two runs, no hang, no script error |
| Uncommitted (Dylan's, untouched) | `export_presets.cfg`, `Assets/beer.png`, `Assets/cig.png` |

## What this session did

1. Implemented all ten passes of the Dolo redesign, one commit per pass, on top
   of a new design-token layer in `src/UI/Design/`.
2. Then actually rendered and played it, and found a lot wrong. **Nothing from
   that review is fixed** — it is logged, root-caused and ordered.

The second half is the important part: everything built clean, booted clean and
passed its tests while still looking wrong. `scripts/smoke.sh` only proves a
scene *constructs*.

## The headline bug

Both of Dylan's reports — discards too small, river not showing them all — are
one defect, and it is not in the river code.

`TileNode` is a `Button`, and `DoloTheme` gives every `Button` 20px horizontal
content margins (`DoloStyles.SetButtonPadding`). A Button's minimum size includes
its stylebox margins, so a 28px river tile gets a **40px minimum width** — four
columns fit where the design specifies six, capacity is 16 rather than 24, and:

```
wall=0   seat0: held=17 visible=16  CLIPPED   seat1: held=18 visible=16  CLIPPED
         seat2: held=17 visible=16  CLIPPED   seat3: held=17 visible=16  CLIPPED
```

Fix with `StyleBoxEmpty` overrides on `TileNode`. Note that restoring six columns
is still marginal: the design expects 18 per river, exhaustion produces 17–18,
and a riichi rotation eats a further column. Size for ~22.

## Order of work

1. The `TileNode` padding fix — re-run `AutoPlay` and confirm `held == visible`.
2. Four more that are simply broken (details in the review doc):
   - `LobbyController` never calls `DoloTheme.Apply`, so the whole lobby is unthemed.
   - `_scoringPanel` is a `Panel` with an unanchored `VBoxContainer`.
   - No tile overlay connects `Resized`, so the furiten hatch never renders.
   - `config/name` moved `user://` — existing players lose settings, UUID, login.
3. Put the three P2 decisions to Dylan before touching them: the red tile backs,
   the five call-button hues, and Settings overflowing its card.
4. P3 polish.

## Harnesses

Both under `tools/`, committed because this repo has no CI and no visual tests.

```bash
Godot --path . --resolution 1920x1080 res://Scenes/ReviewShots.tscn   # poses all ten screens
Godot --path . --resolution 1920x1080 res://Scenes/AutoPlay.tscn      # plays a full solo game
```

Output goes to `user://review/`. `AutoPlay` drives real `TileNode`s and real HUD
buttons rather than calling into `GameState`, so anything that only breaks in the
interface still breaks there. It logs per-seat river occupancy against what the
river can actually show.

## Notes

- **Computer-use cannot drive the game.** `request_access` resolves against
  Start-menu applications and the game is not installed, so neither "Godot" nor
  "Dolo Mahjong" matches. The harnesses exist for that reason.
- **The Claude Design MCP works.** `DesignSync`, project
  `3099062e-7488-43d5-908e-32819075f560`, file `Dolo Table.dc.html` — `list_files`
  and `get_file` needed no auth prompt. The colourblind pass in that file is the
  authoritative spec for the six tile highlight states, and it already settles the
  four-colour-felt question: the tints collapse to one tone *"because a wedge tint
  competed with a dora border for the same attention"*.
- The full-game loop itself is sound. Every problem found is in the presentation
  layer.
