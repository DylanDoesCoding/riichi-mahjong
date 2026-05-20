// =============================================================================
// Hand.cs
// Manages a single player's hand during a game of Riichi Mahjong.
//
// A player's state at any time consists of:
//   - Closed tiles  : tiles in hand, not yet committed to any meld (13 or 14)
//   - Open melds    : called sets (chi/pon/kan) that are face-up on the table
//   - Riichi status : whether the player has declared riichi
//   - Draw          : the most recently drawn tile (if any)
//
// This class handles:
//   - Adding/removing tiles
//   - Sorting the hand for display
//   - Checking whether the hand is fully concealed
//   - Computing the shanten number (how many tiles away from tenpai)
//   - Listing all tiles the hand is waiting for (when in tenpai)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace RiichiMahjong.Core
{
	public class Hand
	{
		// ---- Internal state --------------------------------------------------

		private readonly List<Tile> _closedTiles;  // Tiles in hand (not melded)
		private readonly List<Meld> _openMelds;    // Called/declared melds, displayed right of hand

		// ---- Public properties -----------------------------------------------

		/// <summary>The tiles currently held in the concealed part of the hand.</summary>
		public ReadOnlyCollection<Tile> ClosedTiles => _closedTiles.AsReadOnly();

		/// <summary>All open (and closed-kan) melds made by this player.</summary>
		public ReadOnlyCollection<Meld> OpenMelds => _openMelds.AsReadOnly();

		/// <summary>Whether the player has declared riichi.</summary>
		public bool IsRiichi { get; private set; }

		/// <summary>Whether the player has declared double-riichi (riichi on first turn).</summary>
		public bool IsDoubleRiichi { get; private set; }

		/// <summary>Whether ippatsu is still active (win within first uninterrupted turn after riichi).</summary>
		public bool IsIppatsu { get; private set; }

		/// <summary>The most recently drawn tile (null if the player just discarded or hasn't drawn yet).</summary>
		public Tile? DrawnTile { get; private set; }

		/// <summary>
		/// True if the hand has NO open melds (chi/pon/open-kan).
		/// Note: a concealed kan (KanClosed) does NOT open the hand.
		/// </summary>
		public bool IsFullyClosed => !_openMelds.Any(m => m.IsOpen);

		/// <summary>Total tile count: closed + all meld tiles.</summary>
		public int TileCount => _closedTiles.Count + _openMelds.Sum(m => m.Tiles.Count);

		// ---- Constructor -----------------------------------------------------

		public Hand()
		{
			_closedTiles = new List<Tile>(14);
			_openMelds   = new List<Meld>(4);
			IsRiichi     = false;
			IsIppatsu    = false;
			DrawnTile    = null;
		}

		// ---- Tile management -------------------------------------------------

		/// <summary>Add a tile to the closed hand (on draw or initial deal).</summary>
		public void AddTile(Tile tile)
		{
			_closedTiles.Add(tile);
			DrawnTile = tile;
		}

		/// <summary>Add multiple tiles at once (used for initial deal).</summary>
		public void AddTiles(IEnumerable<Tile> tiles)
		{
			foreach (var t in tiles)
				_closedTiles.Add(t);
		}

		/// <summary>
		/// Remove a tile from the closed hand (on discard).
		/// Returns false if the tile was not found.
		/// </summary>
		public bool RemoveTile(Tile tile)
		{
			int idx = _closedTiles.IndexOf(tile);
			if (idx < 0) return false;
			_closedTiles.RemoveAt(idx);
			DrawnTile = null;
			return true;
		}

		/// <summary>
		/// Remove a tile from the closed hand by its index in the sorted display order.
		/// The hand should be sorted before calling this.
		/// </summary>
		public Tile RemoveTileAt(int index)
		{
			var tile = _closedTiles[index];
			_closedTiles.RemoveAt(index);
			DrawnTile = null;
			return tile;
		}

		// ---- Meld management -------------------------------------------------

		/// <summary>
		/// Complete a Chi call: remove the two contributing tiles from the closed hand
		/// and add the formed meld to open melds.
		/// <paramref name="t1"/> and <paramref name="t2"/> are the tiles from hand;
		/// <paramref name="claimed"/> is the tile taken from the discard.
		/// </summary>
		public void ApplyChi(Tile t1, Tile t2, Tile claimed, ClaimSource source)
		{
			RemoveTile(t1);
			RemoveTile(t2);
			_openMelds.Add(Meld.Chi(t1, t2, claimed, claimed, source));
		}

		/// <summary>
		/// Complete a Pon call: remove two matching tiles from the closed hand
		/// and add the triplet meld.
		/// </summary>
		public void ApplyPon(Tile tile, Tile claimed, ClaimSource source)
		{
			RemoveTile(tile);
			RemoveTile(tile);
			_openMelds.Add(Meld.Pon(tile, claimed, source));
		}

		/// <summary>
		/// Declare an open Kan (called on another's discard):
		/// remove 3 matching tiles from hand, form a KanOpen meld.
		/// </summary>
		public void ApplyKanOpen(Tile tile, Tile claimed, ClaimSource source)
		{
			RemoveTile(tile);
			RemoveTile(tile);
			RemoveTile(tile);
			_openMelds.Add(Meld.KanOpen(tile, claimed, source));
		}

		/// <summary>
		/// Declare a concealed Kan (all 4 from own hand):
		/// remove 4 matching tiles from hand, form a KanClosed meld.
		/// </summary>
		public void ApplyKanClosed(Tile tile)
		{
			RemoveTile(tile);
			RemoveTile(tile);
			RemoveTile(tile);
			RemoveTile(tile);
			_openMelds.Add(Meld.KanClosed(tile));
		}

		/// <summary>
		/// Extend an existing open Pon into a Kan (add the 4th tile from own draw):
		/// replace the Pon meld with a KanExtended meld.
		/// </summary>
		public bool ApplyKanExtended(Tile tile)
		{
			// Find the matching Pon
			int ponIdx = _openMelds.FindIndex(m => m.Type == MeldType.Pon && m.Lead == tile);
			if (ponIdx < 0) return false;

			RemoveTile(tile);  // Remove the 4th copy from hand
			_openMelds[ponIdx] = Meld.KanExtended(tile);
			return true;
		}

		// ---- Riichi ----------------------------------------------------------

		/// <summary>
		/// Mark the hand as riichi-declared. The hand is locked from this point.
		/// <paramref name="isDouble"/> is true when riichi is declared on the very first turn.
		/// </summary>
		public void DeclareRiichi(bool isDouble = false)
		{
			IsRiichi      = true;
			IsDoubleRiichi = isDouble;
			IsIppatsu     = true;  // Ippatsu window opens
		}

		/// <summary>
		/// Called when an opponent makes a call (chi/pon/kan), breaking the ippatsu window.
		/// </summary>
		public void BreakIppatsu() => IsIppatsu = false;

		/// <summary>Called when the player's own next turn starts (after winning chance passes).</summary>
		public void ClearIppatsu() => IsIppatsu = false;

		// ---- Sorting ---------------------------------------------------------

		/// <summary>
		/// Sort the closed tiles for display: Man → Pin → Sou → Wind → Dragon,
		/// ascending value within each suit. The drawn tile stays at the end.
		/// </summary>
		public void Sort()
		{
			// Temporarily remove the drawn tile so it stays at the end after sorting,
			// keeping the visual "lift" highlight working in the UI.
			// Tile is a class — use the reference directly, not .Value (which is the int property).
			var drawn = DrawnTile;
			if (drawn != null)
			{
				int idx = _closedTiles.LastIndexOf(drawn);
				if (idx >= 0) _closedTiles.RemoveAt(idx);
			}

			_closedTiles.Sort((a, b) =>
			{
				int suitCmp = ((int)a.Suit).CompareTo((int)b.Suit);
				return suitCmp != 0 ? suitCmp : a.Value.CompareTo(b.Value);
			});

			if (drawn != null)
				_closedTiles.Add(drawn);  // DrawnTile property unchanged
		}

		// ---- Shanten calculation ---------------------------------------------

		/// <summary>
		/// Calculate the shanten number of the current closed hand (ignoring open melds
		/// for the purposes of the standard hand shapes, but accounting for meld count).
		///
		/// Shanten = number of tiles still needed to reach tenpai.
		///   -1 = complete (winning hand)
		///    0 = tenpai (one tile away from winning)
		///    1 = one tile away from tenpai
		///   ... and so on.
		///
		/// We calculate the minimum shanten across three hand types:
		///   1. Standard  (4 sets + 1 pair)
		///   2. Seven Pairs (Chiitoitsu)
		///   3. Thirteen Orphans (Kokushi)
		/// </summary>
		public int Shanten()
		{
			var tiles = _closedTiles.ToList();
			int mentuCount = _openMelds.Count(m => !m.IsKan || m.Type == MeldType.KanClosed)
						   + _openMelds.Count(m => m.IsKan);
			// For shanten purposes, count each open non-kan meld as a complete mentsu
			int openMentsu = _openMelds.Count(m => m.IsOpen || m.IsKan);

			int s = ShantenStandard(tiles, openMentsu);
			if (IsFullyClosed)
			{
				s = Math.Min(s, ShantenSevenPairs(tiles));
				s = Math.Min(s, ShantenThirteenOrphans(tiles));
			}
			return s;
		}

		/// <summary>Returns true if the hand is in tenpai (shanten == 0).</summary>
		public bool IsTenpai() => Shanten() == 0;

		/// <summary>
		/// Fast check: returns true if <paramref name="candidate"/> would complete this hand
		/// (i.e. adding it gives shanten == -1).  Only 2 Shanten calls — use this instead of
		/// GetWaitingTiles().Contains() when you only need to test a single tile.
		/// </summary>
		public bool IsWaitingFor(Tile candidate)
		{
			_closedTiles.Add(candidate);
			bool win = Shanten() == -1;
			_closedTiles.RemoveAt(_closedTiles.Count - 1);
			return win;
		}

		/// <summary>
		/// Returns all tiles that would complete the hand (the "waits").
		/// Only valid when the hand is in tenpai.
		/// </summary>
		public List<Tile> GetWaitingTiles()
		{
			var waits = new List<Tile>();
			if (Shanten() != 0) return waits;

			// Test every possible tile type (34 types) as a candidate winning tile
			foreach (var candidate in AllTileTypes())
			{
				_closedTiles.Add(candidate);
				if (Shanten() == -1)
					waits.Add(candidate);
				_closedTiles.RemoveAt(_closedTiles.Count - 1);
			}
			return waits;
		}

		// ---- Private shanten algorithms -------------------------------------

		/// <summary>
		/// Standard 4-set + 1-pair shanten algorithm.
		/// Uses a recursive tile-counting approach.
		/// openMentsu: number of complete sets already formed via open melds.
		/// </summary>
		private static int ShantenStandard(List<Tile> tiles, int openMentsu)
		{
			// Count tiles by a compact int representation for fast processing
			int[] counts = TilesToCounts(tiles);

			int best = 8 - openMentsu * 2;  // Worst case: 0 mentsu, 0 pairs found

			// Try each tile as the pair candidate.
			// Pass jantai=1 so the recursive function knows the pair is already accounted for
			// and does NOT apply the "still need a pair" penalty when all set-slots are full.
			for (int i = 0; i < 34; i++)
			{
				if (counts[i] < 2) continue;
				counts[i] -= 2;
				int s = CalcShantenRecursive(counts, openMentsu, 0, 1);
				best = Math.Min(best, s);
				counts[i] += 2;
			}
			// Also try with no pair (pair still to be found)
			{
				int s = CalcShantenRecursive(counts, openMentsu, 0, 0);
				best = Math.Min(best, s);
			}
			return best;
		}

		private static int CalcShantenRecursive(int[] counts, int mentsu, int taatsu, int jantai)
		{
			// mentsu  = complete sets (3-tile)
			// taatsu  = partial sets (2-tile pairs or sequences)
			// jantai  = pair (0 or 1)
			// We want to maximise mentsu*2 + taatsu + jantai, then shanten = 8 - that value

			int total = mentsu * 2 + taatsu + jantai;
			if (mentsu + taatsu > 4) total -= (mentsu + taatsu - 4);  // Can't have more than 4 sets
			if (mentsu + taatsu == 4 && jantai == 0) total -= 1;      // Need a pair slot

			int best = 8 - total;

			for (int i = 0; i < 34; i++)
			{
				if (counts[i] == 0) continue;

				// Try triplet (pon/ankou)
				if (counts[i] >= 3)
				{
					counts[i] -= 3;
					best = Math.Min(best, CalcShantenRecursive(counts, mentsu + 1, taatsu, jantai));
					counts[i] += 3;
				}

				// Try sequence (chi) — only for number suits, check suit boundary
				int suit = i / 9;
				if (suit < 3)  // Only Man/Pin/Sou (not Wind/Dragon)
				{
					int pos = i % 9;

					if (pos <= 6 && counts[i + 1] > 0 && counts[i + 2] > 0)
					{
						counts[i]--; counts[i + 1]--; counts[i + 2]--;
						best = Math.Min(best, CalcShantenRecursive(counts, mentsu + 1, taatsu, jantai));
						counts[i]++; counts[i + 1]++; counts[i + 2]++;
					}

					// Try kanchan (middle wait: e.g. 2-4 waiting for 3)
					if (pos <= 6 && counts[i + 2] > 0)
					{
						counts[i]--; counts[i + 2]--;
						best = Math.Min(best, CalcShantenRecursive(counts, mentsu, taatsu + 1, jantai));
						counts[i]++; counts[i + 2]++;
					}

					// Try sequential pair (e.g. 3-4 waiting for 2 or 5)
					if (pos <= 7 && counts[i + 1] > 0)
					{
						counts[i]--; counts[i + 1]--;
						best = Math.Min(best, CalcShantenRecursive(counts, mentsu, taatsu + 1, jantai));
						counts[i]++; counts[i + 1]++;
					}
				}

				// Try pair as taatsu (2 of same tile for a partial set)
				if (counts[i] >= 2)
				{
					counts[i] -= 2;
					best = Math.Min(best, CalcShantenRecursive(counts, mentsu, taatsu + 1, jantai));
					counts[i] += 2;
				}

				// Only process the FIRST nonzero tile at each recursion level.
				// The recursive calls handle tiles at higher indices, so continuing
				// the loop here would re-explore the same combinations exponentially.
				break;
			}

			return best;
		}

		/// <summary>
		/// Seven Pairs (Chiitoitsu) shanten:
		/// Need 7 pairs. Shanten = 6 - (number of pairs already in hand).
		/// Duplicate pairs (3+ of same tile) only count once.
		/// </summary>
		private static int ShantenSevenPairs(List<Tile> tiles)
		{
			int[] counts = TilesToCounts(tiles);
			int pairs = counts.Count(c => c >= 2);
			return 6 - pairs;
		}

		/// <summary>
		/// Thirteen Orphans (Kokushi Musou) shanten:
		/// Need one each of the 13 terminal/honour types, plus one duplicate.
		/// Shanten = 13 - (unique terminals/honours in hand) - (1 if any duplicate).
		/// </summary>
		private static int ShantenThirteenOrphans(List<Tile> tiles)
		{
			// The 13 required tile types (indices in the 34-tile count array)
			int[] orphanIds =
			{
				0,  // 1m
				8,  // 9m
				9,  // 1p
				17, // 9p
				18, // 1s
				26, // 9s
				27, // East
				28, // South
				29, // West
				30, // North
				31, // White Dragon
				32, // Green Dragon
				33, // Red Dragon
			};

			int[] counts   = TilesToCounts(tiles);
			int unique     = orphanIds.Count(id => counts[id] >= 1);
			int hasDuplicate = orphanIds.Any(id => counts[id] >= 2) ? 1 : 0;

			return 13 - unique - hasDuplicate;
		}

		// ---- Static helpers --------------------------------------------------

		/// <summary>
		/// Convert a list of tiles into a count array indexed by TileId (0–33).
		/// </summary>
		public static int[] TilesToCounts(IEnumerable<Tile> tiles)
		{
			var counts = new int[34];
			foreach (var t in tiles)
				counts[t.TileId]++;
			return counts;
		}

		/// <summary>Returns one instance of every possible tile type (34 total).</summary>
		public static IEnumerable<Tile> AllTileTypes()
		{
			for (int v = 1; v <= 9; v++) yield return Tile.Man(v);
			for (int v = 1; v <= 9; v++) yield return Tile.Pin(v);
			for (int v = 1; v <= 9; v++) yield return Tile.Sou(v);
			yield return Tile.Wind(WindDirection.East);
			yield return Tile.Wind(WindDirection.South);
			yield return Tile.Wind(WindDirection.West);
			yield return Tile.Wind(WindDirection.North);
			yield return Tile.Dragon(DragonType.White);
			yield return Tile.Dragon(DragonType.Green);
			yield return Tile.Dragon(DragonType.Red);
		}

		// ---- Reset (between hands) ------------------------------------------

		/// <summary>Clear all tiles and state — called at the start of each new hand.</summary>
		public void Reset()
		{
			_closedTiles.Clear();
			_openMelds.Clear();
			IsRiichi       = false;
			IsDoubleRiichi = false;
			IsIppatsu      = false;
			DrawnTile      = null;
		}

		// ---- Display --------------------------------------------------------

		public override string ToString()
		{
			string closed = string.Join(" ", _closedTiles);
			string melds  = _openMelds.Count > 0
				? " | " + string.Join(" ", _openMelds)
				: "";
			string riichi = IsRiichi ? " [RIICHI]" : "";
			return closed + melds + riichi;
		}
	}
}
