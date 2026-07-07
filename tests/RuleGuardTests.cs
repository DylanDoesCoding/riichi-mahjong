// =============================================================================
// RuleGuardTests.cs
// Negative tests: every way a player (or buggy/malicious client) could attempt
// an ILLEGAL play must be rejected by GameState with no state change.
//
// The existing suites prove legal plays work; this suite proves illegal ones
// don't. Each block sets up a real game, attempts the violation, and asserts
// both the rejection and that the game state was not corrupted.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RiichiMahjong.Core;

static class RuleGuardTests
{
    public static (int pass, int fail) Run()
    {
        Console.WriteLine("[ Rule Guards — illegal plays must be rejected ]\n");
        int pass = 0, fail = 0;

        void Test(string name, bool result)
        {
            Console.WriteLine($"  {(result ? "✓" : "✗")}  {name}");
            if (result) pass++; else fail++;
        }

        // =====================================================================
        // 1. Turn ownership — only the current player may act
        // =====================================================================
        {
            var game = MakeGame();
            var t = Tile.Man(5);
            game.Players[1].Hand.Reset();
            game.Players[1].Hand.AddTiles(Junk13());
            game.Players[1].Hand.AddTile(t);

            Test("Guard: out-of-turn discard rejected", !game.Discard(1, t));
            Test("Guard: out-of-turn discard leaves phase unchanged",
                 game.Phase == TurnPhase.ActionPhase);
            Test("Guard: out-of-turn discard leaves current player unchanged",
                 game.CurrentPlayerIndex == 0);
        }

        // =====================================================================
        // 2. Phantom tiles — cannot discard a tile you don't hold
        // =====================================================================
        {
            var game = MakeGame();
            var h0 = game.Players[0].Hand;
            h0.Reset();
            h0.AddTiles(Junk13());
            h0.AddTile(Tile.Man(5));               // 14 tiles, no Sou(9) anywhere

            Test("Guard: discarding a tile not in hand rejected",
                 !game.Discard(0, Tile.Sou(9)));
            Test("Guard: hand unchanged after phantom discard",
                 h0.ClosedTiles.Count == 14);
            Test("Guard: phase unchanged after phantom discard",
                 game.Phase == TurnPhase.ActionPhase);
        }

        // =====================================================================
        // 3. False tsumo — declaring a win without a winning hand
        // =====================================================================
        {
            var game = MakeGame();
            var h0 = game.Players[0].Hand;
            h0.Reset();
            h0.AddTiles(Junk13());
            h0.AddTile(Tile.Dragon(DragonType.White));  // hopeless 14-tile hand

            Test("Guard: tsumo without a winning hand rejected", !game.DeclareTsumo());
            Test("Guard: phase unchanged after false tsumo",
                 game.Phase == TurnPhase.ActionPhase);
        }

        // =====================================================================
        // 4. Pon guards
        // =====================================================================
        {
            // 4a: pon in the wrong phase (no discard pending)
            var game = MakeGame();
            Test("Guard: pon during ActionPhase rejected", !game.ClaimPon(1));

            // 4b: discarder cannot pon their own tile
            game = MakeGame();
            OpenClaimWindow(game, Tile.Man(4));
            Test("Guard: discarder cannot pon own discard", !game.ClaimPon(0));

            // 4c: pon without two matching copies
            game = MakeGame();
            var h2 = game.Players[2].Hand;
            h2.Reset();
            h2.AddTiles(Junk13());                 // contains no Man(4) pair
            OpenClaimWindow(game, Tile.Man(4));
            Test("Guard: pon with fewer than 2 copies rejected", !game.ClaimPon(2));

            // 4d: a riichi player cannot pon (hand is locked)
            game = MakeGame();
            var h1 = game.Players[1].Hand;
            h1.Reset();
            var riichiTiles = Junk13();
            riichiTiles[0] = Tile.Man(4);
            riichiTiles[1] = Tile.Man(4);          // pon material
            h1.AddTiles(riichiTiles);
            h1.DeclareRiichi();
            game.Players[1].DeclaredRiichi = true;
            OpenClaimWindow(game, Tile.Man(4));
            Test("Guard: riichi player cannot pon", !game.ClaimPon(1));
            Test("Guard: riichi pon attempt leaves hand closed",
                 game.Players[1].Hand.OpenMelds.Count == 0);
        }

        // =====================================================================
        // 5. Chi guards
        // =====================================================================
        {
            // 5a: chi only from the player to the discarder's right (leftOf claimer)
            var game = MakeGame();
            var h2 = game.Players[2].Hand;
            h2.Reset();
            var chiTiles = Junk13();
            chiTiles[0] = Tile.Man(2);
            chiTiles[1] = Tile.Man(3);
            h2.AddTiles(chiTiles);
            OpenClaimWindow(game, Tile.Man(4));
            Test("Guard: chi from non-left seat rejected (seat 2 vs discarder 0)",
                 !game.ClaimChi(2, Tile.Man(2), Tile.Man(3)));

            // 5b: chi tiles must form a run with the discard
            game = MakeGame();
            var h1 = game.Players[1].Hand;
            h1.Reset();
            var badRun = Junk13();
            badRun[0] = Tile.Man(2);
            badRun[1] = Tile.Man(7);
            h1.AddTiles(badRun);
            OpenClaimWindow(game, Tile.Man(4));
            Test("Guard: chi with non-sequence tiles rejected",
                 !game.ClaimChi(1, Tile.Man(2), Tile.Man(7)));

            // 5c: chi with tiles the claimer doesn't hold
            game = MakeGame();
            h1 = game.Players[1].Hand;
            h1.Reset();
            h1.AddTiles(Junk13());                 // no Man(2)/Man(3)
            OpenClaimWindow(game, Tile.Man(4));
            Test("Guard: chi with tiles not in hand rejected",
                 !game.ClaimChi(1, Tile.Man(2), Tile.Man(3)));

            // 5d: riichi player cannot chi
            game = MakeGame();
            h1 = game.Players[1].Hand;
            h1.Reset();
            var riichiChi = Junk13();
            riichiChi[0] = Tile.Man(2);
            riichiChi[1] = Tile.Man(3);
            h1.AddTiles(riichiChi);
            h1.DeclareRiichi();
            game.Players[1].DeclaredRiichi = true;
            OpenClaimWindow(game, Tile.Man(4));
            Test("Guard: riichi player cannot chi",
                 !game.ClaimChi(1, Tile.Man(2), Tile.Man(3)));
        }

        // =====================================================================
        // 6. Daiminkan guards
        // =====================================================================
        {
            var game = MakeGame();
            var h1 = game.Players[1].Hand;
            h1.Reset();
            var twoCopies = Junk13();
            twoCopies[0] = Tile.Man(4);
            twoCopies[1] = Tile.Man(4);            // only 2 copies — kan needs 3
            h1.AddTiles(twoCopies);
            OpenClaimWindow(game, Tile.Man(4));
            Test("Guard: daiminkan with fewer than 3 copies rejected",
                 !game.ClaimDaiminkan(1));

            game = MakeGame();
            h1 = game.Players[1].Hand;
            h1.Reset();
            var threeCopies = Junk13();
            threeCopies[0] = Tile.Man(4);
            threeCopies[1] = Tile.Man(4);
            threeCopies[2] = Tile.Man(4);
            h1.AddTiles(threeCopies);
            h1.DeclareRiichi();
            OpenClaimWindow(game, Tile.Man(4));
            Test("Guard: riichi player cannot daiminkan", !game.ClaimDaiminkan(1));
        }

        // =====================================================================
        // 7. Yakuless ron — a winning SHAPE without any yaku cannot win
        //    Seat 1: 234m 456p 678s 4s6s + WW (kanchan 5s wait, closed, no riichi)
        //    Ron on 5s: no pinfu (kanchan), no tanyao (honour pair), West is
        //    valueless for seat 1 in an East round → zero yaku.
        // =====================================================================
        {
            var game = MakeGame();
            var h1 = game.Players[1].Hand;
            h1.Reset();
            h1.AddTiles(new List<Tile>
            {
                Tile.Man(2), Tile.Man(3), Tile.Man(4),
                Tile.Pin(4), Tile.Pin(5), Tile.Pin(6),
                Tile.Sou(6), Tile.Sou(7), Tile.Sou(8),
                Tile.Sou(4), Tile.Sou(6),
                Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West),
            });
            // Simulate mid-hand: a pre-first-draw ron would legitimately score
            // Renhou (Blessing of Man), which is exactly what we DON'T want here.
            game.Players[1].WallDrawCount = 3;
            Test("Guard setup: yakuless hand is tenpai", h1.IsTenpai());
            Test("Guard setup: yakuless hand waits on 5s", h1.IsWaitingFor(Tile.Sou(5)));

            OpenClaimWindow(game, Tile.Sou(5));
            Test("Guard: yakuless ron rejected", !game.ClaimRonMulti(new[] { 1 }));
            Test("Guard: game still in ClaimWindow after yakuless ron attempt",
                 game.Phase == TurnPhase.ClaimWindow);
            Test("Guard: yakuless claimer's hand back to 13 tiles",
                 h1.ClosedTiles.Count == 13);
        }

        // =====================================================================
        // 8. Ron guards — discarder cannot ron own tile
        // =====================================================================
        {
            var game = MakeGame();
            OpenClaimWindow(game, Tile.Man(4));
            Test("Guard: discarder cannot ron own discard",
                 !game.ClaimRonMulti(new[] { 0 }));
        }

        // =====================================================================
        // 9. Riichi guards — open hand and insufficient points
        // =====================================================================
        {
            // 9a: open hand cannot riichi
            var game = MakeGame();
            var h0 = game.Players[0].Hand;
            h0.Reset();
            var open = TenpaiTiles13();
            open.Add(Tile.Man(9));                 // 14th tile (drawn)
            h0.AddTiles(open);
            h0.ApplyPon(Tile.Man(1), Tile.Man(1), ClaimSource.Left);  // opens the hand
            Test("Guard setup: hand has an open meld", h0.OpenMelds.Any(m => m.IsOpen));
            Test("Guard: riichi with an open hand rejected",
                 !game.DeclareRiichi(0, Tile.Man(9)));

            // 9b: fewer than 1000 points cannot riichi
            game = MakeGame();
            h0 = game.Players[0].Hand;
            h0.Reset();
            var closed = TenpaiTiles13();
            closed.Add(Tile.Sou(9));               // discard candidate that keeps tenpai? use drawn junk
            h0.AddTiles(closed);
            game.Players[0].Points = 900;
            Test("Guard: riichi with under 1000 points rejected",
                 !game.DeclareRiichi(0, Tile.Sou(9)));
            Test("Guard: no riichi bet taken on rejected declaration",
                 game.Players[0].Points == 900);
        }

        // =====================================================================
        // 10. Kan declaration guards
        // =====================================================================
        {
            var game = MakeGame();
            var h0 = game.Players[0].Hand;
            h0.Reset();
            var three = Junk13();
            three[0] = Tile.Man(1);
            three[1] = Tile.Man(1);
            three[2] = Tile.Man(1);                // only 3 copies (Junk13 slot 0-2 replaced)
            h0.AddTiles(three);
            h0.AddTile(Tile.Sou(9));
            Test("Guard: ankan with only 3 copies rejected",
                 !game.DeclareAnkan(0, Tile.Man(1)));

            Test("Guard: kakan without a matching pon rejected",
                 !game.DeclareKakan(0, Tile.Man(1)));

            // Out-of-turn kan
            var game2 = MakeGame();
            var h1 = game2.Players[1].Hand;
            h1.Reset();
            var four = new List<Tile>
            {
                Tile.Man(1), Tile.Man(1), Tile.Man(1), Tile.Man(1),
                Tile.Pin(1), Tile.Pin(2), Tile.Pin(3),
                Tile.Sou(1), Tile.Sou(2), Tile.Sou(3),
                Tile.Pin(7), Tile.Pin(8), Tile.Pin(9),
            };
            h1.AddTiles(four);
            Test("Guard: out-of-turn ankan rejected (not seat 1's turn)",
                 !game2.DeclareAnkan(1, Tile.Man(1)));
        }

        Console.WriteLine();
        return (pass, fail);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>Fresh game — seat 0 is dealer, ActionPhase, 14 dealt tiles.</summary>
    static GameState MakeGame()
        => NewStarted();

    static GameState NewStarted()
    {
        var game = new GameState(humanSeat: -1,
                                 playerNames: new[] { "P0", "P1", "P2", "P3" });
        game.StartGame();
        return game;
    }

    /// <summary>
    /// Put the game into a ClaimWindow: reset seat 0's hand to junk + the given
    /// tile, then discard it. Callers set up claimants BEFORE calling this.
    /// </summary>
    static void OpenClaimWindow(GameState game, Tile discard)
    {
        var h0 = game.Players[0].Hand;
        h0.Reset();
        h0.AddTiles(Junk13());
        h0.AddTile(discard);
        if (!game.Discard(0, discard))
            throw new InvalidOperationException("Test setup: seat 0 discard failed");
    }

    /// <summary>13 disconnected tiles — nowhere near tenpai, no pairs with Man(4).</summary>
    static List<Tile> Junk13() => new()
    {
        Tile.Man(1), Tile.Man(6), Tile.Man(9),
        Tile.Pin(1), Tile.Pin(4), Tile.Pin(9),
        Tile.Sou(1), Tile.Sou(3), Tile.Sou(7),
        Tile.Wind(WindDirection.East), Tile.Wind(WindDirection.North),
        Tile.Dragon(DragonType.Green), Tile.Dragon(DragonType.Red),
    };

    /// <summary>
    /// 13 tiles in tenpai (123m 456m 789p 123s + 1m pair-wait shape):
    /// 111m contains pon material for the open-hand riichi test.
    /// </summary>
    static List<Tile> TenpaiTiles13() => new()
    {
        Tile.Man(1), Tile.Man(1), Tile.Man(1),
        Tile.Man(4), Tile.Man(5), Tile.Man(6),
        Tile.Pin(7), Tile.Pin(8), Tile.Pin(9),
        Tile.Sou(1), Tile.Sou(2), Tile.Sou(3),
        Tile.Man(9),
    };
}
