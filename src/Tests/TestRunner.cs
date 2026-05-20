// =============================================================================
// TestRunner.cs
// Headless test suite for the tile interactability chain.
// Run with:
//   Godot --headless --scene res://Scenes/TestScene.tscn
// Results written to user://test_results.txt
// =============================================================================

using Godot;
using System.Collections.Generic;
using RiichiMahjong.UI;
using RiichiMahjong.Core;
using MouseFilterEnum = Godot.Control.MouseFilterEnum;
using GodotFileAccess = Godot.FileAccess;

namespace RiichiMahjong.Tests
{
    public partial class TestRunner : Node
    {
        private readonly List<string> _log = new();
        private int _pass = 0;
        private int _fail = 0;

        public override void _Ready()
        {
            Log("=== RiichiMahjong Tile Interactability Test Suite ===");
            Log($"Godot {Engine.GetVersionInfo()["string"]}");
            Log("");

            Test_TileNode_MouseFilter_DefaultBeforeAddChild();
            Test_TileNode_MouseFilter_SetInteractiveBeforeAddChild();
            Test_TileNode_MouseFilter_AfterAddChild_Interactive();
            Test_TileNode_MouseFilter_AfterAddChild_NonInteractive();
            Test_TileNode_Signal_DirectEmit();
            Test_TileNode_Signal_ViaPressedEvent();
            Test_HUD_MouseFilter_AfterReady();
            Test_HandDisplay_TileMouseFilter_AfterRebuild();
            Test_HandDisplay_SignalChain();
            Test_HandDisplay_SignalChain_Discard();

            Log("");
            Log($"=== RESULTS: {_pass} PASSED, {_fail} FAILED ===");

            WriteResults();
            GetTree().Quit();
        }

        // -------------------------------------------------------------------------
        // T1: TileNode default MouseFilter before it enters the scene tree
        // -------------------------------------------------------------------------
        private void Test_TileNode_MouseFilter_DefaultBeforeAddChild()
        {
            var tile = new TileNode();
            Assert("T1 TileNode default MouseFilter (before AddChild) == Stop",
                tile.MouseFilter == MouseFilterEnum.Stop);
            tile.Free();
        }

        // -------------------------------------------------------------------------
        // T2: SetInteractive(true) before AddChild — does MouseFilter change?
        // -------------------------------------------------------------------------
        private void Test_TileNode_MouseFilter_SetInteractiveBeforeAddChild()
        {
            var tile = new TileNode();
            tile.SetInteractive(true);
            Assert("T2 After SetInteractive(true) before AddChild == Stop",
                tile.MouseFilter == MouseFilterEnum.Stop);
            tile.Free();
        }

        // -------------------------------------------------------------------------
        // T3: CRITICAL — after AddChild (_Ready runs), does MouseFilter stay Stop?
        // This test catches the bug where _Ready() resets MouseFilter to Ignore.
        // -------------------------------------------------------------------------
        private void Test_TileNode_MouseFilter_AfterAddChild_Interactive()
        {
            var tile = new TileNode();
            tile.SetInteractive(true);
            AddChild(tile);   // _Ready() fires here
            var filter = tile.MouseFilter;
            Assert($"T3 CRITICAL: After AddChild(interactive) MouseFilter == Stop (actual: {filter})",
                filter == MouseFilterEnum.Stop);
            tile.QueueFree();
        }

        // -------------------------------------------------------------------------
        // T4: Non-interactive tile should have Ignore after AddChild
        // -------------------------------------------------------------------------
        private void Test_TileNode_MouseFilter_AfterAddChild_NonInteractive()
        {
            var tile = new TileNode();
            tile.SetInteractive(false);
            AddChild(tile);
            var filter = tile.MouseFilter;
            Assert($"T4 After AddChild(non-interactive) MouseFilter == Ignore (actual: {filter})",
                filter == MouseFilterEnum.Ignore);
            tile.QueueFree();
        }

        // -------------------------------------------------------------------------
        // T5: TileClicked signal can be directly emitted and received
        // -------------------------------------------------------------------------
        private void Test_TileNode_Signal_DirectEmit()
        {
            var tile = new TileNode();
            AddChild(tile);
            bool received = false;
            tile.TileClicked += _ => received = true;
            tile.EmitSignal(TileNode.SignalName.TileClicked, tile);
            Assert("T5 TileClicked received after direct EmitSignal", received);
            tile.QueueFree();
        }

        // -------------------------------------------------------------------------
        // T6: TileClicked fires when Button's Pressed signal fires (the real path)
        // -------------------------------------------------------------------------
        private void Test_TileNode_Signal_ViaPressedEvent()
        {
            var tile = new TileNode();
            tile.SetInteractive(true);
            AddChild(tile);

            bool received = false;
            tile.TileClicked += _ => received = true;

            // Emit the Button "pressed" signal — this triggers OnPressed → TileClicked
            tile.EmitSignal(BaseButton.SignalName.Pressed);

            Assert("T6 TileClicked fires when Button.Pressed is emitted", received);
            tile.QueueFree();
        }

        // -------------------------------------------------------------------------
        // T7: HUD.MouseFilter must be Pass after _Ready (otherwise it blocks tiles)
        // -------------------------------------------------------------------------
        private void Test_HUD_MouseFilter_AfterReady()
        {
            var hud = new HUD();
            AddChild(hud);
            var filter = hud.MouseFilter;
            Assert($"T7 CRITICAL: HUD.MouseFilter == Pass after _Ready (actual: {filter})",
                filter == MouseFilterEnum.Pass);
            hud.QueueFree();
        }

        // -------------------------------------------------------------------------
        // T8: HandDisplay — interactive tiles have Stop filter after Rebuild
        // -------------------------------------------------------------------------
        private void Test_HandDisplay_TileMouseFilter_AfterRebuild()
        {
            var hand = new HandDisplay();
            hand.Orientation    = HandOrientation.Bottom;
            hand.IsInteractive  = true;
            hand.FaceDown       = false;
            AddChild(hand);   // _Ready creates HBoxContainer

            var tiles = new List<Tile>
            {
                Tile.Man(1), Tile.Man(2), Tile.Man(3),
                Tile.Pin(4), Tile.Pin(5), Tile.Pin(6),
            };
            hand.Rebuild(tiles);

            // Walk the HBoxContainer's children looking for TileNodes
            int stopCount  = 0;
            int ignoreCount = 0;
            int total       = 0;
            foreach (var child in hand.GetChild(0).GetChildren())
            {
                if (child is TileNode tn)
                {
                    total++;
                    if (tn.MouseFilter == MouseFilterEnum.Stop)   stopCount++;
                    if (tn.MouseFilter == MouseFilterEnum.Ignore) ignoreCount++;
                }
            }

            Assert($"T8a HandDisplay Rebuild produced TileNodes (found: {total})", total > 0);
            Assert($"T8b All interactive TileNodes have Stop filter (stop={stopCount}, ignore={ignoreCount}, total={total})",
                total > 0 && stopCount == total);

            hand.QueueFree();
        }

        // -------------------------------------------------------------------------
        // T9: HandDisplay — clicking a tile (via signal) emits TileSelected
        // -------------------------------------------------------------------------
        private void Test_HandDisplay_SignalChain()
        {
            var hand = new HandDisplay();
            hand.Orientation    = HandOrientation.Bottom;
            hand.IsInteractive  = true;
            hand.FaceDown       = false;
            AddChild(hand);

            var tiles = new List<Tile> { Tile.Man(1), Tile.Man(2) };
            hand.Rebuild(tiles);

            bool selectedFired = false;
            hand.TileSelected += _ => selectedFired = true;

            // Find first TileNode and emit its Pressed signal
            TileNode? firstTile = null;
            foreach (var child in hand.GetChild(0).GetChildren())
                if (child is TileNode tn) { firstTile = tn; break; }

            if (firstTile != null)
                firstTile.EmitSignal(BaseButton.SignalName.Pressed);

            Assert("T9 TileSelected fires on first tile press via HandDisplay", selectedFired);
            hand.QueueFree();
        }

        // -------------------------------------------------------------------------
        // T10: HandDisplay — second press on selected tile emits TileDiscarded
        // -------------------------------------------------------------------------
        private void Test_HandDisplay_SignalChain_Discard()
        {
            var hand = new HandDisplay();
            hand.Orientation    = HandOrientation.Bottom;
            hand.IsInteractive  = true;
            hand.FaceDown       = false;
            AddChild(hand);

            var tiles = new List<Tile> { Tile.Man(1), Tile.Man(2) };
            hand.Rebuild(tiles);

            bool discardFired = false;
            hand.TileDiscarded += _ => discardFired = true;

            // First press → select
            TileNode? firstTile = null;
            foreach (var child in hand.GetChild(0).GetChildren())
                if (child is TileNode tn) { firstTile = tn; break; }

            if (firstTile != null)
            {
                firstTile.EmitSignal(BaseButton.SignalName.Pressed); // select
                firstTile.EmitSignal(BaseButton.SignalName.Pressed); // discard
            }

            Assert("T10 TileDiscarded fires on second press of same tile", discardFired);
            hand.QueueFree();
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        private void Assert(string name, bool condition)
        {
            string status = condition ? "PASS" : "FAIL";
            Log($"  [{status}] {name}");
            if (condition) _pass++; else _fail++;
        }

        private void Log(string line)
        {
            _log.Add(line);
            GD.Print(line);
        }

        private void WriteResults()
        {
            string path = "user://test_results.txt";
            using var file = GodotFileAccess.Open(path, GodotFileAccess.ModeFlags.Write);
            if (file == null)
            {
                GD.PrintErr($"Could not write to {path}: {GodotFileAccess.GetOpenError()}");
                return;
            }
            foreach (var line in _log)
                file.StoreLine(line);
            GD.Print($"\nResults written to: {path}");
        }
    }
}
