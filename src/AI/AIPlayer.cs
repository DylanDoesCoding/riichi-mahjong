// =============================================================================
// AIPlayer.cs
// Decision-making logic for a CPU-controlled player.
//
// The AI operates in two phases each turn:
//   1. CLAIM DECISION  — should I pon/chi/kan/ron this discard?
//   2. DISCARD DECISION — which tile should I discard from my 14-tile hand?
//
// AI Difficulty levels:
//   Easy   — mostly random, will call and riichi occasionally
//   Medium — shanten-based: always discards to minimize shanten; claims wisely
//   Hard   — value-aware: weighs hand speed vs expected value; defends vs riichi players
//
// The AI has no knowledge of other players' closed tiles (it only sees what
// a real player could see: open melds, discards, and dora indicators).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RiichiMahjong.Core;

namespace RiichiMahjong.AI
{
    public enum AIDifficulty { Easy, Medium, Hard }

    public class AIPlayer
    {
        private readonly AIDifficulty _difficulty;
        private readonly Random       _rng;

        public AIPlayer(AIDifficulty difficulty = AIDifficulty.Medium, Random? rng = null)
        {
            _difficulty = difficulty;
            _rng        = rng ?? new Random();
        }

        // =====================================================================
        // DISCARD DECISION
        // =====================================================================

        /// <summary>
        /// Choose which tile to discard from the current hand (14 tiles).
        /// Returns the tile the AI wants to discard.
        /// </summary>
        public Tile ChooseDiscard(Hand hand, GameState gameState, int playerIndex)
        {
            var tiles = hand.ClosedTiles.ToList();
            if (tiles.Count == 0) throw new InvalidOperationException("Hand is empty.");

            // Riichi players cannot choose — they have no tiles left to reorganise
            // (This shouldn't be called for riichi players after declaration,
            //  but as a safeguard return the drawn tile)
            if (hand.IsRiichi)
                return hand.DrawnTile ?? tiles.Last();

            return _difficulty switch
            {
                AIDifficulty.Easy   => EasyDiscard(tiles),
                AIDifficulty.Medium => ShantenBasedDiscard(hand, gameState, playerIndex),
                AIDifficulty.Hard   => ShantenBasedDiscard(hand, gameState, playerIndex), // Will diverge later
                _ => EasyDiscard(tiles),
            };
        }

        /// <summary>Easy: pick a random tile, with slight bias against terminals/honours.</summary>
        private Tile EasyDiscard(List<Tile> tiles)
        {
            // 70% chance to try discarding a terminal/honour first (rough strategy)
            if (_rng.NextDouble() < 0.7)
            {
                var isolated = tiles
                    .Where(t => t.IsTerminalOrHonour)
                    .ToList();
                if (isolated.Count > 0)
                    return isolated[_rng.Next(isolated.Count)];
            }
            return tiles[_rng.Next(tiles.Count)];
        }

        /// <summary>
        /// Medium/Hard: discard the tile that minimises shanten (or keeps shanten equal
        /// while maximising future tile efficiency).
        /// </summary>
        private Tile ShantenBasedDiscard(Hand hand, GameState gameState, int playerIndex)
        {
            var tiles   = hand.ClosedTiles.ToList();
            int bestShanten = int.MaxValue;
            var candidates = new List<Tile>();

            foreach (var tile in tiles)
            {
                // Try removing this tile and compute shanten
                var testList = new List<Tile>(tiles);
                testList.Remove(tile);

                int shanten = CalcShantenFromList(testList, hand.OpenMelds.Count);
                if (shanten < bestShanten)
                {
                    bestShanten = shanten;
                    candidates.Clear();
                    candidates.Add(tile);
                }
                else if (shanten == bestShanten)
                {
                    candidates.Add(tile);
                }
            }

            // Among ties, prefer to discard: honours > terminals > simples
            // (Isolated honours have lower value than live simples)
            candidates = PrioritiseCandidates(candidates, tiles);
            return candidates[_rng.Next(candidates.Count)];
        }

        /// <summary>
        /// When multiple tiles give the same shanten, prefer discarding in this order:
        ///   1. Isolated honours (no pair/triplet use)
        ///   2. Isolated terminals
        ///   3. Other tiles (simples)
        /// </summary>
        private List<Tile> PrioritiseCandidates(List<Tile> candidates, List<Tile> allTiles)
        {
            // Isolated = appears only once in hand AND has no adjacent tiles (for number suits)
            var honours = candidates.Where(t => t.IsHonour).ToList();
            if (honours.Count > 0) return honours;

            var terminals = candidates.Where(t => t.IsTerminal).ToList();
            if (terminals.Count > 0) return terminals;

            return candidates;
        }

        // =====================================================================
        // CLAIM DECISION
        // =====================================================================

        /// <summary>
        /// Should the AI claim this discard for RON?
        /// </summary>
        public bool ShouldClaimRon(Tile discard, Hand hand, GameState gameState, int playerIndex)
        {
            // Fast path: 2 Shanten calls (IsTenpai + IsWaitingFor) instead of GetWaitingTiles (35 calls).
            if (gameState.Players[playerIndex].Furiten.IsFuriten) return false;
            return hand.IsTenpai() && hand.IsWaitingFor(discard);
        }

        /// <summary>
        /// Should the AI claim this discard for PON?
        /// Returns true/false based on difficulty and hand state.
        /// </summary>
        public bool ShouldClaimPon(Tile discard, Hand hand, GameState gameState, int playerIndex)
        {
            if (hand.IsRiichi) return false;

            int copies = hand.ClosedTiles.Count(t => t == discard);
            if (copies < 2) return false;

            return _difficulty switch
            {
                AIDifficulty.Easy   => _rng.NextDouble() < 0.5,
                AIDifficulty.Medium => ShantenImprovedByPon(discard, hand),
                AIDifficulty.Hard   => ShantenImprovedByPon(discard, hand) && !IsDefensiveDiscard(discard, gameState, playerIndex),
                _ => false,
            };
        }

        /// <summary>
        /// Should the AI claim this discard for CHI?
        /// (Only valid from left player.)
        /// </summary>
        public bool ShouldClaimChi(Tile discard, Tile t1, Tile t2, Hand hand, GameState gameState, int playerIndex)
        {
            if (hand.IsRiichi) return false;
            if (!IsValidChi(t1, t2, discard)) return false;

            return _difficulty switch
            {
                AIDifficulty.Easy   => _rng.NextDouble() < 0.3,
                AIDifficulty.Medium => ShantenImprovedByChi(discard, t1, t2, hand),
                AIDifficulty.Hard   => ShantenImprovedByChi(discard, t1, t2, hand),
                _ => false,
            };
        }

        /// <summary>
        /// Returns the tile to use for ankan (all 4 in closed hand), or null if none available.
        /// </summary>
        public Tile? GetAnkanTile(Hand hand, GameState gameState, int playerIndex)
        {
            if (hand.IsRiichi) return null;
            if (gameState.Wall.KanCount >= 4) return null;

            foreach (var t in Hand.AllTileTypes())
                if (hand.ClosedTiles.Count(c => c == t) >= 4)
                    return t;
            return null;
        }

        /// <summary>
        /// Returns the tile to use for kakan (extend existing open Pon), or null if none.
        /// </summary>
        public Tile? GetKakanTile(Hand hand, GameState gameState, int playerIndex)
        {
            if (hand.IsRiichi) return null;
            if (gameState.Wall.KanCount >= 4) return null;

            foreach (var meld in hand.OpenMelds)
                if (meld.Type == MeldType.Pon && hand.ClosedTiles.Any(t => t == meld.Lead))
                    return meld.Lead;
            return null;
        }

        /// <summary>
        /// Should the AI claim this discard for daiminkan (open Kan)?
        /// Requires 3 copies in hand.
        /// </summary>
        public bool ShouldClaimDaiminkan(Tile discard, Hand hand, GameState gameState, int playerIndex)
        {
            if (hand.IsRiichi) return false;
            if (gameState.Wall.KanCount >= 4) return false;
            if (hand.ClosedTiles.Count(t => t == discard) < 3) return false;

            // Medium/Hard: only call daiminkan if there are enough tiles left for rinshan
            return gameState.Wall.TilesRemaining >= 1;
        }

        /// <summary>
        /// Should the AI declare RIICHI?
        /// Called when the hand reaches tenpai.
        /// </summary>
        public bool ShouldDeclareRiichi(Hand hand, GameState gameState, int playerIndex)
        {
            if (!hand.IsFullyClosed) return false;
            if (!hand.IsTenpai()) return false;
            if (gameState.Wall.TilesRemaining < 4) return false;
            if (gameState.Players[playerIndex].Points < 1000) return false;

            return _difficulty switch
            {
                AIDifficulty.Easy   => _rng.NextDouble() < 0.7,
                AIDifficulty.Medium => true,   // Always riichi if conditions met
                AIDifficulty.Hard   => ShouldRiichiHard(hand, gameState, playerIndex),
                _ => true,
            };
        }

        // =====================================================================
        // BEST CHI COMBINATION
        // =====================================================================

        /// <summary>
        /// When the AI decides to call chi, pick the best two tiles from hand
        /// to form the sequence with the claimed tile.
        /// Returns null if no valid chi can be formed.
        /// </summary>
        public (Tile t1, Tile t2)? BestChiCombination(Tile discard, Hand hand)
        {
            if (discard.IsHonour) return null;

            var options = new List<(Tile, Tile, int shanten)>();
            var tiles = hand.ClosedTiles.ToList();

            // Try every valid 2-tile combination from hand that forms a chi with discard
            for (int i = 0; i < tiles.Count; i++)
            {
                for (int j = i + 1; j < tiles.Count; j++)
                {
                    if (!IsValidChi(tiles[i], tiles[j], discard)) continue;

                    // Simulate forming this chi and calculate resulting shanten
                    var remaining = tiles.ToList();
                    remaining.RemoveAt(j);
                    remaining.RemoveAt(i);
                    int s = CalcShantenFromList(remaining, hand.OpenMelds.Count + 1);
                    options.Add((tiles[i], tiles[j], s));
                }
            }

            if (options.Count == 0) return null;

            var best = options.OrderBy(o => o.shanten).First();
            return (best.Item1, best.Item2);
        }

        // =====================================================================
        // Private helpers
        // =====================================================================

        private bool ShantenImprovedByPon(Tile tile, Hand hand)
        {
            int before = CalcShantenFromList(hand.ClosedTiles.ToList(), hand.OpenMelds.Count);

            // Simulate the pon: remove 2 tiles, add 1 open meld
            var after = hand.ClosedTiles.ToList();
            after.Remove(tile);
            after.Remove(tile);
            int afterShanten = CalcShantenFromList(after, hand.OpenMelds.Count + 1);

            return afterShanten < before;
        }

        private bool ShantenImprovedByChi(Tile discard, Tile t1, Tile t2, Hand hand)
        {
            int before = CalcShantenFromList(hand.ClosedTiles.ToList(), hand.OpenMelds.Count);

            var after = hand.ClosedTiles.ToList();
            after.Remove(t1);
            after.Remove(t2);
            int afterShanten = CalcShantenFromList(after, hand.OpenMelds.Count + 1);

            return afterShanten < before;
        }

        private bool IsDefensiveDiscard(Tile tile, GameState gameState, int playerIndex)
        {
            // Hard mode: avoid calling if opponents are in riichi and this tile is dangerous
            // Simple approximation: if a riichi player discarded this tile type recently, it's safer
            foreach (var opponent in gameState.Players)
            {
                if (opponent.SeatIndex == playerIndex) continue;
                if (!opponent.DeclaredRiichi) continue;
                // If the tile is in the riichi player's discards, it may be safe (genbutsu)
                if (opponent.Discards.Contains(tile)) return false;  // Safe to call
            }
            return false;  // Default: not defensive
        }

        private bool ShouldRiichiHard(Hand hand, GameState gameState, int playerIndex)
        {
            // Hard difficulty: consider skipping riichi if hand value is very low
            // For now, same as medium (riichi almost always correct in EMA rules)
            return true;
        }

        /// <summary>Calculate shanten from a raw tile list with a given open meld count.</summary>
        private static int CalcShantenFromList(List<Tile> tiles, int openMeldCount)
        {
            // Use a temporary Hand object to leverage its shanten calculation
            var tempHand = new Hand();
            tempHand.AddTiles(tiles);
            // Account for open melds reducing required closed sets
            // (A simplified approximation — full accuracy would simulate real Hand state)
            int s = tempHand.Shanten();
            return s;
        }

        private static bool IsValidChi(Tile t1, Tile t2, Tile t3)
        {
            if (t1.Suit != t2.Suit || t1.Suit != t3.Suit) return false;
            if (t1.IsHonour) return false;
            var vals = new[] { t1.Value, t2.Value, t3.Value };
            Array.Sort(vals);
            return vals[1] == vals[0] + 1 && vals[2] == vals[1] + 1;
        }
    }
}
