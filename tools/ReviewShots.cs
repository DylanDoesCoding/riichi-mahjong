// =============================================================================
// ReviewShots.cs — development tool, not shipped.
//
// Stages every screen at the 1920 x 1080 reference size and writes a PNG of
// each to user://review/. It exists because the redesign has no visual
// regression net: the headless smoke check proves a scene constructs, not that
// it looks right, and several screens (scoring, results, claim window) are
// several minutes of play away from being visible at all.
//
// Run with:
//   Godot --path . --resolution 1920x1080 res://Scenes/ReviewShots.tscn
//
// Screens are instantiated as children rather than reached by changing scenes,
// so each one can be posed with representative data before it is captured.
// =============================================================================

using Godot;
using System.Collections.Generic;
using System.Linq;
using RiichiMahjong.Core;
using RiichiMahjong.UI;

public partial class ReviewShots : Node
{
    private const string OutDir = "user://review";

    public override async void _Ready()
    {
        DirAccess.MakeDirRecursiveAbsolute(OutDir);
        GD.Print($"[review] writing to {ProjectSettings.GlobalizePath(OutDir)}");

        await ShotMainMenu();
        await ShotSettings();
        await ShotCosmetics();
        await ShotLobby();
        await ShotTable();
        await ShotScoring();
        await ShotDraw();
        await ShotResults();

        GD.Print("[review] done");
        GetTree().Quit();
    }

    // =====================================================================
    // Screens
    // =====================================================================

    private async System.Threading.Tasks.Task ShotMainMenu()
    {
        var menu = Load("res://Scenes/MainMenu.tscn");
        await Settle(30);
        await Capture("01-main-menu");
        menu.QueueFree();
        await Settle(3);
    }

    private async System.Threading.Tasks.Task ShotSettings()
    {
        var menu = Load("res://Scenes/MainMenu.tscn");
        await Settle(20);

        // Open the overlay the way the player does.
        menu.GetNode<Button>("CentrePanel/OptionsButton").EmitSignal(BaseButton.SignalName.Pressed);
        await Settle(20);
        await Capture("02-settings");

        menu.QueueFree();
        await Settle(3);
    }

    private async System.Threading.Tasks.Task ShotCosmetics()
    {
        var screen = Load("res://Scenes/Cosmetics.tscn");
        await Settle(30);
        await Capture("03-cosmetics");
        screen.QueueFree();
        await Settle(3);
    }

    private async System.Threading.Tasks.Task ShotLobby()
    {
        var lobby = Load("res://Scenes/Lobby.tscn");
        await Settle(30);
        await Capture("04-lobby-play-online");
        lobby.QueueFree();
        await Settle(3);
    }

    private async System.Threading.Tasks.Task ShotTable()
    {
        var table = Load("res://Scenes/GameTable.tscn");

        // Let the solo game deal and the CPUs take a few turns, so the rivers
        // have tiles in them rather than showing an empty table.
        await Settle(260);
        await Capture("05-table");

        var hud = table.GetNodeOrNull<HUD>("UI/HUD");
        if (hud != null)
        {
            // Claim window: the five calls plus the ring on the discarded tile.
            hud.ShowClaimButtons(canRon: true, canPon: true, canChi: true, canKan: true);
            hud.SetStatus("RON available! Click RON or PASS.");
            hud.SetCountdownTile(1);
            hud.StartCountdown(20f);
            hud.UpdateCountdown(13f, 20f);
            await Settle(20);
            await Capture("06-claim-window");

            hud.HideClaimButtons();
            hud.StopCountdown();

            // Waits popup, including a dead wait and the furiten hatch.
            hud.SetFuriten(true, isPermanent: true);
            var waits = new List<Tile>
            {
                new(TileSuit.Sou, 3), new(TileSuit.Sou, 6), new(TileSuit.Pin, 1),
            };
            var remaining = new Dictionary<int, int>
            {
                { waits[0].TileId, 3 }, { waits[1].TileId, 1 }, { waits[2].TileId, 0 },
            };
            hud.ShowWaitsPopup(waits, remaining, new Tile(TileSuit.Man, 9));
            await Settle(20);
            await Capture("07-waits-and-furiten");
            hud.HideWaitsPopup();
            hud.SetFuriten(false, false);
        }

        table.QueueFree();
        await Settle(3);
    }

    private async System.Threading.Tasks.Task ShotScoring()
    {
        var table = Load("res://Scenes/GameTable.tscn");
        await Settle(120);

        var hud = table.GetNodeOrNull<HUD>("UI/HUD");
        hud?.ShowScoringPanelNet(
            winnerName:  "Dolo",
            isTsumo:     false,
            payerName:   "CPU 2",
            allNames:    new[] { "Dolo", "CPU 1", "CPU 2", "CPU 3" },
            allPoints:   new[] { 32900, 24000, 18100, 25000 },
            winnerSeat:  0,
            dealerSeat:  0,
            yakuNames:   new[] { "Riichi", "Pinfu", "Pure Double Sequence" },
            yakuFans:    new[] { 1, 1, 1 },
            yakuIsYakuman: new[] { false, false, false },
            han: 5, fu: 30,
            doraCount: 1, uraDoraCount: 1, redDoraCount: 0,
            totalPointsWon: 7900);

        await Settle(20);
        await Capture("08-scoring-ron");

        table.QueueFree();
        await Settle(3);
    }

    private async System.Threading.Tasks.Task ShotDraw()
    {
        var table = Load("res://Scenes/GameTable.tscn");
        await Settle(120);

        var hud = table.GetNodeOrNull<HUD>("UI/HUD");
        hud?.ShowRyuukyokuPanel(
            names:            new[] { "Dolo", "CPU 1", "CPU 2", "CPU 3" },
            currentPoints:    new[] { 26500, 24500, 25500, 23500 },
            dealerVisualSeat: 0,
            isTenpai:         new[] { true, false, true, false },
            waitingTiles: new[]
            {
                new List<Tile> { new(TileSuit.Sou, 3), new(TileSuit.Sou, 6) },
                new List<Tile>(),
                new List<Tile> { new(TileSuit.Pin, 5) },
                new List<Tile>(),
            },
            pointDeltas: new[] { 1500, -1500, 1500, -1500 });

        await Settle(20);
        await Capture("09-exhaustive-draw");

        table.QueueFree();
        await Settle(3);
    }

    private async System.Threading.Tasks.Task ShotResults()
    {
        // A synthetic finished game, so the screen has a real trajectory and log.
        var names  = new[] { "Dolo", "CPU 1", "CPU 2", "CPU 3" };
        var record = new MatchRecord(names);

        record.RecordRiichi(0);
        record.RecordRiichi(0);
        record.RecordRiichi(2);
        record.RecordRiichi(3);

        var totals = new[] { 25000, 25000, 25000, 25000 };
        var script = new (string label, int winner, int loser, int han, string yaku, int[] deltas)[]
        {
            ("East 1", 0,  2, 3, "Riichi, Pinfu, Tsumo",        new[] {  5800,     0, -5800,     0 }),
            ("East 2", 2,  1, 2, "Yakuhai, Dora 1",             new[] {     0, -2900,  2900,     0 }),
            ("East 3", -1, -1, 0, "",                           new[] {  1500, -1500,  1500, -1500 }),
            ("East 4", 0,  3, 6, "Riichi, Chinitsu",            new[] { 12000,     0,     0, -12000 }),
            ("South 1", 1, 0, 2, "Riichi, Tsumo",               new[] { -2600,  5200, -1300, -1300 }),
            ("South 2", 0, -1, 4, "Riichi, Ippatsu, Tsumo",     new[] {  5200, -2600, -1300, -1300 }),
            ("South 3", 3, 0, 3, "Riichi, Sanshoku",            new[] { -5800,     0,     0,  5800 }),
            ("South 4", 0, 2, 5, "Riichi, Ura 2, Menzen Tsumo", new[] {  8000,     0, -8000,     0 }),
        };

        foreach (var hand in script)
        {
            for (int seat = 0; seat < 4; seat++) totals[seat] += hand.deltas[seat];

            record.RecordHand(new HandLogEntry
            {
                Label      = hand.label,
                WinnerSeat = hand.winner,
                LoserSeat  = hand.loser,
                IsDraw     = hand.winner < 0,
                Yaku       = hand.yaku,
                Han        = hand.han,
                Fu         = 30,
                Deltas     = (int[])hand.deltas.Clone(),
                Totals     = (int[])totals.Clone(),
            });
        }

        MatchResultsHandoff.Record    = record;
        MatchResultsHandoff.Results   = record.Settle(totals);
        MatchResultsHandoff.LocalSeat = 0;

        var screen = Load("res://Scenes/Results.tscn");
        await Settle(40);
        await Capture("10-results");
        screen.QueueFree();
        MatchResultsHandoff.Clear();
        await Settle(3);
    }

    // =====================================================================
    // Plumbing
    // =====================================================================

    private Node Load(string path)
    {
        var node = GD.Load<PackedScene>(path).Instantiate();
        AddChild(node);
        return node;
    }

    /// <summary>Let N frames pass so layout, tweens and the AI timer settle.</summary>
    private async System.Threading.Tasks.Task Settle(int frames)
    {
        for (int i = 0; i < frames; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async System.Threading.Tasks.Task Capture(string name)
    {
        // The viewport texture is only valid after a completed draw.
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

        var image = GetViewport().GetTexture().GetImage();
        string path = $"{OutDir}/{name}.png";
        var err = image.SavePng(path);
        GD.Print($"[review] {name}: {err} ({image.GetWidth()}x{image.GetHeight()})");
    }
}
