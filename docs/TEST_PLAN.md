# Test plan — cosmetics + river geometry

## Status — DONE 2026-08-18 (branch `test/cosmetics-catalogue-tests`)

Sections **A, B and C are implemented** (476 → 527 unit tests, 0 fail). Section **D**
stays on the `ReviewShots` / `AutoPlay` harnesses, as intended, and is unchanged.

- **A** — `tests/CosmeticsTests.cs`: slot defaults, CPU seats and the wire format, plus
  the defensive Deserialise cases. Pure additions.
- **B** — finished-prop set moved into Core (`CosmeticCatalogue.FinishedProps` /
  `PropIsFinished`); `CosmeticVisuals.PropIsDrawn` delegates to it. Tests: the default
  prop and every CPU prop have finished art (the exact assertions that would have caught
  the shipped item-23 bug).
- **C** — river capacity extracted to Core (`RiverGeometry.Fit` / `Capacity`);
  `DoloLayout` derives tile size, separation and the rect dimensions from it (rects
  unchanged, verified on the rendered table). Tests assert desktop ≥ 22 (6×4 = 24), lock
  touch at 16, and encode the P0.1 case (a 40px-min tile regresses below a full hand).
  `AutoPlay` now exits non-zero when any river clips, so the harness gates the regression.

---

The original backlog follows, for reference.

# Test plan — to add *after* the next PR

Scheduled for after the redesign-review fixes land (branch
`fix/redesign-review-p0-p1`). These are **not** in that PR; this is the backlog.

Context that shapes the list: `tests/RiichiMahjong.Tests.csproj` compiles only
`src/Core/*.cs` and `src/AI/AIPlayer.cs` — **no Godot dependency**. So anything
that touches a `Godot.*` type (all of `src/UI/`) cannot be a unit test in the
current harness. The visual net for those is `tools/ReviewShots.cs` and
`tools/AutoPlay.cs`. The list below separates what the unit harness can take now
from what needs a small refactor or the visual harness.

---

## A. Core unit tests — implementable now, no new dependency

Add to a new `tests/CosmeticsTests.cs` with a `Run()` returning `(pass, fail)`,
wired into the runner like `MatchRecordTests`.

1. **Every slot default is a real option.**
   For each `CosmeticSlot`, `IsValid(slot, DefaultFor(slot))` is true. Cheap, and
   it guards against a default pointing at an id that was renamed or removed.

2. **CPU seat sets are valid.**
   For `seat` in 0..3, `CosmeticSet.ForCpuSeat(seat)` yields ids that are
   `IsValid` in each slot. Guards the hard-coded CPU tables in `Cosmetics.cs`.

3. **Serialise / Deserialise round-trips.**
   `Deserialise(set.Serialise()) == set` field-for-field. Then the defensive
   cases: `Deserialise("")`, a two-field string, and a string with an unknown id
   per slot all fall back to valid ids rather than throwing. The wire format
   (`Surface|Frame|Prop|Emblem`) crosses the network, so malformed input from a
   peer must not crash the client.

## B. Item-23 regression — needs a tiny Core refactor first

The bug that shipped: the **default prop was `coffee`, which had no art**, so every
new player's table showed a dashed placeholder. It is fixed now (art added,
wired in `CosmeticVisuals.PropIsDrawn`), but it is **not currently unit-testable**
because "which props have finished art" lives in `src/UI/` (`CosmeticVisuals`),
which the test project cannot see.

**Refactor:** move the finished-prop set into Core — either
`CosmeticCatalogue.FinishedProps` (a `HashSet<string>`) or a `Finished` flag on
`CosmeticOption` — and have `CosmeticVisuals.PropIsDrawn` delegate to it. One
source of truth, and then:

4. **The default prop has finished art.** `FinishedProps.Contains(DefaultFor(Prop))`.
   This is the exact assertion that would have caught the shipped bug.

5. **No CPU seat shows a placeholder.** Every prop in every `ForCpuSeat` is in
   `FinishedProps` (the CPU tables already intend this — the comment says so — but
   nothing enforces it).

## C. Highest-value regression — the river clip (P0.1), needs an extract

The headline bug (rivers silently dropping discards) was a layout-minimum
interaction, caught only by playing. To make it a fast test rather than a
full-game harness run:

6. **Extract river capacity to a pure helper.** A function of (river rect, tile
   size, separation) → `(columns, rows, capacity)`, living somewhere Core-visible
   (or a small `src/Core`-side geometry util the UI calls). Then assert desktop
   capacity is **≥ 22** (a hand to exhaustion is 17–18, and a riichi rotation eats
   a column), and touch likewise against its own max. This encodes *why* 6×4 was
   chosen and fails loudly if a future padding/size change regresses it.

7. **Promote `AutoPlay`'s clip check to a hard failure.** Today it logs
   `held == visible` / `CLIPPED`. Make a `CLIPPED` line set a non-zero exit code
   so the harness can gate a change, not just narrate one. Until there is CI this
   is the closest thing to a river regression test.

## D. Visual-only — keep on the ReviewShots / AutoPlay harnesses

Not worth forcing into a headless unit test; a Godot `Button`/layout is needed.
Note them so they are re-checked when the relevant screen changes:

- **IconButton captions never double.** Setting a caption goes through
  `DoloWidgets.SetIconButtonText` (child label), never `button.Text`, so the
  Button never draws a second caption underneath the row. (Regression from this
  PR — the "Next Hand" button read as "NlexttHHamdd".)
- **Scoring card fills its width** (PanelContainer, not a bare Panel).
- **Overlays redraw after layout** — the furiten hatch actually renders; the dora
  wedge is proportional and outlined.
- **Results rail fits 1920** with the chart filling the flexible column.
- **Settings shows every row** with no scroll (two-column body).

---

### Suggested sequencing

`A` first (pure additions, no risk). `B` and `C` each carry a small refactor, so
they want their own commit with the test alongside the extract. `D` stays as
harness checks referenced from `REDESIGN_REVIEW.md`.
