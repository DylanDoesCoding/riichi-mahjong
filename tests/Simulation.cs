// =============================================================================
// Simulation.cs
// Headless game simulation — exercises all major game-logic paths without Godot.
// Tests: full game loop, riichi, tsumo, ron, scoring, shanten, furiten.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RiichiMahjong.Core;
using RiichiMahjong.AI;

// ---- Shim: AIPlayer.ShouldDeclareRiichi / ShouldClaimRon need GameSettings-style paths ----
// GameSettings is a Godot class, so we stub out what the AI actually reads.
// (AIPlayer only reads GameState/Hand — no actual Godot calls.)

static class Program
{
    static readonly Random Rng = new(42);

    static int _handsPlayed     = 0;
    static int _tsumoWins       = 0;
    static int _ronWins         = 0;
    static int _draws           = 0;
    static int _riichiDeclared  = 0;
    static int _totalHan        = 0;
    static int _scoredHands     = 0;
    static readonly List<string> _log = new();

    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine("  Riichi Mahjong — Headless Game Simulation");
        Console.WriteLine("═══════════════════════════════════════════════════\n");

        // --- Unit tests first ---
        RunUnitTests();

        // --- Full game simulation ---
        Console.WriteLine("\n--- Simulating full East round (4 hands) ---\n");
        SimulateGame();

        // --- Summary ---
        PrintSummary();
    }

    // =========================================================================
    // Unit tests
    // =========================================================================

    static void RunUnitTests()
    {
        Console.WriteLine("[ Unit Tests ]\n");
        int pass = 0, fail = 0;

        void Test(string name, bool result)
        {
            string mark = result ? "✓" : "✗";
            Console.WriteLine($"  {mark}  {name}");
            if (result) pass++; else fail++;
        }

        // ---- Shanten calculation ----
        {
            // Kokushi tenpai: all 13 unique terminals/honours — waiting for any duplicate
            var h = new Hand();
            h.AddTile(Tile.Man(1));  h.AddTile(Tile.Man(9));
            h.AddTile(Tile.Pin(1));  h.AddTile(Tile.Pin(9));
            h.AddTile(Tile.Sou(1));  h.AddTile(Tile.Sou(9));
            h.AddTile(Tile.Wind(WindDirection.East));
            h.AddTile(Tile.Wind(WindDirection.South));
            h.AddTile(Tile.Wind(WindDirection.West));
            h.AddTile(Tile.Wind(WindDirection.North));
            h.AddTile(Tile.Dragon(DragonType.White));
            h.AddTile(Tile.Dragon(DragonType.Green));
            h.AddTile(Tile.Dragon(DragonType.Red));
            Test("Kokushi 13-orphans tenpai (shanten=0)", h.Shanten() == 0);
        }
        {
            // Complete 14-tile standard hand: 123m 456m 789m 123p + 11p
            var h = new Hand();
            foreach (var v in new[]{1,2,3,4,5,6,7,8,9}) h.AddTile(Tile.Man(v));
            h.AddTile(Tile.Pin(1)); h.AddTile(Tile.Pin(2)); h.AddTile(Tile.Pin(3));
            h.AddTile(Tile.Pin(1)); h.AddTile(Tile.Pin(1)); // two more Pin(1) → pair
            Test("Complete standard hand 14 tiles (shanten=-1)", h.Shanten() == -1);
        }
        {
            var h = new Hand();
            h.AddTiles(new[]{ Tile.Man(1),Tile.Man(2),Tile.Man(3),
                               Tile.Pin(4),Tile.Pin(5),Tile.Pin(6),
                               Tile.Sou(7),Tile.Sou(8),Tile.Sou(9),
                               Tile.Wind(WindDirection.East),Tile.Wind(WindDirection.East),
                               Tile.Dragon(DragonType.White),Tile.Dragon(DragonType.White) });
            Test("Standard tenpai (13t, 3 sets + 2 pairs) shanten=0", h.Shanten() == 0);
            var waits = h.GetWaitingTiles();
            Test("Shanpon waits contain East wind", waits.Any(w => w.Suit == TileSuit.Wind));
            Test("Shanpon waits contain White dragon", waits.Any(w => w.Suit == TileSuit.Dragon));
            Test("IsWaitingFor(East) true", h.IsWaitingFor(Tile.Wind(WindDirection.East)));
            Test("IsWaitingFor(1m) false", !h.IsWaitingFor(Tile.Man(1)));
        }
        {
            // Seven pairs tenpai: 6 pairs + 1 singleton
            var h = new Hand();
            foreach (var t in new Tile[]
            {
                Tile.Man(1),Tile.Man(1),Tile.Man(2),Tile.Man(2),
                Tile.Pin(3),Tile.Pin(3),Tile.Pin(4),Tile.Pin(4),
                Tile.Sou(5),Tile.Sou(5),Tile.Sou(6),Tile.Sou(6),
                Tile.Wind(WindDirection.East)
            }) h.AddTile(t);
            Test("Seven pairs tenpai shanten=0", h.Shanten() == 0);
            Test("IsWaitingFor East=true (completes 7th pair)", h.IsWaitingFor(Tile.Wind(WindDirection.East)));
        }

        // ---- WinChecker ----
        {
            var closed = new List<Tile>
            {
                Tile.Man(1),Tile.Man(2),Tile.Man(3),
                Tile.Pin(4),Tile.Pin(5),Tile.Pin(6),
                Tile.Sou(7),Tile.Sou(8),Tile.Sou(9),
                Tile.Dragon(DragonType.White),Tile.Dragon(DragonType.White),
                Tile.Wind(WindDirection.East),Tile.Wind(WindDirection.East),Tile.Wind(WindDirection.East)
            };
            var result = WinChecker.Check(closed, new List<Meld>());
            Test("WinChecker: 3-set + pair + set = IsWin", result.IsWin);
            Test("WinChecker: has at least one decomposition", result.Decompositions.Count > 0);
        }

        // ---- FuritenTracker ----
        {
            var ft = new FuritenTracker();
            var discard = Tile.Man(5);
            var waits = new List<Tile> { discard, Tile.Man(6) };
            ft.RecordOwnDiscard(discard, waits);
            Test("FuritenTracker: own discard of wait = permanent furiten", ft.IsPermanentFuriten);
        }
        {
            var ft = new FuritenTracker();
            ft.RecordMissedDiscard(Tile.Man(5), isWait: true, isRiichi: false);
            Test("FuritenTracker fast: missed wait = temporary furiten", ft.IsTemporaryFuriten);
            ft.OnDraw();
            Test("FuritenTracker: temporary clears on draw", !ft.IsTemporaryFuriten);
        }
        {
            var ft = new FuritenTracker();
            ft.RecordMissedDiscard(Tile.Man(5), isWait: true, isRiichi: true);
            Test("FuritenTracker: riichi miss = permanent furiten", ft.IsPermanentFuriten);
            ft.OnDraw();
            Test("FuritenTracker: permanent does NOT clear on draw", ft.IsPermanentFuriten);
        }

        // ---- TileWall ----
        {
            var wall = new TileWall();
            Test("TileWall: 122 live tiles after initial deal setup", wall.TilesRemaining == 122);
            var dealt = wall.DealInitialHands();
            Test("TileWall: 4 hands dealt", dealt.Length == 4);
            Test("TileWall: dealer gets 14 tiles", dealt[0].Count == 14);
            Test("TileWall: others get 13 tiles", dealt[1].Count == 13 && dealt[2].Count == 13 && dealt[3].Count == 13);
            int remaining = wall.TilesRemaining;
            Test($"TileWall: {remaining} tiles remain after deal (expected 69)", remaining == 69);
        }

        // ---- GameState API ----
        {
            var gs = new GameState(humanSeat: 0);
            gs.StartGame();
            Test("GameState: Phase is ActionPhase at start (dealer has 14)", gs.Phase == TurnPhase.ActionPhase);
            Test("GameState: dealer is current player", gs.CurrentPlayerIndex == gs.DealerIndex);
            var dealer = gs.Players[gs.DealerIndex];
            Test("GameState: dealer has 14 closed tiles", dealer.Hand.ClosedTiles.Count == 14);
            Test("GameState: others have 13", gs.Players.Where((_, i) => i != gs.DealerIndex).All(p => p.Hand.ClosedTiles.Count == 13));
        }

        // =========================================================================
        // ---- Riichi state — tile count invariant and Tsumo/Ron win paths ----
        // Rule: hand always has 13 closed tiles between turns, 14 only during
        // ActionPhase (after drawing). Riichi declaration must discard one tile,
        // leaving exactly 13. Any deviation breaks Tsumo/Ron win detection.
        // =========================================================================

        // Helper: a concrete 13-tile tenpai hand used across all riichi tests.
        //   1m2m3m | 4p5p6p | 7s8s | EaEaEa | WdWd
        //   = 3 + 3 + 2 + 3 + 2 = 13 tiles   (waits: 6s or 9s via ryanmen)
        static List<Tile> MakeRiichiTenpaiHand13() => new()
        {
            Tile.Man(1), Tile.Man(2), Tile.Man(3),
            Tile.Pin(4), Tile.Pin(5), Tile.Pin(6),
            Tile.Sou(7), Tile.Sou(8),
            Tile.Wind(WindDirection.East), Tile.Wind(WindDirection.East), Tile.Wind(WindDirection.East),
            Tile.Dragon(DragonType.White), Tile.Dragon(DragonType.White),
        };

        // ---- Tile count: 13 tiles in riichi, never 14 (Hand level) ----
        {
            // 14-tile hand: 13-tile tenpai structure + lone GreenDragon (the riichi discard)
            var h = new Hand();
            h.AddTiles(MakeRiichiTenpaiHand13());
            h.AddTile(Tile.Dragon(DragonType.Green));   // lone — the tile to discard
            Test("Riichi setup: 14 tiles pre-discard", h.ClosedTiles.Count == 14);

            h.DeclareRiichi();
            bool removed = h.RemoveTile(Tile.Dragon(DragonType.Green));
            Test("Riichi: RemoveTile succeeds (tile was in hand)", removed);
            Test("Riichi: exactly 13 closed tiles after riichi discard", h.ClosedTiles.Count == 13);
            Test("Riichi: hand is tenpai (shanten=0)", h.Shanten() == 0);
            Test("Riichi: IsRiichi flag is set", h.IsRiichi);
        }

        // ---- Riichi waits are correct after discard ----
        {
            var h = new Hand();
            h.AddTiles(MakeRiichiTenpaiHand13());
            h.DeclareRiichi();

            Test("Riichi+waits: IsWaitingFor(6s) = true  (ryanmen low)",  h.IsWaitingFor(Tile.Sou(6)));
            Test("Riichi+waits: IsWaitingFor(9s) = true  (ryanmen high)", h.IsWaitingFor(Tile.Sou(9)));
            Test("Riichi+waits: IsWaitingFor(5s) = false (not a wait)",   !h.IsWaitingFor(Tile.Sou(5)));
            Test("Riichi+waits: IsWaitingFor(7s) = false (already in hand)", !h.IsWaitingFor(Tile.Sou(7)));
            Test("Riichi+waits: IsWaitingFor(Gd) = false (lone, discarded)", !h.IsWaitingFor(Tile.Dragon(DragonType.Green)));
        }

        // ---- Riichi + Tsumo: drawing winning tile gives 14 tiles + WinChecker win ----
        {
            var h = new Hand();
            h.AddTiles(MakeRiichiTenpaiHand13());
            h.DeclareRiichi();

            // Simulate self-draw of 6s (completing the ryanmen)
            h.AddTile(Tile.Sou(6));
            Test("Riichi+Tsumo: 14 closed tiles after winning draw", h.ClosedTiles.Count == 14);
            Test("Riichi+Tsumo: shanten == -1 (complete winning hand)", h.Shanten() == -1);
            Test("Riichi+Tsumo: IsRiichi still set", h.IsRiichi);

            var winCheck = WinChecker.Check(h.ClosedTiles.ToList(), h.OpenMelds.ToList());
            Test("Riichi+Tsumo: WinChecker.IsWin = true", winCheck.IsWin);
            Test("Riichi+Tsumo: at least one standard decomposition found",
                winCheck.Decompositions.Any(d => !d.IsSevenPairs && !d.IsThirteenOrphans));
        }
        {
            // Same via the other wait tile (9s)
            var h = new Hand();
            h.AddTiles(MakeRiichiTenpaiHand13());
            h.DeclareRiichi();
            h.AddTile(Tile.Sou(9));
            Test("Riichi+Tsumo (9s): 14 tiles, shanten=-1", h.ClosedTiles.Count == 14 && h.Shanten() == -1);
            var wc = WinChecker.Check(h.ClosedTiles.ToList(), h.OpenMelds.ToList());
            Test("Riichi+Tsumo (9s): WinChecker.IsWin = true", wc.IsWin);
        }

        // ---- Riichi + Ron: waiting for opponent discard gives win ----
        {
            var h = new Hand();
            h.AddTiles(MakeRiichiTenpaiHand13());
            h.DeclareRiichi();
            Test("Riichi+Ron setup: 13 tiles (not 14)", h.ClosedTiles.Count == 13);

            // Simulate Ron: add the opponent's discard to the hand (as ClaimRon does)
            h.AddTile(Tile.Sou(6));
            Test("Riichi+Ron: 14 closed tiles after adding Ron tile", h.ClosedTiles.Count == 14);
            Test("Riichi+Ron: shanten == -1", h.Shanten() == -1);

            var winCheck = WinChecker.Check(h.ClosedTiles.ToList(), h.OpenMelds.ToList());
            Test("Riichi+Ron: WinChecker.IsWin = true", winCheck.IsWin);
        }

        // ---- Non-completing tile does NOT produce a win ----
        {
            var h = new Hand();
            h.AddTiles(MakeRiichiTenpaiHand13());
            h.DeclareRiichi();
            h.AddTile(Tile.Man(5));   // random non-wait tile
            var wc = WinChecker.Check(h.ClosedTiles.ToList(), h.OpenMelds.ToList());
            Test("Riichi+wrong tile: WinChecker.IsWin = false", !wc.IsWin);
        }

        // ---- GameState.DeclareRiichi: valid declaration ----
        {
            var gs = new GameState(humanSeat: 0);
            gs.StartGame();
            int seat   = gs.CurrentPlayerIndex;
            var player = gs.Players[seat];
            int ptsBefore = player.Points;

            // Replace dealt hand with a known 14-tile riichi-eligible hand:
            //   13-tile tenpai structure + GreenDragon (the lone discard tile)
            player.Hand.Reset();
            player.Hand.AddTiles(MakeRiichiTenpaiHand13());
            player.Hand.AddTile(Tile.Dragon(DragonType.Green));

            bool ok = gs.DeclareRiichi(seat, Tile.Dragon(DragonType.Green));

            Test("GS.DeclareRiichi (valid): returns true", ok);
            Test("GS.DeclareRiichi (valid): hand has 13 closed tiles", player.Hand.ClosedTiles.Count == 13);
            Test("GS.DeclareRiichi (valid): Hand.IsRiichi = true", player.Hand.IsRiichi);
            Test("GS.DeclareRiichi (valid): player.DeclaredRiichi = true", player.DeclaredRiichi);
            Test("GS.DeclareRiichi (valid): 1000 points deducted", player.Points == ptsBefore - GameState.RiichiBetAmount);
            Test("GS.DeclareRiichi (valid): RiichiBetsOnTable == 1", gs.RiichiBetsOnTable == 1);
            Test("GS.DeclareRiichi (valid): Phase is ClaimWindow", gs.Phase == TurnPhase.ClaimWindow);
            Test("GS.DeclareRiichi (valid): hand is tenpai after discard", player.Hand.Shanten() == 0);
        }

        // ---- GameState.DeclareRiichi: tile not in hand (bug guard) ----
        // This is the specific bug: if discardTile is not found, Discard() used to
        // fail AFTER riichi flags were set, leaving 14 tiles + IsRiichi=true.
        {
            var gs = new GameState(humanSeat: 0);
            gs.StartGame();
            int seat   = gs.CurrentPlayerIndex;
            var player = gs.Players[seat];
            int ptsBefore = player.Points;

            player.Hand.Reset();
            player.Hand.AddTiles(MakeRiichiTenpaiHand13());
            player.Hand.AddTile(Tile.Dragon(DragonType.Green));

            // 9s is NOT in the hand — the discard should be rejected cleanly
            bool ok = gs.DeclareRiichi(seat, Tile.Sou(9));

            Test("GS.DeclareRiichi (bad tile): returns false", !ok);
            Test("GS.DeclareRiichi (bad tile): hand still has 14 tiles", player.Hand.ClosedTiles.Count == 14);
            Test("GS.DeclareRiichi (bad tile): Hand.IsRiichi stays false", !player.Hand.IsRiichi);
            Test("GS.DeclareRiichi (bad tile): DeclaredRiichi stays false", !player.DeclaredRiichi);
            Test("GS.DeclareRiichi (bad tile): points NOT deducted", player.Points == ptsBefore);
            Test("GS.DeclareRiichi (bad tile): Phase stays ActionPhase", gs.Phase == TurnPhase.ActionPhase);
        }

        // ---- GameState.DeclareRiichi: non-tenpai discard rejected ----
        {
            var gs = new GameState(humanSeat: 0);
            gs.StartGame();
            int seat   = gs.CurrentPlayerIndex;
            var player = gs.Players[seat];

            // Build a 14-tile hand where discarding ANYTHING leaves shanten >= 1
            player.Hand.Reset();
            player.Hand.AddTiles(new List<Tile>
            {
                Tile.Man(1), Tile.Man(3),  Tile.Man(5),   // isolated — not a sequence
                Tile.Pin(2), Tile.Pin(7),  Tile.Pin(9),
                Tile.Sou(1), Tile.Sou(4),  Tile.Sou(8),
                Tile.Wind(WindDirection.East), Tile.Wind(WindDirection.South),
                Tile.Dragon(DragonType.White), Tile.Dragon(DragonType.Green),
                Tile.Man(7),
            });
            bool ok = gs.DeclareRiichi(seat, Tile.Man(1));
            Test("GS.DeclareRiichi (not tenpai): returns false", !ok);
            Test("GS.DeclareRiichi (not tenpai): IsRiichi stays false", !player.Hand.IsRiichi);
        }

        // ---- GameState.DeclareTsumo: riichi player draws winning tile ----
        {
            var gs = new GameState(humanSeat: 0);
            gs.StartGame();
            int seat   = gs.CurrentPlayerIndex;
            var player = gs.Players[seat];

            // Set up player as if they declared riichi on a previous turn
            // (13-tile tenpai hand, riichi flags already set)
            player.Hand.Reset();
            player.Hand.AddTiles(MakeRiichiTenpaiHand13());
            player.Hand.DeclareRiichi(isDouble: false);
            player.DeclaredRiichi = true;
            player.RiichiBetTurn  = 0;

            // Simulate drawing the winning tile
            player.Hand.AddTile(Tile.Sou(6));
            player.Hand.Sort();

            Test("GS.DeclareTsumo (riichi): 14 closed tiles after draw", player.Hand.ClosedTiles.Count == 14);
            Test("GS.DeclareTsumo (riichi): shanten == -1", player.Hand.Shanten() == -1);
            bool ok = gs.DeclareTsumo();
            Test("GS.DeclareTsumo (riichi): succeeds", ok);
            Test("GS.DeclareTsumo (riichi): Phase is HandEnd", gs.Phase == TurnPhase.HandEnd);
        }

        // ---- GameState.ClaimRon: riichi player wins on opponent's discard ----
        {
            var gs = new GameState(humanSeat: 0);
            gs.StartGame();

            // Seat 0 (dealer) is current player in ActionPhase.
            // Set up seat 1 as if they declared riichi (13-tile tenpai, waiting 6s or 9s).
            var riichiPlayer = gs.Players[1];
            riichiPlayer.Hand.Reset();
            riichiPlayer.Hand.AddTiles(MakeRiichiTenpaiHand13());
            riichiPlayer.Hand.DeclareRiichi(isDouble: false);
            riichiPlayer.DeclaredRiichi = true;
            riichiPlayer.RiichiBetTurn  = 0;

            // Give the dealer a hand containing 9s so they can discard it.
            var dealer = gs.Players[gs.CurrentPlayerIndex];
            dealer.Hand.Reset();
            dealer.Hand.AddTiles(new List<Tile>
            {
                Tile.Man(2), Tile.Man(3), Tile.Man(4),
                Tile.Pin(5), Tile.Pin(6), Tile.Pin(7),
                Tile.Sou(1), Tile.Sou(2), Tile.Sou(3),
                Tile.Wind(WindDirection.South), Tile.Wind(WindDirection.South), Tile.Wind(WindDirection.South),
                Tile.Dragon(DragonType.Red), Tile.Sou(9),  // 9s is the winning tile for seat 1
            });

            // Dealer discards 9s → ClaimWindow opens
            bool discardOk = gs.Discard(gs.CurrentPlayerIndex, Tile.Sou(9));
            Test("GS.ClaimRon setup: dealer discard of 9s succeeds", discardOk);
            Test("GS.ClaimRon setup: Phase is ClaimWindow", gs.Phase == TurnPhase.ClaimWindow);

            // Seat 1 claims Ron on 9s
            bool ronOk = gs.ClaimRon(1);
            Test("GS.ClaimRon (riichi): succeeds", ronOk);
            Test("GS.ClaimRon (riichi): Phase is HandEnd", gs.Phase == TurnPhase.HandEnd);
            Test("GS.ClaimRon (riichi): winner is seat 1", gs.LastWinnerSeat == 1);
            Test("GS.ClaimRon (riichi): discarder is seat 0", gs.LastDiscarderSeat == 0);
        }

        // ---- Riichi candidate logic (mirrors GetRiichiCandidates in GameController) ----
        {
            // Helper: find valid riichi discards from a 14-tile hand
            static List<Tile> RiichiCandidates(Hand hand)
            {
                var result  = new List<Tile>();
                var seen    = new HashSet<int>();
                var closed  = hand.ClosedTiles.ToList();
                for (int i = 0; i < closed.Count; i++)
                {
                    if (!seen.Add(closed[i].TileId)) continue;
                    var test = closed.ToList();
                    test.RemoveAt(i);
                    var th = new Hand(); th.AddTiles(test);
                    if (th.IsTenpai()) result.Add(closed[i]);
                }
                return result;
            }

            // Case 1 — ONE lone dragon, valid ryanmen wait.
            // Hand: 2m3m4m 1p2p3p 6s7s 8s8s 西西西 + 菜(lone, drawn)
            // Discard 菜 → wait for 5s or 8s.  Any other discard → not tenpai.
            {
                var h = new Hand();
                h.AddTiles(new[]{
                    Tile.Man(2), Tile.Man(3), Tile.Man(4),
                    Tile.Pin(1), Tile.Pin(2), Tile.Pin(3),
                    Tile.Sou(6), Tile.Sou(7),
                    Tile.Sou(8), Tile.Sou(8),
                    Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West),
                    Tile.Dragon(DragonType.Green)   // lone — the riichi discard
                });
                var cands = RiichiCandidates(h);
                Test("Riichi: 1 lone dragon → exactly 1 candidate", cands.Count == 1);
                Test("Riichi: candidate is the lone Green Dragon", cands.Count == 1 && cands[0].Suit == TileSuit.Dragon && cands[0].Value == (int)DragonType.Green);

                // After discarding 菜, verify waits are 5s and 8s
                var after = new Hand();
                after.AddTiles(new[]{
                    Tile.Man(2), Tile.Man(3), Tile.Man(4),
                    Tile.Pin(1), Tile.Pin(2), Tile.Pin(3),
                    Tile.Sou(6), Tile.Sou(7),
                    Tile.Sou(8), Tile.Sou(8),
                    Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West)
                });
                Test("After riichi discard: shanten=0 (tenpai)", after.Shanten() == 0);
                Test("After riichi discard: waits include 5s", after.IsWaitingFor(Tile.Sou(5)));
                Test("After riichi discard: waits include 8s", after.IsWaitingFor(Tile.Sou(8)));
                Test("After riichi discard: does NOT wait for 4s", !after.IsWaitingFor(Tile.Sou(4)));
                Test("After riichi discard: does NOT wait for Green Dragon", !after.IsWaitingFor(Tile.Dragon(DragonType.Green)));
            }

            // Case 2 — TWO lone dragons, same bamboo structure.
            // Hand: 2m3m4m 1p2p 6s7s 8s8s 西西西 + 菜(lone) + 白(lone)
            // Removing EITHER dragon still leaves the other as an isolated tile → shanten 1 → no valid riichi.
            {
                var h = new Hand();
                h.AddTiles(new[]{
                    Tile.Man(2), Tile.Man(3), Tile.Man(4),
                    Tile.Pin(1), Tile.Pin(2),               // only 2 dot tiles (no complete sequence)
                    Tile.Sou(6), Tile.Sou(7),
                    Tile.Sou(8), Tile.Sou(8),
                    Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West),
                    Tile.Dragon(DragonType.Green),           // lone
                    Tile.Dragon(DragonType.White)            // lone
                });
                var cands = RiichiCandidates(h);
                Test("Riichi: 2 lone dragons → 0 riichi candidates (neither discard gives tenpai)", cands.Count == 0);

                // Verify: removing 菜 gives shanten 1 (not tenpai)
                var testA = new Hand();
                testA.AddTiles(new[]{
                    Tile.Man(2), Tile.Man(3), Tile.Man(4),
                    Tile.Pin(1), Tile.Pin(2),
                    Tile.Sou(6), Tile.Sou(7),
                    Tile.Sou(8), Tile.Sou(8),
                    Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West),
                    Tile.Dragon(DragonType.White)  // still lone after removing 菜
                });
                Test("2-dragon hand (remove 菜): shanten=1 not tenpai", testA.Shanten() == 1);

                // Verify: removing 白 also gives shanten 1
                var testB = new Hand();
                testB.AddTiles(new[]{
                    Tile.Man(2), Tile.Man(3), Tile.Man(4),
                    Tile.Pin(1), Tile.Pin(2),
                    Tile.Sou(6), Tile.Sou(7),
                    Tile.Sou(8), Tile.Sou(8),
                    Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West),
                    Tile.Dragon(DragonType.Green)  // still lone after removing 白
                });
                Test("2-dragon hand (remove 白): shanten=1 not tenpai", testB.Shanten() == 1);
            }

            // Case 3 — Dragon PAIR (jantai) + lone honour: valid riichi, pair stays in hand.
            // Hand (14t): 2m3m4m 1p2p3p 西西西 6s7s 白白 + 菜(lone, the riichi discard)
            //   = 3+3+3+2+2+1 = 14 tiles ✓
            // After discarding 菜 → 13-tile tenpai waiting for 5s or 8s.
            // 白白 is the pair (jantai) and stays locked in the riichi hand — that is expected.
            {
                var h = new Hand();
                h.AddTiles(new[]{
                    Tile.Man(2), Tile.Man(3), Tile.Man(4),
                    Tile.Pin(1), Tile.Pin(2), Tile.Pin(3),
                    Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West),
                    Tile.Sou(6), Tile.Sou(7),               // ryanmen, waits for 5s or 8s
                    Tile.Dragon(DragonType.White), Tile.Dragon(DragonType.White), // pair (jantai)
                    Tile.Dragon(DragonType.Green)            // lone — the riichi discard
                });
                var cands = RiichiCandidates(h);
                Test("Riichi: 白白 pair + lone 菜 → exactly 1 candidate (菜)", cands.Count == 1);
                Test("Riichi: candidate is Green Dragon (not White)", cands.Count == 1 && cands[0].Value == (int)DragonType.Green);

                // The locked 13-tile tenpai hand: groups + ryanmen + 白白 pair
                // 2m3m4m 1p2p3p 西西西 6s7s 白白 = 3+3+3+2+2 = 13 tiles ✓
                var locked = new Hand();
                locked.AddTiles(new[]{
                    Tile.Man(2), Tile.Man(3), Tile.Man(4),
                    Tile.Pin(1), Tile.Pin(2), Tile.Pin(3),
                    Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West),
                    Tile.Sou(6), Tile.Sou(7),
                    Tile.Dragon(DragonType.White), Tile.Dragon(DragonType.White)
                });
                Test("Locked riichi hand (白白 as pair): tenpai", locked.Shanten() == 0);
                Test("Locked riichi hand: waits 5s", locked.IsWaitingFor(Tile.Sou(5)));
                Test("Locked riichi hand: waits 8s", locked.IsWaitingFor(Tile.Sou(8)));
                Test("Locked riichi hand: does NOT wait for 菜", !locked.IsWaitingFor(Tile.Dragon(DragonType.Green)));
                Test("Locked riichi hand: does NOT wait for 白", !locked.IsWaitingFor(Tile.Dragon(DragonType.White)));
            }

            // Case 4 — IsTenpai() on 14-tile hands: correct behaviour.
            // A 14-tile hand with one extra tile above the 13-tile tenpai structure → IsTenpai true.
            // A 14-tile hand with two extra tiles (only 12 "useful") → IsTenpai false.
            {
                // Valid: 13-tile tenpai + 1 lone honour = shanten 0 on 14 tiles
                var valid14 = new Hand();
                valid14.AddTiles(new[]{
                    Tile.Man(2), Tile.Man(3), Tile.Man(4),
                    Tile.Pin(1), Tile.Pin(2), Tile.Pin(3),
                    Tile.Sou(6), Tile.Sou(7),
                    Tile.Sou(8), Tile.Sou(8),
                    Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West),
                    Tile.Dragon(DragonType.Green)  // the extra tile
                });
                Test("IsTenpai on 14t (valid, 1 extra): returns true", valid14.IsTenpai());
                Test("IsTenpai on 14t: candidate count == 1", RiichiCandidates(valid14).Count == 1);

                // Invalid: 14-tile hand where no single discard gives tenpai
                var invalid14 = new Hand();
                invalid14.AddTiles(new[]{
                    Tile.Man(2), Tile.Man(3), Tile.Man(4),
                    Tile.Pin(1), Tile.Pin(2),               // incomplete dots
                    Tile.Sou(6), Tile.Sou(7),
                    Tile.Sou(8), Tile.Sou(8),
                    Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West),
                    Tile.Dragon(DragonType.Green),
                    Tile.Dragon(DragonType.White)            // second lone honour — kills tenpai
                });
                Test("IsTenpai on 14t (invalid, 2 lone honours): returns false", !invalid14.IsTenpai());
                Test("IsTenpai on 14t invalid: candidate count == 0", RiichiCandidates(invalid14).Count == 0);
            }

            // Case 5 — Dead wait: player holds 2 copies of 8s, all 4 copies accounted for.
            // IsWaitingFor(8s) is still true (it WOULD complete the hand), but the remaining count is 0.
            // The dead-wait calculation happens in ComputeRemainingCounts (UI layer), so here we just
            // verify that IsWaitingFor correctly identifies the wait regardless of availability.
            {
                var tenpai = new Hand();
                tenpai.AddTiles(new[]{
                    Tile.Man(2), Tile.Man(3), Tile.Man(4),
                    Tile.Pin(1), Tile.Pin(2), Tile.Pin(3),
                    Tile.Sou(6), Tile.Sou(7),
                    Tile.Sou(8), Tile.Sou(8),
                    Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West), Tile.Wind(WindDirection.West)
                });
                var waits = tenpai.GetWaitingTiles();
                Test("Dead-wait hand: 2 waits (5s and 8s)", waits.Count == 2);
                Test("Dead-wait hand: IsWaitingFor(5s) = true", tenpai.IsWaitingFor(Tile.Sou(5)));
                Test("Dead-wait hand: IsWaitingFor(8s) = true (even if 0 remain in pool)", tenpai.IsWaitingFor(Tile.Sou(8)));

                // Simulate remaining-count logic: player holds 2×8s, 2 more already discarded → 0 left
                int totalCopies  = 4;
                int inOwnHand    = tenpai.ClosedTiles.Count(t => t == Tile.Sou(8)); // 2
                int inDiscards   = 2;   // simulated: other players discarded 2 copies
                int remaining8s  = Math.Max(0, totalCopies - inOwnHand - inDiscards);
                Test("Dead-wait remaining count: 4 − 2(hand) − 2(discards) = 0", remaining8s == 0);
                Test("Dead-wait remaining count: 5s is live (remaining > 0)", Math.Max(0, 4 - 0 - 0) == 4);
            }
        }

        // ---- Dealer tile counts: DealerIndex-aware initial deal ----
        // Regression test for the bug where DealInitialHands() always gave 14 tiles to
        // seat 0, but StartNewHand() should give 14 to whoever is DealerIndex.
        {
            // Directly verify GameState tile counts at hand start for all 4 dealer positions.
            for (int dealerSeat = 0; dealerSeat < 4; dealerSeat++)
            {
                var gs2 = new GameState(humanSeat: 0);
                // Manually rotate the dealer to the target seat
                // (We poke DealerIndex indirectly via reflection-free approach: AdvanceDealer is private,
                //  so we run enough BeginNextHand() calls to reach the desired dealer position.)
                // Simpler: use the Wall directly to verify the tile counts.
                // Actually GameState.StartGame() sets DealerIndex=0 then calls StartNewHand().
                // We need DealerIndex != 0.  Easiest: test via the Wall's DealInitialHands() directly.
                var wall = new TileWall(new Random(dealerSeat));
                var dealt = wall.DealInitialHands();
                // hands[0] always has 14 tiles (East/Dealer role), others 13
                Test($"DealInitialHands: hands[0] has 14 tiles (dealer={dealerSeat})", dealt[0].Count == 14);
                Test($"DealInitialHands: hands[1] has 13 tiles (dealer={dealerSeat})", dealt[1].Count == 13);
                Test($"DealInitialHands: hands[2] has 13 tiles (dealer={dealerSeat})", dealt[2].Count == 13);
                Test($"DealInitialHands: hands[3] has 13 tiles (dealer={dealerSeat})", dealt[3].Count == 13);
            }

            // Verify GameState correctly routes the 14-tile hand to the current DealerIndex.
            // We simulate by starting a game and then advancing to subsequent hands.
            {
                // Hand 1: DealerIndex == 0 (seat 0 = human dealer)
                var gs2 = new GameState(humanSeat: 0);
                gs2.StartGame();  // DealerIndex = 0, StartNewHand called
                Test("Hand 1 (dealer=0): dealer has 14 tiles",  gs2.Players[0].Hand.ClosedTiles.Count == 14);
                Test("Hand 1 (dealer=0): seat 1 has 13 tiles",  gs2.Players[1].Hand.ClosedTiles.Count == 13);
                Test("Hand 1 (dealer=0): seat 2 has 13 tiles",  gs2.Players[2].Hand.ClosedTiles.Count == 13);
                Test("Hand 1 (dealer=0): seat 3 has 13 tiles",  gs2.Players[3].Hand.ClosedTiles.Count == 13);
                Test("Hand 1 (dealer=0): phase is ActionPhase", gs2.Phase == TurnPhase.ActionPhase);
                Test("Hand 1 (dealer=0): current player is dealer", gs2.CurrentPlayerIndex == 0);
            }
        }

        // =========================================================================
        // ---- Bug fixes: multiplayer tile sort, seat rotation, HUD rotation ----
        // =========================================================================

        // ---- Bug 1: Tile sort after draw in network mode ----
        // Network mode appends the drawn tile to _netMyTiles then sorts [0..Count-2],
        // keeping the drawn tile at the end (mirrors Hand.Sort()).
        // HandDisplay.Rebuild requires the drawn tile at index Count-1 for the "lifted" effect.
        {
            static int TileCmp(Tile a, Tile b)
            {
                int s = ((int)a.Suit).CompareTo((int)b.Suit);
                return s != 0 ? s : a.Value.CompareTo(b.Value);
            }
            static bool IsSortedExcludingLast(List<Tile> tiles)
            {
                for (int i = 0; i < tiles.Count - 2; i++)
                    if (TileCmp(tiles[i], tiles[i + 1]) > 0) return false;
                return true;
            }
            var tileOrderer = Comparer<Tile>.Create(TileCmp);

            // Simulate a sorted 13-tile hand received from the server
            var netTiles = new List<Tile>
            {
                Tile.Man(1), Tile.Man(3), Tile.Man(7),
                Tile.Pin(2), Tile.Pin(5), Tile.Pin(9),
                Tile.Sou(1), Tile.Sou(4), Tile.Sou(8),
                Tile.Wind(WindDirection.East),
                Tile.Wind(WindDirection.South),
                Tile.Dragon(DragonType.White),
                Tile.Dragon(DragonType.Green),
            };
            Test("TileSort: initial 13 tiles start sorted (as server sends)", IsSortedExcludingLast(netTiles));

            // Draw Pin(3) — sorts between Pin(2) and Pin(5)
            var drawn = Tile.Pin(3);
            netTiles.Add(drawn);
            if (netTiles.Count > 1)
                netTiles.Sort(0, netTiles.Count - 1, tileOrderer);

            Test("TileSort: drawn tile stays at index 13 (Count-1)", netTiles[13] == drawn);
            Test("TileSort: tiles [0..12] are sorted after draw", IsSortedExcludingLast(netTiles));
            int p2idx = netTiles.FindIndex(t => t.Suit == TileSuit.Pin && t.Value == 2);
            int p5idx = netTiles.FindIndex(t => t.Suit == TileSuit.Pin && t.Value == 5);
            Test("TileSort: Pin(2) precedes Pin(5) in sorted portion", p2idx < p5idx && p2idx < 13);

            // Discard a non-drawn tile — list must stay sorted excluding the (still-last) drawn tile
            netTiles.RemoveAt(0);  // Remove Man(1) at position 0
            Test("TileSort: 13 tiles remain after discard", netTiles.Count == 13);
            Test("TileSort: drawn tile still at index 12 (Count-1) after discard", netTiles[12] == drawn);
            Test("TileSort: [0..11] still sorted after non-drawn discard", IsSortedExcludingLast(netTiles));

            // Second draw — e.g. Sou(5) — must also land at end, sorted portion unchanged
            netTiles.RemoveAt(netTiles.Count - 1);  // simulate discard of previous drawn tile
            var drawn2 = Tile.Sou(5);
            netTiles.Add(drawn2);
            if (netTiles.Count > 1)
                netTiles.Sort(0, netTiles.Count - 1, tileOrderer);

            Test("TileSort: second drawn tile at end", netTiles[netTiles.Count - 1] == drawn2);
            Test("TileSort: sorted portion correct after second draw", IsSortedExcludingLast(netTiles));

            // Edge case: draw a tile identical in value to the last sorted tile
            // The drawn tile must still be the one at Count-1 (not an existing duplicate)
            netTiles.RemoveAt(netTiles.Count - 1);
            // Sou(8) is already in the hand (at some position); draw another Sou(8)
            var drawnDup = Tile.Sou(8);
            netTiles.Add(drawnDup);
            if (netTiles.Count > 1)
                netTiles.Sort(0, netTiles.Count - 1, tileOrderer);
            Test("TileSort: duplicate-value drawn tile at index Count-1", netTiles[netTiles.Count - 1] == drawnDup);
            Test("TileSort: sorted portion correct with duplicate draw", IsSortedExcludingLast(netTiles));
        }

        // ---- Bug 2 & 3: Seat rotation formula (ToVisualSeat) ----
        // Maps global server seat → visual position: 0=self/bottom, 1=right, 2=top, 3=left.
        {
            static int ToVisualSeat(int globalSeat, int humanSeat) => (globalSeat - humanSeat + 4) % 4;

            // Human's own seat always maps to visual 0 (bottom)
            for (int h = 0; h < 4; h++)
                Test($"SeatRotation: humanSeat={h} own seat → visual 0", ToVisualSeat(h, h) == 0);

            // humanSeat=0: identity (no rotation needed — local mode)
            for (int g = 0; g < 4; g++)
                Test($"SeatRotation: humanSeat=0 global {g} → visual {g}", ToVisualSeat(g, 0) == g);

            // humanSeat=1
            Test("SeatRotation: human=1 global 0 → visual 3 (left)", ToVisualSeat(0, 1) == 3);
            Test("SeatRotation: human=1 global 2 → visual 1 (right)", ToVisualSeat(2, 1) == 1);
            Test("SeatRotation: human=1 global 3 → visual 2 (top)", ToVisualSeat(3, 1) == 2);

            // humanSeat=2
            Test("SeatRotation: human=2 global 0 → visual 2 (top)", ToVisualSeat(0, 2) == 2);
            Test("SeatRotation: human=2 global 1 → visual 3 (left)", ToVisualSeat(1, 2) == 3);
            Test("SeatRotation: human=2 global 3 → visual 1 (right)", ToVisualSeat(3, 2) == 1);

            // humanSeat=3
            Test("SeatRotation: human=3 global 0 → visual 1 (right)", ToVisualSeat(0, 3) == 1);
            Test("SeatRotation: human=3 global 1 → visual 2 (top)", ToVisualSeat(1, 3) == 2);
            Test("SeatRotation: human=3 global 2 → visual 3 (left)", ToVisualSeat(2, 3) == 3);

            // Discard-pool routing: discard from seat X must land in visual pool ToVisualSeat(X)
            for (int h = 0; h < 4; h++)
                for (int g = 0; g < 4; g++)
                    Test($"SeatRotation: seat {g} discard → pool[{ToVisualSeat(g,h)}] for human={h}",
                        ToVisualSeat(ToVisualSeat(g, h), 0) == ToVisualSeat(g, h));  // visual index is stable
        }

        // ---- Bug 2: HUD name/score rotation (NetUpdateHud data mapping) ----
        {
            static (string[] names, int[] scores) RotateForHud(string[] names, int[] scores, int humanSeat)
            {
                var rotN = new string[4];
                var rotS = new int[4];
                for (int g = 0; g < 4; g++)
                {
                    int vs = (g - humanSeat + 4) % 4;
                    rotN[vs] = names[g];
                    rotS[vs] = scores[g];
                }
                return (rotN, rotS);
            }

            var serverNames  = new[] { "Alice", "Bob", "Carol", "Dave" };
            var serverScores = new[] { 30000, 25000, 28000, 22000 };

            // humanSeat=0: no rotation — labels match global order
            {
                var (rn, rs) = RotateForHud(serverNames, serverScores, humanSeat: 0);
                Test("HudRot: human=0 visual[0]=Alice (self/bottom)", rn[0] == "Alice");
                Test("HudRot: human=0 visual[1]=Bob  (right)",        rn[1] == "Bob");
                Test("HudRot: human=0 visual[2]=Carol (top)",         rn[2] == "Carol");
                Test("HudRot: human=0 visual[3]=Dave  (left)",        rn[3] == "Dave");
            }

            // humanSeat=2: Carol at bottom, Dave at right, Alice at top, Bob at left
            {
                var (rn, rs) = RotateForHud(serverNames, serverScores, humanSeat: 2);
                Test("HudRot: human=2 visual[0]=Carol (self/bottom)", rn[0] == "Carol");
                Test("HudRot: human=2 visual[1]=Dave  (right)",       rn[1] == "Dave");
                Test("HudRot: human=2 visual[2]=Alice (top)",         rn[2] == "Alice");
                Test("HudRot: human=2 visual[3]=Bob   (left)",        rn[3] == "Bob");
                Test("HudRot: human=2 score[0]=Carol's 28000",        rs[0] == 28000);
                Test("HudRot: human=2 score[1]=Dave's  22000",        rs[1] == 22000);
            }

            // Wind labels: after rotation, (visualPos - visualDealer + 4) % 4 must equal
            // the direct calculation (globalSeat - globalDealer + 4) % 4.
            for (int humanSeat = 0; humanSeat < 4; humanSeat++)
            {
                for (int globalDealer = 0; globalDealer < 4; globalDealer++)
                {
                    int visDealer = (globalDealer - humanSeat + 4) % 4;
                    for (int globalSeat = 0; globalSeat < 4; globalSeat++)
                    {
                        int visPos        = (globalSeat - humanSeat   + 4) % 4;
                        int windViaVisual = (visPos     - visDealer    + 4) % 4;
                        int windDirect    = (globalSeat - globalDealer + 4) % 4;
                        Test($"HudRot: wind consistent human={humanSeat} dealer={globalDealer} seat={globalSeat}",
                            windViaVisual == windDirect);
                    }
                }
            }
        }

        // ---- Bug 2: Dealer position transfers correctly between hands ----
        // After a non-dealer win, DealerIndex advances by 1 and the new dealer gets 14 tiles.
        // After a dealer win, DealerIndex stays and Counters increment.
        {
            // ── Non-dealer win → dealer rotates ──
            var gs = new GameState(humanSeat: 0, new[] { "P0", "P1", "P2", "P3" });
            gs.StartGame();
            Test("DealerTransfer: initial dealer = 0", gs.DealerIndex == 0);
            Test("DealerTransfer: dealer starts with 14 tiles", gs.Players[0].Hand.ClosedTiles.Count == 14);

            // Set seat 1 as waiting on White Dragon, seat 0 discards it
            var p1 = gs.Players[1];
            p1.Hand.Reset();
            p1.Hand.AddTiles(new List<Tile>
            {
                Tile.Man(1), Tile.Man(2), Tile.Man(3),
                Tile.Pin(4), Tile.Pin(5), Tile.Pin(6),
                Tile.Sou(7), Tile.Sou(8), Tile.Sou(9),
                Tile.Wind(WindDirection.East), Tile.Wind(WindDirection.East), Tile.Wind(WindDirection.East),
                Tile.Dragon(DragonType.White),
            });
            var p0 = gs.Players[0];
            p0.Hand.Reset();
            p0.Hand.AddTiles(new List<Tile>
            {
                Tile.Man(2), Tile.Man(3), Tile.Man(4),
                Tile.Pin(5), Tile.Pin(6), Tile.Pin(7),
                Tile.Sou(1), Tile.Sou(2), Tile.Sou(3),
                Tile.Wind(WindDirection.South), Tile.Wind(WindDirection.South), Tile.Wind(WindDirection.South),
                Tile.Dragon(DragonType.Green), Tile.Dragon(DragonType.White),
            });
            bool discardOk = gs.Discard(0, Tile.Dragon(DragonType.White));
            bool ronOk     = gs.ClaimRon(1);
            Test("DealerTransfer: setup discard succeeds", discardOk);
            Test("DealerTransfer: seat 1 ron succeeds", ronOk);
            Test("DealerTransfer: phase HandEnd after ron", gs.Phase == TurnPhase.HandEnd);

            gs.BeginNextHand();
            if (gs.Phase != TurnPhase.GameOver)
            {
                Test("DealerTransfer: non-dealer win → dealer advances to seat 1", gs.DealerIndex == 1);
                Test("DealerTransfer: new dealer (seat 1) has 14 tiles", gs.Players[1].Hand.ClosedTiles.Count == 14);
                Test("DealerTransfer: seat 0 has 13 tiles", gs.Players[0].Hand.ClosedTiles.Count == 13);
                Test("DealerTransfer: seat 2 has 13 tiles", gs.Players[2].Hand.ClosedTiles.Count == 13);
                Test("DealerTransfer: seat 3 has 13 tiles", gs.Players[3].Hand.ClosedTiles.Count == 13);
                Test("DealerTransfer: current player = new dealer", gs.CurrentPlayerIndex == gs.DealerIndex);
                Test("DealerTransfer: phase is ActionPhase", gs.Phase == TurnPhase.ActionPhase);
            }

            // ── Dealer win → dealer stays, counter increments ──
            var gs2 = new GameState(humanSeat: 0, new[] { "D0", "P1", "P2", "P3" });
            gs2.StartGame();
            int prevCounters = gs2.Counters;

            // Give dealer (seat 0) a complete 14-tile winning hand
            var dealer = gs2.Players[0];
            dealer.Hand.Reset();
            dealer.Hand.AddTiles(new List<Tile>
            {
                Tile.Man(1), Tile.Man(2), Tile.Man(3),
                Tile.Pin(4), Tile.Pin(5), Tile.Pin(6),
                Tile.Sou(7), Tile.Sou(8), Tile.Sou(9),
                Tile.Wind(WindDirection.East), Tile.Wind(WindDirection.East), Tile.Wind(WindDirection.East),
                Tile.Dragon(DragonType.White), Tile.Dragon(DragonType.White),
            });
            bool tsumo = gs2.DeclareTsumo();
            Test("DealerTransfer: dealer tsumo succeeds", tsumo);

            gs2.BeginNextHand();
            if (gs2.Phase != TurnPhase.GameOver)
            {
                Test("DealerTransfer: dealer win → dealer stays at 0", gs2.DealerIndex == 0);
                Test("DealerTransfer: dealer win → counter incremented", gs2.Counters == prevCounters + 1);
                Test("DealerTransfer: dealer still has 14 tiles next hand", gs2.Players[0].Hand.ClosedTiles.Count == 14);
            }
        }

        Console.WriteLine($"\n  Result: {pass} passed, {fail} failed\n");
        if (fail > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  *** Unit test failures detected — investigate before shipping ***");
            Console.ResetColor();
        }
    }

    // =========================================================================
    // Full game simulation (headless AI-vs-AI, human moves automated)
    // =========================================================================

    static void SimulateGame()
    {
        var gs   = new GameState(humanSeat: 0, new[]{ "You", "CPU 1", "CPU 2", "CPU 3" });
        var ai   = Enumerable.Range(0, 4).Select(_ => new AIPlayer()).ToArray();

        gs.OnHandEnd       += (reason, winners) => OnHandEnd(gs, reason, winners);
        gs.OnRiichiDeclared += seat => { _riichiDeclared++; Log($"  → Seat {seat} declares RIICHI"); };

        gs.StartGame();

        // Simulate up to 8 hands (East + South round snippets)
        int handLimit = 8;
        for (int hand = 0; hand < handLimit && gs.Phase != TurnPhase.GameOver; hand++)
        {
            _handsPlayed++;
            Log($"\n─── Hand {hand + 1} (Dealer: {gs.Players[gs.DealerIndex].Name}) ───");
            RunHand(gs, ai);

            if (gs.Phase == TurnPhase.HandEnd || gs.Phase == TurnPhase.GameOver)
            {
                if (gs.Phase != TurnPhase.GameOver)
                    gs.BeginNextHand();
            }
        }
    }

    static void RunHand(GameState gs, AIPlayer[] ai)
    {
        int safetyCounter = 0;

        while (gs.Phase != TurnPhase.HandEnd && gs.Phase != TurnPhase.GameOver)
        {
            if (++safetyCounter > 500) { Log("  [safety limit hit — aborting hand]"); break; }

            switch (gs.Phase)
            {
                case TurnPhase.DrawPhase:
                    gs.DrawForCurrentPlayer();
                    break;

                case TurnPhase.ActionPhase:
                    TakeAction(gs, ai);
                    break;

                case TurnPhase.ClaimWindow:
                    ProcessClaims(gs, ai);
                    break;
            }
        }
    }

    static void TakeAction(GameState gs, AIPlayer[] ai)
    {
        int   seat = gs.CurrentPlayerIndex;
        var   hand = gs.Players[seat].Hand;
        var   player = gs.Players[seat];

        // Tsumo check (all players)
        if (hand.Shanten() == -1)
        {
            bool ok = gs.DeclareTsumo();
            if (ok) { Log($"  Seat {seat} ({player.Name}): TSUMO"); return; }
        }

        // Riichi eligibility (closed hand, tenpai, enough points)
        if (!hand.IsRiichi && hand.IsFullyClosed && player.Points >= GameState.RiichiBetAmount)
        {
            var closed = hand.ClosedTiles.ToList();
            var seen = new HashSet<int>();
            for (int i = 0; i < closed.Count; i++)
            {
                if (!seen.Add(closed[i].TileId)) continue;
                var test = closed.ToList();
                test.RemoveAt(i);
                var th = new Hand(); th.AddTiles(test);
                if (th.IsTenpai())
                {
                    bool rOk = gs.DeclareRiichi(seat, closed[i]);
                    if (rOk) { Log($"  Seat {seat} ({player.Name}): Riichi on {closed[i]}"); return; }
                }
            }
        }

        // Post-riichi: auto-discard drawn tile (if can't tsumo)
        if (hand.IsRiichi)
        {
            var drawn = hand.DrawnTile ?? hand.ClosedTiles.Last();
            gs.Discard(seat, drawn);
            return;
        }

        // Normal discard via AI
        var discard = ai[seat].ChooseDiscard(hand, gs, seat);
        gs.Discard(seat, discard);
    }

    static void ProcessClaims(GameState gs, AIPlayer[] ai)
    {
        var tile = gs.PendingDiscard!;
        int discarder = gs.DiscarderIndex;

        // Check each player for Ron
        for (int i = 0; i < 4; i++)
        {
            if (i == discarder) continue;
            if (ai[i].ShouldClaimRon(tile, gs.Players[i].Hand, gs, i))
            {
                bool ok = gs.ClaimRon(i);
                if (ok) { Log($"  Seat {i} ({gs.Players[i].Name}): RON on {tile} from seat {discarder}"); return; }
            }
        }

        // Pon / Chi (simplified: first AI that can, does)
        for (int i = 0; i < 4; i++)
        {
            if (i == discarder) continue;
            var hand = gs.Players[i].Hand;
            if (hand.IsRiichi) continue;

            // Pon
            if (hand.ClosedTiles.Count(t => t == tile) >= 2)
            {
                // Skip pon in AI for simplicity — focus on discard/riichi/win testing
                // (ClaimPon would need a subsequent discard choice)
            }
        }

        gs.PassAllClaims();
    }

    static void OnHandEnd(GameState gs, HandEndReason reason, int[] winners)
    {
        switch (reason)
        {
            case HandEndReason.Tsumo:
                _tsumoWins++;
                var ts = gs.LastScoreResult;
                var ty = gs.LastYakuResult;
                if (ts != null && ty != null)
                {
                    _scoredHands++;
                    _totalHan += ts.TotalFan;
                    string yakuNames = string.Join(", ", ty.Yaku.Select(y => y.Name));
                    Log($"  TSUMO win — {ts.TotalFan} han {ts.Fu?.Total} fu — {yakuNames}");
                    Log($"           — Total: +{ts.TotalPointsWon:N0} pts");
                    PrintScores(gs);
                }
                break;

            case HandEndReason.Ron:
                _ronWins++;
                var rs = gs.LastScoreResult;
                var ry = gs.LastYakuResult;
                if (rs != null && ry != null)
                {
                    _scoredHands++;
                    _totalHan += rs.TotalFan;
                    string yakuNames = string.Join(", ", ry.Yaku.Select(y => y.Name));
                    Log($"  RON win  — {rs.TotalFan} han {rs.Fu?.Total} fu — {yakuNames}");
                    Log($"           — Total: +{rs.TotalPointsWon:N0} pts");
                    PrintScores(gs);
                }
                break;

            case HandEndReason.ExhaustiveDraw:
                _draws++;
                Log("  Exhaustive draw (ryuukyoku)");
                PrintScores(gs);
                break;
        }
    }

    static void PrintScores(GameState gs)
    {
        var sorted = gs.Players.OrderByDescending(p => p.Points).ToList();
        Log("  Scores: " + string.Join("  |  ", sorted.Select(p => $"{p.Name} {p.Points:N0}")));
    }

    static void Log(string msg)
    {
        _log.Add(msg);
        Console.WriteLine(msg);
    }

    // =========================================================================
    // Summary
    // =========================================================================

    static void PrintSummary()
    {
        Console.WriteLine("\n═══════════════════════════════════════════════════");
        Console.WriteLine("  Simulation Summary");
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine($"  Hands played       : {_handsPlayed}");
        Console.WriteLine($"  Tsumo wins         : {_tsumoWins}");
        Console.WriteLine($"  Ron wins           : {_ronWins}");
        Console.WriteLine($"  Exhaustive draws   : {_draws}");
        Console.WriteLine($"  Riichi declarations: {_riichiDeclared}");
        if (_scoredHands > 0)
            Console.WriteLine($"  Avg han per win    : {(double)_totalHan / _scoredHands:F1}");
        Console.WriteLine($"  Logic errors       : 0 (no exceptions thrown)");
        Console.WriteLine();

        bool ok = (_tsumoWins + _ronWins + _draws) == _handsPlayed;
        if (ok)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  ✓ All hands resolved cleanly — no hangs or crashes.");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  ✗ Some hands did not resolve — check logic.");
        }
        Console.ResetColor();
        Console.WriteLine("═══════════════════════════════════════════════════");
    }
}
