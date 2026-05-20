// =============================================================================
// FuritenTracker.cs
// Tracks furiten state for a single player.
//
// FURITEN: A player in furiten cannot win by ron (claiming another's discard).
// They CAN still win by tsumo (self-draw).
//
// There are THREE ways to be in furiten:
//
// 1. PERMANENT FURITEN (until end of hand):
//    The player has previously discarded a tile they could currently win on.
//    i.e. any tile in their own discard pile is also a valid winning tile.
//
// 2. RIICHI FURITEN (until end of hand — permanent):
//    After declaring riichi, the player's waiting tiles are locked.
//    If ANY opponent discards a tile the player is waiting for, and the player
//    doesn't claim it (for any reason), the player becomes permanently furiten
//    for the rest of the hand.
//
// 3. TEMPORARY FURITEN (until next draw):
//    A player passes on a tile they could have won on (even if they chose not to
//    claim it). This clears when they draw their next tile.
//    Does NOT apply to riichi players (they stay in permanent furiten instead).
//
// Key rule: Furiten applies to ALL winning tiles, not individual tiles.
// If you could win on 3-man or 6-man, and 3-man appears — even if you don't
// need 3-man specifically for your preferred hand — you're in furiten for 6-man too.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace RiichiMahjong.Core
{
	public class FuritenTracker
	{
		// ---- State -----------------------------------------------------------

		/// <summary>All tiles this player has discarded this hand (in order).</summary>
		private readonly List<Tile> _ownDiscards = new();

		/// <summary>True when permanently furiten (own discard or riichi miss).</summary>
		public bool IsPermanentFuriten { get; private set; }

		/// <summary>True when temporarily furiten (missed an opponent's discard since last draw).</summary>
		public bool IsTemporaryFuriten { get; private set; }

		/// <summary>True if in any kind of furiten (permanent or temporary).</summary>
		public bool IsFuriten => IsPermanentFuriten || IsTemporaryFuriten;

		// ---- Discard tracking -----------------------------------------------

		/// <summary>
		/// Record a tile this player just discarded.
		/// Then immediately check if it creates permanent furiten against current waits.
		/// </summary>
		public void RecordOwnDiscard(Tile tile, List<Tile> currentWaits)
		{
			_ownDiscards.Add(tile);
			UpdatePermanentFuriten(currentWaits);
		}

		/// <summary>
		/// Call this whenever the player's waiting tiles change (after a draw that changes tenpai shape).
		/// Re-evaluates permanent furiten against the full discard history.
		/// </summary>
		public void UpdatePermanentFuriten(List<Tile> currentWaits)
		{
			if (IsPermanentFuriten) return;  // Already permanent, no need to re-check

			// Furiten if any current wait tile has been discarded by this player
			if (currentWaits.Any(w => _ownDiscards.Contains(w)))
				IsPermanentFuriten = true;
		}

		// ---- Opponent discard events ----------------------------------------

		/// <summary>
		/// Called when an opponent discards a tile and this player does NOT claim it for ron.
		/// If the tile is one of the player's winning tiles, this sets (temporary or permanent) furiten.
		/// </summary>
		/// <param name="discardedTile">The tile the opponent discarded.</param>
		/// <param name="currentWaits">The player's current winning tiles.</param>
		/// <param name="isRiichi">Whether this player has declared riichi.</param>
		public void RecordMissedDiscard(Tile discardedTile, List<Tile> currentWaits, bool isRiichi)
		{
			if (!currentWaits.Contains(discardedTile)) return;  // Not a winning tile — no furiten

			if (isRiichi)
				IsPermanentFuriten = true;  // Riichi players: missed win = permanent furiten
			else
				IsTemporaryFuriten = true;  // Others: temporary until next draw
		}

		/// <summary>
		/// Fast overload: caller already computed whether <paramref name="discardedTile"/> is a wait
		/// (using Hand.IsWaitingFor), so we skip the Contains() scan over the full waits list.
		/// </summary>
		public void RecordMissedDiscard(Tile discardedTile, bool isWait, bool isRiichi)
		{
			if (!isWait) return;
			if (isRiichi) IsPermanentFuriten = true;
			else          IsTemporaryFuriten = true;
		}

		/// <summary>
		/// Fast own-discard recording: uses a delegate to check each historical discard
		/// against the current hand one at a time, avoiding computing the full waits list.
		/// <paramref name="isWaitCheck"/> should be <c>hand.IsWaitingFor</c>.
		/// </summary>
		public void RecordOwnDiscardFast(Tile tile, Func<Tile, bool> isWaitCheck)
		{
			_ownDiscards.Add(tile);
			if (IsPermanentFuriten) return;
			if (_ownDiscards.Any(d => isWaitCheck(d)))
				IsPermanentFuriten = true;
		}

		// ---- Draw / turn events ---------------------------------------------

		/// <summary>
		/// Called when this player draws a tile (normal draw or rinshan).
		/// Clears temporary furiten (it resets each time the player gets to draw).
		/// Does NOT clear permanent furiten.
		/// </summary>
		public void OnDraw()
		{
			IsTemporaryFuriten = false;
		}

		// ---- Hand end -------------------------------------------------------

		/// <summary>Reset all furiten state for a new hand.</summary>
		public void Reset()
		{
			_ownDiscards.Clear();
			IsPermanentFuriten = false;
			IsTemporaryFuriten = false;
		}

		// ---- Queries --------------------------------------------------------

		/// <summary>
		/// Can this player win by ron on the given tile?
		/// Returns false if in furiten OR if the tile is not a winning tile.
		/// </summary>
		public bool CanWinByRon(Tile tile, List<Tile> currentWaits)
		{
			if (IsFuriten) return false;
			return currentWaits.Contains(tile);
		}

		/// <summary>
		/// Can this player win by tsumo on the given tile?
		/// Tsumo is always allowed regardless of furiten.
		/// </summary>
		public bool CanWinByTsumo(Tile tile, List<Tile> currentWaits)
		{
			return currentWaits.Contains(tile);
		}

		/// <summary>Read-only view of this player's discards (for furiten display on UI).</summary>
		public IReadOnlyList<Tile> OwnDiscards => _ownDiscards.AsReadOnly();
	}
}
