# Session handoff — 2026-08-18 (test plan A/B/C)

Entry point for the next session. Read this, then
[`TEST_PLAN.md`](TEST_PLAN.md) (status block at the top) and
[`REDESIGN_REVIEW.md`](REDESIGN_REVIEW.md) if you need the redesign history.

## State

| | |
|---|---|
| Branch | `test/cosmetics-catalogue-tests`, off `master` after PR #19 merged. **Pushed / PR open** unless noted below. |
| Tests | **527 pass, 0 fail** (was 476; +51 across cosmetics and river geometry) |
| Builds | client clean, 0 warnings / 0 errors; `smoke.sh` OK; a full `AutoPlay` game exits 0 |
| Merged | PR #18 (P0/P1/P2) and PR #19 (all of P3) are both on master |

## What this session did — `TEST_PLAN.md` sections A, B and C

Three commits, one per section (TEST_PLAN suggested that split):

- **A — `test:` `330717a`** — `tests/CosmeticsTests.cs`. Pure additions: every slot default
  and every CPU seat resolves to a real option; Serialise/Deserialise round-trips; and the
  defensive wire cases (empty/null/whitespace, short strings, unknown ids, mixed valid/
  invalid, extra fields, per-field trim) all fall back to a valid set rather than throwing.
- **B — `refactor:` `712751c`** — moved the finished-prop set into Core
  (`CosmeticCatalogue.FinishedProps` / `PropIsFinished`); `CosmeticVisuals.PropIsDrawn`
  delegates to it. Tests: the default prop and every CPU prop have finished art — the exact
  assertions that would have caught the shipped item-23 bug. Fixed a stale comment on `Prop`.
- **C — `test:` (amended)** — river capacity extracted to Core (`RiverGeometry.Fit`/
  `Capacity`, no Godot dependency). `DoloLayout` now derives tile size, separation and the
  rect *dimensions* from it (each rect is a per-seat centre expanded to the Core size; the
  on-table geometry is unchanged, verified pixel-for-pixel on the rendered table). Tests
  assert desktop ≥ 22 (6×4 = 24), lock touch at 16, cover the `Fit` boundaries, and encode
  the P0.1 case (a 40px-min tile regresses below a full hand). `AutoPlay` now **fails fast
  with a non-zero exit** the instant a river clips.

### AutoPlay harness robustness (part of C)

Reaching the results screen calls `ChangeSceneToFile`, which frees the AutoPlay node. That
used to make the end-of-run `GetTree().Quit()` throw `data.tree is null` (the process exited
via the crash). The fix: capture the `SceneTree` up front and quit through it, guard
`Frames`/`Capture`/`Step` against the freed node, and fail fast on a clip. A full game now
exits 0 cleanly with `clip check: OK` — no crash, no hang on the rematch screen.

## Next session

1. If the branch is not pushed / no PR: `git push -u`, open a PR against `master` (one PR for
   all three commits). Do **not** stack PRs — Dylan merges fast without deleting branches.
2. **`TEST_PLAN.md` section D** is visual-only and intentionally stays on the
   `ReviewShots` / `AutoPlay` harnesses — no unit tests to add, just re-check the listed
   screens when the relevant UI changes.
3. Open runway beyond the test plan: playtest with real players; a Supabase keep-alive to
   stop the recurring free-tier pause; optionally a CI workflow that runs the test console
   app so the `dotnet test` false-green can't hide a regression.

## Harnesses (unchanged)

```bash
Godot --path . --resolution 1920x1080 res://Scenes/ReviewShots.tscn   # poses all ten screens
Godot --path . --resolution 1920x1080 res://Scenes/AutoPlay.tscn      # plays a full solo game, exits 0/1
```

Godot lives at
`C:\Users\Dylan\Documents\godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe`
(not on `PATH`). Output goes to `%APPDATA%/Godot/app_userdata/RiichiMahjong/review/`.
Build C#: `dotnet build RiichiMahjong.csproj`. Tests: `dotnet run --project
tests/RiichiMahjong.Tests.csproj` (NOT `dotnet test` — false green).
