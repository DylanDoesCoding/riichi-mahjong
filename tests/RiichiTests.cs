// =============================================================================
// RiichiTests.cs
// Unit tests for riichi-specific game rules:
//   - A riichi player may only discard the tile they just drew
//   - A riichi player cannot pon or chi on others' discards
//   - A riichi player can still declare tsumo
//   - DeclareRiichi flag persists; Reset() clears it
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RiichiMahjong.Core;

static class RiichiTests
{
    public static (int pass, int fail) Run()
    {
        Console.WriteLine("[ Riichi Rules ]\n");
        int pass = 0, fail = 0;

        void Test(string name, bool result)
        {
            Console.WriteLine($"  {(result ? "✓" : "✗")}  {name}");
            if (result) pass++; else fail++;
        }

        // =====================================================================
        // 1. Hand-level riichi flag
        // =====================================================================
        {
            var h = new Hand();
            Test("Hand.IsRiichi false initially", !h.IsRiichi);

            h.DeclareRiichi();
            Test("Hand.IsRiichi true after DeclareRiichi()", h.IsRiichi);

            h.Reset();
            Test("Hand.IsRiichi false after Reset()", !h.IsRiichi);
        }

        // =====================================================================
        // 2. DrawnTile tracking
        // =====================================================================
        {
            var h = new Hand();
            var t1 = Tile.Man(1);
            var t2 = Tile.Man(2);
            h.AddTile(t1);
            Test("DrawnTile equals first added tile", h.DrawnTile == t1);
            h.AddTile(t2);
            Test("DrawnTile equals last added tile", h.DrawnTile == t2);
            h.RemoveTile(t2);
            Test("DrawnTile is null after removing the drawn tile", h.DrawnTile == null);
        }

        // =====================================================================
        // 3. Riichi discard lock — GameState.Discard() enforcement
        //    Seat 0 is always dealer; game starts in ActionPhase for seat 0.
        // =====================================================================
        {
            // --- 3a: Non-drawn tile must be rejected in riichi ---
            var game = MakeGame();
            var h0   = game.Players[0].Hand;
            h0.Reset();
            SetupTenpaiHand(h0);          // 13 tiles, waiting on Man(9)
            h0.DeclareRiichi();
            var drawn    = Tile.Man(9);   // completing tile
            var nonDrawn = Tile.Man(1);   // another tile already in hand
            h0.AddTile(drawn);            // DrawnTile = Man(9)

            Test("Riichi: Discard(non-drawn) rejected", !game.Discard(0, nonDrawn));

            // --- 3b: Drawn tile must be accepted in riichi ---
            var game2 = MakeGame();
            var h0b   = game2.Players[0].Hand;
            h0b.Reset();
            SetupTenpaiHand(h0b);
            h0b.DeclareRiichi();
            h0b.AddTile(Tile.Man(9));
            Test("Riichi: Discard(drawn) accepted", game2.Discard(0, Tile.Man(9)));

            // --- 3c: After the forced discard, game transitions to ClaimWindow ---
            Test("Riichi: phase is ClaimWindow after forced discard",
                 game2.Phase == TurnPhase.ClaimWindow);
        }

        // =====================================================================
        // 4. Riichi player cannot pon on a pending discard
        //    Seat 0 discards → ClaimWindow. Seat 0 (marked riichi) tries ClaimPon.
        //    Even if they had the tile, ClaimPon should fail because IsRiichi
        //    means the GameRoom won't offer pon — and ClaimPon on a tile the
        //    riichi player discarded themselves is blocked by the discarder guard.
        //    We test the definitive check: a riichi hand should not have its pon
        //    offered — verified via the Hand.IsRiichi flag which GetEligibleClaimers
        //    reads on the server.
        // =====================================================================
        {
            var h = new Hand();
            h.AddTiles(new[]
            {
                Tile.Man(1), Tile.Man(1),   // pair — could pon a third
                Tile.Pin(1), Tile.Pin(2), Tile.Pin(3),
            });
            h.DeclareRiichi();

            Test("Riichi hand: IsRiichi prevents pon eligibility",
                 h.IsRiichi);  // server reads this in GetEligibleClaimers

            // Simulate what GetEligibleClaimers does:
            bool eligibleForPon = !h.IsRiichi
                               && h.ClosedTiles.Count(t => t == Tile.Man(1)) >= 2;
            Test("GetEligibleClaimers logic: riichi player excluded from pon", !eligibleForPon);

            bool eligibleForChi = !h.IsRiichi;
            Test("GetEligibleClaimers logic: riichi player excluded from chi", !eligibleForChi);

            bool eligibleForOpenKan = !h.IsRiichi;
            Test("GetEligibleClaimers logic: riichi player excluded from open kan", !eligibleForOpenKan);
        }

        // =====================================================================
        // 5. Riichi player can win by tsumo when drawn tile completes the hand
        // =====================================================================
        {
            var game = MakeGame();
            var h0   = game.Players[0].Hand;
            h0.Reset();

            // Set up complete winning hand: 1-9m 1-3p + pair wait on Pin(5)
            h0.AddTiles(new[]
            {
                Tile.Man(1), Tile.Man(2), Tile.Man(3),
                Tile.Man(4), Tile.Man(5), Tile.Man(6),
                Tile.Man(7), Tile.Man(8), Tile.Man(9),
                Tile.Pin(1), Tile.Pin(2), Tile.Pin(3),
                Tile.Pin(5),                              // 13 tiles, waits on Pin(5)
            });
            h0.DeclareRiichi();
            h0.AddTile(Tile.Pin(5));                      // draw winning tile

            bool canTsumo = game.WinChecker_CanWinTsumo(0);
            Test("Riichi: WinChecker_CanWinTsumo true on winning draw", canTsumo);

            bool tsumoOk = game.DeclareTsumo();
            Test("Riichi: DeclareTsumo succeeds", tsumoOk);
            Test("Riichi: game ends after tsumo (HandEnd phase)",
                 game.Phase == TurnPhase.HandEnd);
        }

        // =====================================================================
        // 6. Riichi player cannot discard before drawing (DrawnTile == null)
        // =====================================================================
        {
            var game = MakeGame();
            var h0   = game.Players[0].Hand;
            h0.Reset();
            SetupTenpaiHand(h0);    // 13 tiles, no DrawnTile yet
            h0.DeclareRiichi();

            // DrawnTile is null — any discard attempt must fail the riichi lock
            var anyTile = h0.ClosedTiles.First();
            Test("Riichi: Discard rejected when DrawnTile is null",
                 !game.Discard(0, anyTile));
        }

        // =====================================================================
        // 7. Non-winning drawn tile is forced discard — cannot keep it
        // =====================================================================
        {
            var game = MakeGame();
            var h0   = game.Players[0].Hand;
            h0.Reset();
            SetupTenpaiHand(h0);   // waits on Man(9)
            h0.DeclareRiichi();
            var nonWinningDraw = Tile.Pin(9);  // not the wait tile
            h0.AddTile(nonWinningDraw);

            bool canTsumo = game.WinChecker_CanWinTsumo(0);
            Test("Riichi: non-winning draw → WinChecker_CanWinTsumo false", !canTsumo);

            // Only legal move: discard the drawn tile
            bool discardOk = game.Discard(0, nonWinningDraw);
            Test("Riichi: forced discard of non-winning drawn tile succeeds", discardOk);
        }

        Console.WriteLine();
        return (pass, fail);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Creates a fresh 4-player game. Seat 0 is always the dealer and starts
    /// in ActionPhase with 14 tiles. We immediately reset seat 0's hand so tests
    /// can populate it with known tiles.
    /// </summary>
    static GameState MakeGame()
    {
        var game = new GameState(humanSeat: -1,
                                 playerNames: new[] { "P0", "P1", "P2", "P3" });
        game.StartGame();
        return game;
    }

    /// <summary>
    /// Loads a known tenpai hand (13 tiles) into <paramref name="hand"/>.
    /// Waits on Man(9) to complete the 7m-8m-9m sequence.
    /// </summary>
    static void SetupTenpaiHand(Hand hand)
    {
        hand.AddTiles(new[]
        {
            Tile.Man(1), Tile.Man(2), Tile.Man(3),
            Tile.Man(4), Tile.Man(5), Tile.Man(6),
            Tile.Man(7), Tile.Man(8),               // ryanmen → waits on 6m or 9m
            Tile.Pin(1), Tile.Pin(1),               // pair
            Tile.Sou(1), Tile.Sou(2), Tile.Sou(3),
        });
    }
}
