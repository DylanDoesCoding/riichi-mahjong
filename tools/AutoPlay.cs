// =============================================================================
// AutoPlay.cs — development tool, not shipped.
//
// Plays a complete solo game from deal to game over by driving the real UI:
// it presses actual TileNodes and actual HUD buttons rather than calling into
// GameState, so anything that only breaks in the interface still breaks here.
//
// It exists to answer questions a single posed screenshot cannot:
//   - do the rivers hold a full hand's discards, or does ClipContents eat them
//   - what do the rivers look like late in a hand rather than on turn one
//   - does the hand -> scoring -> next hand -> game over loop actually complete
//
// Captures screenshots at the interesting moments and writes a diagnostic log
// with per-seat discard counts against what the river is actually showing.
//
// Run with:
//   Godot --path . --resolution 1920x1080 res://Scenes/AutoPlay.tscn
// =============================================================================

using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RiichiMahjong.UI;

public partial class AutoPlay : Node
{
    private const string OutDir = "user://review";

    /// <summary>Frames between actions, so animations and AI turns can resolve.</summary>
    private const int ActionInterval = 6;

    /// <summary>Give up rather than spin forever if the game wedges.</summary>
    private const int MaxFrames = 60 * 60 * 12;

    private Node          _table = null!;
    private HUD?          _hud;
    private readonly StringBuilder _log = new();

    private int _frame;
    private int _handsSeen;
    private int _shots;
    private int _lastLoggedWall = int.MaxValue;
    private bool _clipDetected;

    // The game ends by changing the scene to Results, which frees this node — so the
    // SceneTree is captured up front and every Quit goes through it, since GetTree() on a
    // freed node throws. Without this the process either crashed (before the guards) or
    // hung idle on the rematch screen (after them) instead of exiting.
    private SceneTree _tree = null!;

    public override async void _Ready()
    {
        DirAccess.MakeDirRecursiveAbsolute(OutDir);
        Log("=== autoplay: full solo game ===");
        _tree = GetTree();

        _table = GD.Load<PackedScene>("res://Scenes/GameTable.tscn").Instantiate();
        AddChild(_table);

        await Frames(60);
        _hud = _table.GetNodeOrNull<HUD>("UI/HUD");
        Log(_hud != null ? "hud found" : "HUD MISSING");

        while (_frame < MaxFrames)
        {
            await Frames(ActionInterval);
            // Reaching the results screen swaps the scene and frees this node; once that
            // happens there is no tree left to drive, so stop rather than crash on it.
            if (!IsInsideTree()) break;
            _frame += ActionInterval;

            SampleRivers();

            bool over;
            try { over = await Step(); }
            catch (System.Exception e) { Log($"step error: {e.GetType().Name}: {e.Message}"); over = false; }
            if (over) break;
        }

        // Whether the game freed us on its way to the results screen or we simply ran out
        // of frames, quit through the cached tree. A clip already forced exit 1 in
        // SampleRivers, so reaching here means no river clipped.
        Log(_clipDetected
            ? "clip check: FAIL — a river clipped discards (exit 1)"
            : "clip check: OK — no river clipped its discards");
        Log($"=== finished after {_frame} frames, {_handsSeen} hand ends, {_shots} shots ===");
        Flush();
        _tree.Quit(_clipDetected ? 1 : 0);
    }

    // =====================================================================
    // One decision
    // =====================================================================

    /// <summary>Returns true when the game is over.</summary>
    private async System.Threading.Tasks.Task<bool> Step()
    {
        // If the game already swapped us out for the results scene, this node is freed —
        // report game-over so the loop ends and quits through the cached tree.
        if (!IsInsideTree()) return true;

        // Game over lands on the results screen, which replaces the whole scene.
        if (GetTree().CurrentScene?.Name == "Results" || FindNodeOfType<ResultsScreen>(GetTree().Root) != null)
        {
            await Frames(40);
            await Capture("game-over-results");
            Log("results screen reached");
            return true;
        }

        // Late in a hand the rivers are at their fullest — that is the moment the
        // clipping is visible rather than merely measurable.
        if (!_lateShot && FindWallCount() is >= 0 and <= 6)
        {
            _lateShot = true;
            await Capture("rivers-full");
        }

        // A hand has ended: capture the panel, then continue.
        var scoringNext = FindScoringNextButton();
        if (scoringNext != null)
        {
            _handsSeen++;
            await Frames(10);
            await Capture($"hand{_handsSeen:00}-result");
            Log($"hand {_handsSeen} ended");
            scoringNext.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(30);
            return false;
        }

        // Claim window open: always pass, so the game keeps moving.
        var pass = FindVisibleButton("PASS");
        if (pass != null)
        {
            if (!_claimShot)
            {
                _claimShot = true;
                await Capture("claim-window-live");
            }
            pass.EmitSignal(BaseButton.SignalName.Pressed);
            return false;
        }

        var next = FindVisibleButton("NEXT HAND");
        if (next != null)
        {
            next.EmitSignal(BaseButton.SignalName.Pressed);
            return false;
        }

        // Our turn: two taps on the first interactive tile — select, then discard.
        // The hand can rebuild between the taps (a draw, a claim resolving), which
        // frees the node out from under us, so both taps are guarded.
        var tile = FirstInteractiveHandTile();
        if (tile != null && IsInstanceValid(tile))
        {
            tile.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(2);

            if (IsInstanceValid(tile) && !tile.IsQueuedForDeletion())
                tile.EmitSignal(BaseButton.SignalName.Pressed);

            _idleTicks = 0;
        }
        else
        {
            // Nothing to do: if this persists, say what the UI is showing so the
            // stall can be diagnosed from the log rather than guessed at.
            if (++_idleTicks % 200 == 0) LogVisibleState();
        }

        return false;
    }

    private bool _claimShot;
    private bool _lateShot;
    private int  _idleTicks;

    private void LogVisibleState()
    {
        var buttons = FindAllOfType<Button>(_table)
            .Where(b => b.IsVisibleInTree() && b.Text.Length > 0)
            .Select(b => b.Text);
        var status = FindAllOfType<Label>(_table)
            .FirstOrDefault(l => l.Text.Length > 12 && l.Text.Contains(' '));
        Log($"idle x{_idleTicks}: buttons=[{string.Join(", ", buttons)}] status=\"{status?.Text}\"");
    }

    // =====================================================================
    // Instrumentation
    // =====================================================================

    /// <summary>
    /// Compare what each river holds against what it can actually show. The pool is
    /// clipped, so any tile past the visible rect is silently invisible - which is
    /// exactly the failure being investigated.
    /// </summary>
    private void SampleRivers()
    {
        if (_hud == null) return;

        var wallLabel = FindWallCount();
        if (wallLabel < 0) return;

        // Sample once per wall count, and only in the second half of the hand where
        // the rivers are actually full.
        if (wallLabel == _lastLoggedWall) return;
        _lastLoggedWall = wallLabel;
        if (wallLabel > 20 || wallLabel % 4 != 0) return;

        var pools = FindRiverPools();
        var parts = new List<string>();

        for (int i = 0; i < pools.Count; i++)
        {
            var pool  = pools[i];
            int held  = pool.GetChildCount();
            var rect  = (pool.GetParent() as Control)?.Size ?? Vector2.Zero;

            int shown = 0;
            foreach (var child in pool.GetChildren())
                if (child is Control c && c.Position.Y + c.Size.Y <= rect.Y + 0.5f
                                       && c.Position.X + c.Size.X <= rect.X + 0.5f)
                    shown++;

            if (held > shown) _clipDetected = true;
            parts.Add($"seat{i}: held={held} visible={shown}{(held > shown ? "  CLIPPED" : "")}");
        }

        Log($"wall={wallLabel}  " + string.Join("   ", parts));

        // Fail fast, while the node is still in the tree: a clip forces exit 1 the moment
        // it is seen. Waiting until the end of the game is unreliable because reaching the
        // results screen swaps the scene and frees this node before an end-of-run gate
        // could run. A non-zero exit lets a harness run fail a change, not just narrate it.
        if (_clipDetected)
        {
            Log("clip check: FAIL — a river clipped discards (exit 1)");
            Flush();
            _tree.Quit(1);
        }
    }

    private int FindWallCount()
    {
        foreach (var label in FindAllOfType<Label>(_table))
            if (label.Text.EndsWith(" LEFT") &&
                int.TryParse(label.Text.Replace(" LEFT", ""), out int n))
                return n;
        return -1;
    }

    private List<Control> FindRiverPools()
    {
        // The pools are HFlowContainers inside clipped Controls, added by the HUD.
        return FindAllOfType<HFlowContainer>(_table).Cast<Control>().ToList();
    }

    // =====================================================================
    // Node search
    // =====================================================================

    private Button? FindVisibleButton(string textStartsWith)
    {
        foreach (var b in FindAllOfType<Button>(_table))
            if (b.Visible && b.IsVisibleInTree() && !b.Disabled
                && b.Text.StartsWith(textStartsWith, System.StringComparison.OrdinalIgnoreCase))
                return b;
        return null;
    }

    /// <summary>
    /// The scoring card's Next Hand button carries its label in a child row rather
    /// than in Button.Text, so it is found by looking inside the scoring backdrop.
    /// </summary>
    private Button? FindScoringNextButton()
    {
        foreach (var rect in FindAllOfType<ColorRect>(_table))
        {
            if (!rect.Visible || !rect.IsVisibleInTree()) continue;
            if (rect.Size.X < 1000) continue;              // full-screen backdrop only
            if (rect.Color.A < 0.5f) continue;

            var buttons = FindAllOfType<Button>(rect).Where(b => b.IsVisibleInTree()).ToList();
            foreach (var b in buttons)
                foreach (var child in b.GetChildren())
                    if (child is HBoxContainer row)
                        foreach (var inner in row.GetChildren())
                            if (inner is Label l && l.Text.Contains("Next"))
                                return b;
        }
        return null;
    }

    private TileNode? FirstInteractiveHandTile()
    {
        var hand = _table.GetNodeOrNull<Control>("UI/PlayerHand");
        if (hand == null) return null;

        foreach (var tile in FindAllOfType<TileNode>(hand))
            if (!tile.Disabled && tile.IsVisibleInTree())
                return tile;
        return null;
    }

    private static List<T> FindAllOfType<T>(Node root) where T : Node
    {
        var found = new List<T>();
        void Walk(Node n)
        {
            if (n is T match) found.Add(match);
            foreach (var child in n.GetChildren()) Walk(child);
        }
        Walk(root);
        return found;
    }

    private static T? FindNodeOfType<T>(Node root) where T : Node
        => FindAllOfType<T>(root).FirstOrDefault();

    // =====================================================================
    // Plumbing
    // =====================================================================

    private async System.Threading.Tasks.Task Frames(int count)
    {
        for (int i = 0; i < count; i++)
        {
            // The game-over scene change can free this node mid-await; without the guard
            // the next ToSignal calls GetTree() on a freed node and throws "data.tree is null".
            if (!IsInsideTree()) return;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private async System.Threading.Tasks.Task Capture(string name)
    {
        if (!IsInsideTree()) return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (!IsInsideTree()) return;
        var image = GetViewport().GetTexture().GetImage();
        image.SavePng($"{OutDir}/auto-{name}.png");
        _shots++;
        Log($"shot: auto-{name}.png");
    }

    private void Log(string line)
    {
        GD.Print($"[autoplay] {line}");
        _log.AppendLine(line);
        Flush();
    }

    private void Flush()
    {
        using var file = Godot.FileAccess.Open($"{OutDir}/autoplay.log", Godot.FileAccess.ModeFlags.Write);
        file?.StoreString(_log.ToString());
    }
}
