// =============================================================================
// FuritenTests.cs
// Missed-Ron (temporary) furiten must be recorded no matter HOW the claim window
// closes — not only when everyone passes.
//
// The bug this guards: a tenpai player waiting on a tile who declines the Ron
// becomes furiten (temporary; permanent if in riichi). GameState originally only
// recorded that in PassAllClaims, so if the window instead closed because a THIRD
// player pon'd / chi'd / kan'd the tile, the passing waiter wrongly stayed
// non-furiten and could illegally Ron a later identical tile.
//
// Setup convention (mirrors RuleGuardTests): seat 0 is dealer and discards; the
// waiting seat and the claiming seat are prepared BEFORE the discard.
// =============================================================================

using System;
using System.Collections.Generic;
using RiichiMahjong.Core;

static class FuritenTests
{
    public static (int pass, int fail) Run()
    {
        Console.WriteLine("[ Furiten — missed Ron must register however the window closes ]\n");
        int pass = 0, fail = 0;

        void Test(string name, bool result)
        {
            Console.WriteLine($"  {(result ? "✓" : "✗")}  {name}");
            if (result) pass++; else fail++;
        }

        // ---------------------------------------------------------------------
        // 1. Pon closes the window → a passing waiter is temporarily furiten.
        //    (This is the exact case the bug missed.)
        // ---------------------------------------------------------------------
        {
            var game = MakeGame();
            SeatWaitingOn5s(game, 2);          // seat 2 is tenpai waiting on 5s
            SeatCanPon5s(game, 1);             // seat 1 holds two 5s

            Test("Setup: seat 2 is tenpai", game.Players[2].Hand.IsTenpai());
            Test("Setup: seat 2 waits on 5s", game.Players[2].Hand.IsWaitingFor(Tile.Sou(5)));
            Test("Setup: seat 2 not yet furiten", !game.Players[2].Furiten.IsFuriten);

            OpenClaimWindow(game, Tile.Sou(5));   // seat 0 discards the winning tile
            Test("Setup: seat 1 pon succeeds", game.ClaimPon(1));

            Test("Pon window: passing waiter is temporarily furiten",
                 game.Players[2].Furiten.IsTemporaryFuriten);
            Test("Pon window: passing (non-riichi) waiter is NOT permanently furiten",
                 !game.Players[2].Furiten.IsPermanentFuriten);
        }

        // ---------------------------------------------------------------------
        // 2. A seat that was NOT waiting is untouched by the pon.
        // ---------------------------------------------------------------------
        {
            var game = MakeGame();
            SeatWaitingOn5s(game, 2);
            SeatCanPon5s(game, 1);
            game.Players[3].Hand.Reset();
            game.Players[3].Hand.AddTiles(Junk13());   // seat 3: nowhere near tenpai

            OpenClaimWindow(game, Tile.Sou(5));
            game.ClaimPon(1);

            Test("Pon window: a non-waiting seat is NOT furiten",
                 !game.Players[3].Furiten.IsFuriten);
        }

        // ---------------------------------------------------------------------
        // 3. A riichi waiter who is pon'd past gets PERMANENT furiten, not
        //    temporary — riichi locks the miss in for the rest of the hand.
        // ---------------------------------------------------------------------
        {
            var game = MakeGame();
            SeatWaitingOn5s(game, 2);
            game.Players[2].DeclaredRiichi = true;
            SeatCanPon5s(game, 1);

            OpenClaimWindow(game, Tile.Sou(5));
            game.ClaimPon(1);

            Test("Pon window: a riichi waiter is PERMANENTLY furiten",
                 game.Players[2].Furiten.IsPermanentFuriten);
        }

        // ---------------------------------------------------------------------
        // 4. Chi closes the window → the same rule applies to a third-seat waiter.
        //    (Chi is only from the left, so seat 1 chi's seat 0's discard.)
        // ---------------------------------------------------------------------
        {
            var game = MakeGame();
            SeatWaitingOn5s(game, 2);
            // seat 1 forms a 4s-6s kanchan chi around the discarded 5s
            var h1 = game.Players[1].Hand;
            h1.Reset();
            h1.AddTiles(new List<Tile>
            {
                Tile.Sou(4), Tile.Sou(6),
                Tile.Man(1), Tile.Man(6), Tile.Man(9),
                Tile.Pin(1), Tile.Pin(4), Tile.Pin(9),
                Tile.Sou(1), Tile.Sou(3),
                Tile.Wind(WindDirection.East), Tile.Wind(WindDirection.North),
                Tile.Dragon(DragonType.Green),
            });

            OpenClaimWindow(game, Tile.Sou(5));
            Test("Setup: seat 1 chi succeeds", game.ClaimChi(1, Tile.Sou(4), Tile.Sou(6)));
            Test("Chi window: passing waiter is temporarily furiten",
                 game.Players[2].Furiten.IsTemporaryFuriten);
        }

        // ---------------------------------------------------------------------
        // 5. Regression: everyone passing still records furiten (the path that
        //    always worked — kept honest through the shared helper refactor).
        // ---------------------------------------------------------------------
        {
            var game = MakeGame();
            SeatWaitingOn5s(game, 2);

            OpenClaimWindow(game, Tile.Sou(5));
            game.PassAllClaims();

            Test("Pass-all: passing waiter is temporarily furiten",
                 game.Players[2].Furiten.IsTemporaryFuriten);
        }

        Console.WriteLine();
        return (pass, fail);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    static GameState MakeGame()
    {
        var game = new GameState(humanSeat: -1,
                                 playerNames: new[] { "P0", "P1", "P2", "P3" });
        game.StartGame();
        return game;
    }

    /// <summary>Put the game into a ClaimWindow by having seat 0 discard the tile.</summary>
    static void OpenClaimWindow(GameState game, Tile discard)
    {
        var h0 = game.Players[0].Hand;
        h0.Reset();
        h0.AddTiles(Junk13());
        h0.AddTile(discard);
        if (!game.Discard(0, discard))
            throw new InvalidOperationException("Test setup: seat 0 discard failed");
    }

    /// <summary>
    /// Give <paramref name="seat"/> a 13-tile hand that is tenpai waiting on 5s
    /// (a 4s-6s kanchan), matching the shape RuleGuardTests already relies on.
    /// </summary>
    static void SeatWaitingOn5s(GameState game, int seat)
    {
        var h = game.Players[seat].Hand;
        h.Reset();
        h.AddTiles(new List<Tile>
        {
            Tile.Man(2), Tile.Man(3), Tile.Man(4),
            Tile.Pin(4), Tile.Pin(5), Tile.Pin(6),
            Tile.Sou(6), Tile.Sou(7), Tile.Sou(8),
            Tile.Sou(4), Tile.Sou(6),
            Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West),
        });
    }

    /// <summary>Give <paramref name="seat"/> two 5s plus junk so it can pon a discarded 5s.</summary>
    static void SeatCanPon5s(GameState game, int seat)
    {
        var h = game.Players[seat].Hand;
        h.Reset();
        h.AddTiles(new List<Tile>
        {
            Tile.Sou(5), Tile.Sou(5),
            Tile.Man(1), Tile.Man(6), Tile.Man(9),
            Tile.Pin(1), Tile.Pin(4), Tile.Pin(9),
            Tile.Sou(1), Tile.Sou(3),
            Tile.Wind(WindDirection.East), Tile.Wind(WindDirection.North),
            Tile.Dragon(DragonType.Green),
        });
    }

    static List<Tile> Junk13() => new()
    {
        Tile.Man(1), Tile.Man(6), Tile.Man(9),
        Tile.Pin(1), Tile.Pin(4), Tile.Pin(9),
        Tile.Sou(1), Tile.Sou(3), Tile.Sou(7),
        Tile.Wind(WindDirection.East), Tile.Wind(WindDirection.North),
        Tile.Dragon(DragonType.Green), Tile.Dragon(DragonType.Red),
    };
}
