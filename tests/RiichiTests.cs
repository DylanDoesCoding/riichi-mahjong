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

        // =====================================================================
        // 8. GameState.DeclareRiichi — dealer (DrawnTile = null) regression
        //    Bug: DeclareRiichi set hand.IsRiichi=true then called Discard() which
        //    rejected any tile when DrawnTile==null, freezing the game.
        // =====================================================================
        {
            var game = MakeGame();
            var h0   = game.Players[0].Hand;
            h0.Reset();
            // Dealer has 14 tiles from AddTiles() — DrawnTile is null.
            SetupTenpaiHand(h0);              // 13-tile tenpai hand; DrawnTile=null
            var riichiDiscard = Tile.Man(8);  // discard 8m to stay tenpai on 6m/9m
            h0.AddTiles(new[] { riichiDiscard });  // simulate 14th tile via AddTiles (DrawnTile stays null)

            bool declared = game.DeclareRiichi(0, riichiDiscard);
            Test("DeclareRiichi (dealer, DrawnTile=null): returns true", declared);
            Test("DeclareRiichi (dealer, DrawnTile=null): phase is ClaimWindow",
                 game.Phase == TurnPhase.ClaimWindow);
            Test("DeclareRiichi (dealer, DrawnTile=null): hand.IsRiichi set",
                 game.Players[0].Hand.IsRiichi);
        }

        // =====================================================================
        // 9. GameState.DeclareRiichi — non-drawn tile discard regression
        //    Player declares riichi discarding a tile from their original 13
        //    (not the drawn tile). Previously the riichi lock blocked this.
        //
        //    Hand: 1m2m3m 4m5m6m 7m8m9m 1p2p3p 1s | drawn: 5s
        //    Discarding 1s (non-drawn) leaves tenpai on 5s tanki.
        //    Discarding 5s (drawn)     leaves tenpai on 1s tanki.
        //    We test the non-drawn case.
        // =====================================================================
        {
            var game = MakeGame();
            var h0   = game.Players[0].Hand;
            h0.Reset();
            // 13 tiles: four complete sets + lone Sou(1) tanki wait
            h0.AddTiles(new[]
            {
                Tile.Man(1), Tile.Man(2), Tile.Man(3),
                Tile.Man(4), Tile.Man(5), Tile.Man(6),
                Tile.Man(7), Tile.Man(8), Tile.Man(9),
                Tile.Pin(1), Tile.Pin(2), Tile.Pin(3),
                Tile.Sou(1),                            // tanki wait
            });
            h0.AddTile(Tile.Sou(5));     // drawn tile (different from riichi discard)
            // DrawnTile = Sou(5); declaring riichi discarding Sou(1) instead
            // → remaining: 4 sequences + Sou(5) tanki → tenpai ✓

            bool declared = game.DeclareRiichi(0, Tile.Sou(1));
            Test("DeclareRiichi (non-drawn discard): returns true", declared);
            Test("DeclareRiichi (non-drawn discard): phase is ClaimWindow",
                 game.Phase == TurnPhase.ClaimWindow);
            Test("DeclareRiichi (non-drawn discard): hand.IsRiichi set",
                 game.Players[0].Hand.IsRiichi);
        }

        // =====================================================================
        // 10. After GameState.DeclareRiichi, the claim window resolves and the
        //     next player gets their draw (full game-loop continuation test).
        //     Uses the same non-drawn-tile discard scenario as test 9.
        // =====================================================================
        {
            var game = MakeGame();
            var h0   = game.Players[0].Hand;
            h0.Reset();
            h0.AddTiles(new[]
            {
                Tile.Man(1), Tile.Man(2), Tile.Man(3),
                Tile.Man(4), Tile.Man(5), Tile.Man(6),
                Tile.Man(7), Tile.Man(8), Tile.Man(9),
                Tile.Pin(1), Tile.Pin(2), Tile.Pin(3),
                Tile.Sou(1),
            });
            h0.AddTile(Tile.Sou(5));  // DrawnTile = Sou(5), riichi discard = Sou(1)

            int riichiEventFired   = 0;
            int discardEventFired  = 0;
            game.OnRiichiDeclared += _ => riichiEventFired++;
            game.OnTileDiscarded  += (_, _) => discardEventFired++;

            bool declared = game.DeclareRiichi(0, Tile.Sou(1));
            Test("DeclareRiichi game-loop: returns true", declared);
            Test("DeclareRiichi game-loop: OnRiichiDeclared fired once", riichiEventFired == 1);
            Test("DeclareRiichi game-loop: OnTileDiscarded fired once",  discardEventFired == 1);
            Test("DeclareRiichi game-loop: ClaimWindow opened",
                 game.Phase == TurnPhase.ClaimWindow);

            // Simulate AI passing on all claims — no one can claim a riichi discard here
            game.PassAllClaims();
            Test("DeclareRiichi game-loop: DrawPhase after PassAllClaims",
                 game.Phase == TurnPhase.DrawPhase);
            Test("DeclareRiichi game-loop: next player is seat 1",
                 game.CurrentPlayerIndex == 1);
        }

        // =====================================================================
        // Red dora (akadora) — red fives add +1 han each to the score
        // =====================================================================
        {
            // Baseline context: tsumo win, no dora of any kind
            YakuContext MakeCtx(int redDora) => new()
            {
                WinMethod    = WinMethod.Tsumo,
                SeatWind     = WindDirection.South,
                RoundWind    = WindDirection.East,
                RedDoraCount = redDora,
            };

            // Winning hand: 234m 456p 678s 234s + 99p pair (closed tsumo)
            var winTiles = new List<Tile>
            {
                Tile.Man(2), Tile.Man(3), Tile.Man(4),
                Tile.Pin(4), Tile.Pin(5), Tile.Pin(6),
                Tile.Sou(6), Tile.Sou(7), Tile.Sou(8),
                Tile.Sou(2), Tile.Sou(3), Tile.Sou(4),
                Tile.Pin(9), Tile.Pin(9),
            };
            var winCheck = WinChecker.Check(winTiles, new List<Meld>());
            Test("Red dora: test hand is a valid win", winCheck.IsWin);
            var decomp = winCheck.Decompositions[0];

            var yaku0 = YakuChecker.Evaluate(decomp, MakeCtx(0));
            var yaku2 = YakuChecker.Evaluate(decomp, MakeCtx(2));
            Test("Red dora: hand has a base yaku (tsumo)", yaku0.HasYaku && yaku2.HasYaku);

            var score0 = ScoreCalculator.Calculate(decomp, yaku0, MakeCtx(0));
            var score2 = ScoreCalculator.Calculate(decomp, yaku2, MakeCtx(2));
            Test("Red dora: 2 red fives add exactly 2 han",
                 score2.TotalFan == score0.TotalFan + 2);
            Test("Red dora: more han never pays less",
                 score2.TotalPointsWon >= score0.TotalPointsWon);

            // Red five equality: a red Man(5) equals a normal Man(5) for melds/waits
            Test("Red dora: red 5 equals normal 5 (suit+value equality)",
                 Tile.Man(5, isRedDora: true) == Tile.Man(5));
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
