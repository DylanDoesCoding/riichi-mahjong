// =============================================================================
// GameState.cs
// The central state machine for a full game of Riichi Mahjong.
//
// This class owns all game state: players, wall, scores, round/dealer tracking,
// counters, and the current turn phase. It exposes methods the UI and AI call
// to advance the game, and fires events the UI listens to for rendering updates.
//
// Turn flow:
//   DrawPhase     → player draws a tile
//   ActionPhase   → player acts (discard / riichi / tsumo / kan)
//   ClaimWindow   → other players may claim the discard (ron / pon / chi / kan)
//   HandEnd       → win or exhaustive draw is resolved, scores updated
//   BetweenHands  → brief pause while UI shows results, then next hand starts
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace RiichiMahjong.Core
{
	// -------------------------------------------------------------------------
	// Enums
	// -------------------------------------------------------------------------

	/// <summary>Seat positions. Index matches player array order.</summary>
	public enum Seat { East = 0, South = 1, West = 2, North = 3 }

	/// <summary>Which phase the current turn is in.</summary>
	public enum TurnPhase
	{
		DrawPhase,      // Current player needs to draw
		ActionPhase,    // Current player has 14 tiles and must act
		ClaimWindow,    // A tile was discarded — waiting for claim decisions
		HandEnd,        // Hand is over (win or draw) — resolving payments
		GameOver,       // Game has ended — show final scores
	}

	/// <summary>
	/// Why a hand ended.
	/// </summary>
	public enum HandEndReason
	{
		Tsumo,            // Player won by self-draw
		Ron,              // Player(s) won by discard claim
		ExhaustiveDraw,   // Wall ran out — ryuukyoku
		NagashiMangan,    // (Optional rule — not in EMA base rules; placeholder)
		AbortiveDraw,     // Mid-game abort: Suufonren da / Suukaikan / Kyuushu / Sanchahou
	}

	// -------------------------------------------------------------------------
	// PlayerState — all per-player data for one hand
	// -------------------------------------------------------------------------

	public class PlayerState
	{
		public int            SeatIndex       { get; }       // 0=East, 1=South, 2=West, 3=North
		public Hand           Hand            { get; }       = new();
		public FuritenTracker Furiten         { get; }       = new();
		public List<Tile>     Discards        { get; }       = new();
		public int            Points          { get; set; }  = 30000;
		public bool           IsHuman         { get; }
		public string         Name            { get; }

		// Riichi tracking
		public bool           DeclaredRiichi  { get; set; }
		public int            RiichiBetTurn   { get; set; }  = -1;  // Turn number when riichi declared

		// Win/tenpai status at end of hand
		public bool           IsTenpaiAtDraw  { get; set; }

		// Nagashi Mangan tracking — cleared each hand
		/// <summary>Number of times this player has drawn a tile from the wall this hand.</summary>
		public int            WallDrawCount         { get; set; } = 0;
		/// <summary>True if any opponent has claimed (pon/chi/kan) from this player's discards.</summary>
		public bool           HasDiscardBeenClaimed { get; set; } = false;

		public PlayerState(int seatIndex, bool isHuman, string name)
		{
			SeatIndex = seatIndex;
			IsHuman   = isHuman;
			Name      = name;
		}

		/// <summary>The seat wind for this player given the current dealer seat.</summary>
		public WindDirection GetSeatWind(int dealerSeatIndex)
		{
			int offset = (SeatIndex - dealerSeatIndex + 4) % 4;
			return (WindDirection)(offset + 1);  // East=1, South=2, West=3, North=4
		}
	}

	// -------------------------------------------------------------------------
	// GameState
	// -------------------------------------------------------------------------

	public class GameState
	{
		// ---- Configuration --------------------------------------------------

		public const int StartingPoints   = 30000;
		public const int RiichiBetAmount  = 1000;
		public const int CounterPointValue = 300;

		// ---- Players --------------------------------------------------------

		public PlayerState[] Players       { get; private set; }
		public int           HumanSeat     { get; }           // Which seat the human player is

		// ---- Round / hand tracking ------------------------------------------

		/// <summary>Current round wind: East (1) or South (2) in a standard game.</summary>
		public WindDirection RoundWind     { get; private set; } = WindDirection.East;

		/// <summary>Index into Players[] for the current dealer (East seat).</summary>
		public int           DealerIndex   { get; private set; } = 0;

		/// <summary>Number of times the dealer has held (non-rotation hands this dealer).</summary>
		public int           Counters      { get; private set; } = 0;  // Honba sticks

		/// <summary>Total riichi bets currently sitting on the table.</summary>
		public int           RiichiBetsOnTable { get; private set; } = 0;

		/// <summary>Current turn number within this hand (0-based, increments each full cycle).</summary>
		public int           TurnNumber    { get; private set; } = 0;

		/// <summary>
		/// True until the first pon/chi/kan claim is made this hand.
		/// Required for Tenhou/Chiihou/Renhou — any claim breaks the uninterrupted round.
		/// </summary>
		private bool _firstRoundUninterrupted = true;

		/// <summary>Seat index of each kan declarant this hand (in declaration order).</summary>
		private readonly List<int> _kanDeclarants = new();

		/// <summary>
		/// Kuikae forbidden tiles: (suit, value) pairs that the player who just claimed chi
		/// is not allowed to discard immediately.  Cleared after any legal discard.
		/// </summary>
		private readonly HashSet<(TileSuit, int)> _kuikaeForbidden = new();

		// ---- Wall -----------------------------------------------------------

		public TileWall      Wall          { get; private set; } = null!;

		// ---- Turn state -----------------------------------------------------

		public TurnPhase     Phase         { get; private set; } = TurnPhase.DrawPhase;

		/// <summary>Index into Players[] whose turn it currently is.</summary>
		public int           CurrentPlayerIndex { get; private set; } = 0;

		/// <summary>The tile most recently discarded (in the claim window).</summary>
		public Tile?         PendingDiscard { get; private set; }

		/// <summary>Index of the player who discarded PendingDiscard.</summary>
		public int           DiscarderIndex { get; private set; } = -1;

		// ---- Events (UI subscribes to these) --------------------------------

		public event Action<int>?                        OnTileDrawn;            // playerIndex
		public event Action<int, Tile>?                  OnTileDiscarded;        // playerIndex, tile
		public event Action<int, Meld>?                  OnMeldDeclared;         // playerIndex, meld
		public event Action<int>?                        OnRiichiDeclared;       // playerIndex
		public event Action<HandEndReason, int[]>?       OnHandEnd;              // reason, winner indices
		public event Action?                             OnNewHand;
		public event Action?                             OnGameOver;
		/// <summary>
		/// Fired when a kakan opens a chankan claim window.
		/// Subscribers must call <see cref="ResolveChankan"/> (no rob) or
		/// <see cref="ClaimRon"/> (rob) to advance the game.
		/// </summary>
		public event Action<int, Tile>?                  OnChankanOpened;        // kakanPlayerIndex, tile

		// ---- Last-win scoring data (for scoring overlay display) ------------

		/// <summary>Score result of the most recent Tsumo/Ron win. Null if last hand was a draw.</summary>
		public ScoreResult?    LastScoreResult   { get; private set; }
		/// <summary>Yaku detected in the most recent win.</summary>
		public YakuCheckResult? LastYakuResult   { get; private set; }
		/// <summary>Context used to evaluate the most recent win (carries dora counts, seat/round winds etc.).</summary>
		public YakuContext?    LastWinContext     { get; private set; }
		/// <summary>Seat index of the last winner (-1 = no win this hand).</summary>
		public int             LastWinnerSeat    { get; private set; } = -1;
		/// <summary>Seat index of the last discarder for a Ron win (-1 = Tsumo or no win).</summary>
		public int             LastDiscarderSeat { get; private set; } = -1;

		// ---- Special-win flags (cleared at hand start / on discard) ----------

		/// <summary>
		/// True when the current player drew their tile from the dead wall (rinshan).
		/// Used by <see cref="GetTsumoMethod"/> to award Rinshan Kaihou.
		/// Cleared when the player discards.
		/// </summary>
		public bool IsRinshanDraw { get; private set; }

		/// <summary>
		/// True while a kakan has opened a chankan claim window.
		/// <see cref="ClaimRon"/> will use <see cref="WinMethod.Chankan"/> during this window.
		/// Cleared by <see cref="ResolveChankan"/> or <see cref="ClaimRon"/>.
		/// </summary>
		public bool IsChankanWindow { get; private set; }

		/// <summary>
		/// Tiles the current player is forbidden from discarding due to kuikae (swap-calling).
		/// Non-empty only immediately after a successful chi claim — cleared on the next legal discard.
		/// Each entry is a (suit, value) pair matching <see cref="Tile.Suit"/> / <see cref="Tile.Value"/>.
		/// </summary>
		public IReadOnlyCollection<(TileSuit Suit, int Value)> KuikaeForbidden => _kuikaeForbidden;

		// ---- Constructor ----------------------------------------------------

		/// <param name="humanSeat">Which seat (0–3) the human player occupies.</param>
		/// <param name="playerNames">Names for each seat (4 entries).</param>
		public GameState(int humanSeat = 0, string[]? playerNames = null)
		{
			HumanSeat = humanSeat;
			playerNames ??= new[] { "You", "CPU 1", "CPU 2", "CPU 3" };

			Players = new PlayerState[4];
			for (int i = 0; i < 4; i++)
				Players[i] = new PlayerState(i, i == humanSeat, playerNames[i]);
		}

		// =====================================================================
		// Game lifecycle
		// =====================================================================

		/// <summary>Start a new game from scratch.</summary>
		public void StartGame()
		{
			foreach (var p in Players)
				p.Points = StartingPoints;

			DealerIndex      = 0;
			RoundWind        = WindDirection.East;
			Counters         = 0;
			RiichiBetsOnTable = 0;

			StartNewHand();
		}

		/// <summary>Set up and begin a new hand.</summary>
		public void StartNewHand()
		{
			// Clear last-win data
			LastScoreResult   = null;
			LastYakuResult    = null;
			LastWinContext    = null;
			LastWinnerSeat    = -1;
			LastDiscarderSeat = -1;
			IsRinshanDraw     = false;
			IsChankanWindow   = false;

			// Reset player hand state
			_firstRoundUninterrupted = true;
			_kanDeclarants.Clear();
			_kuikaeForbidden.Clear();
			foreach (var p in Players)
			{
				p.Hand.Reset();  // We'll add a Reset() to Hand
                p.Furiten.Reset();
                p.Discards.Clear();
                p.DeclaredRiichi        = false;
                p.RiichiBetTurn         = -1;
                p.IsTenpaiAtDraw        = false;
                p.WallDrawCount         = 0;
                p.HasDiscardBeenClaimed = false;
            }

            // Build and shuffle wall, deal tiles.
            // DealInitialHands() always returns hands[0] with 14 tiles (Dealer/East)
            // and hands[1..3] with 13 tiles each.  Map by DealerIndex so the actual
            // dealer gets the 14-tile hand regardless of whose turn it is in the seat array.
            Wall = new TileWall();
            var hands = Wall.DealInitialHands();
            for (int i = 0; i < 4; i++)
            {
                int seat = (DealerIndex + i) % 4;
                Players[seat].Hand.AddTiles(hands[i]);
                Players[seat].Hand.Sort();
            }

            // Dealer (East) starts in ActionPhase (already has 14 tiles)
            CurrentPlayerIndex = DealerIndex;
            TurnNumber         = 0;
            Phase              = TurnPhase.ActionPhase;
            PendingDiscard     = null;
            DiscarderIndex     = -1;

            OnNewHand?.Invoke();
        }

        // =====================================================================
        // Player actions — called by UI (human) or AI
        // =====================================================================

        /// <summary>
        /// Human or AI discards a tile from their hand.
        /// Transitions: ActionPhase → ClaimWindow
        /// </summary>
        public bool Discard(int playerIndex, Tile tile)
        {
            if (Phase != TurnPhase.ActionPhase) return false;
            if (playerIndex != CurrentPlayerIndex) return false;

            // Kuikae: after chi, the player cannot immediately discard the claimed tile
            // or its ryanmen-equivalent (the tile at the other end of a two-sided wait).
            if (_kuikaeForbidden.Count > 0 && _kuikaeForbidden.Contains((tile.Suit, tile.Value)))
                return false;
            _kuikaeForbidden.Clear();  // Restriction expires after any legal discard

            IsRinshanDraw = false;   // discard clears the rinshan flag

            var player = Players[playerIndex];
            if (!player.Hand.RemoveTile(tile)) return false;

            // Record discard
            player.Discards.Add(tile);
            PendingDiscard = tile;
            DiscarderIndex = playerIndex;

            // Ippatsu window expires on the riichi player's own next discard, but NOT on
            // the riichi declaration discard itself (RiichiBetTurn == TurnNumber on that discard).
            // BreakAllIppatsu handles the meld-call path; this handles the draw-pass path.
            if (player.Hand.IsRiichi && player.RiichiBetTurn != TurnNumber)
                player.Hand.ClearIppatsu();

			// Record in the discarding player's own furiten tracker.
			// Checks whether this discard matches any of the player's own historical discards
			// that they're currently waiting for (permanent furiten rule).
            player.Furiten.RecordOwnDiscardFast(tile, t => player.Hand.IsWaitingFor(t));

            // Suukaikan: 4 kans by 2+ different players → abortive draw on the following discard.
            // (A single player with all 4 kans plays on; mixed-player 4-kans abort here.)
            if (Wall.KanCount >= 4 && _kanDeclarants.Distinct().Count() >= 2)
            {
                Phase = TurnPhase.HandEnd;
                OnHandEnd?.Invoke(HandEndReason.AbortiveDraw, Array.Empty<int>());
                return true;
            }

            Phase = TurnPhase.ClaimWindow;
            OnTileDiscarded?.Invoke(playerIndex, tile);
            return true;
        }

        /// <summary>
        /// Declare riichi. Player must be in tenpai with a concealed hand.
		/// The riichi tile is the discard — it's placed sideways.
		/// </summary>
		public bool DeclareRiichi(int playerIndex, Tile discardTile, bool isDouble = false)
		{
			if (Phase != TurnPhase.ActionPhase) return false;
			if (playerIndex != CurrentPlayerIndex) return false;

			var player = Players[playerIndex];
			if (!player.Hand.IsFullyClosed) return false;
			if (player.DeclaredRiichi) return false;
			if (player.Points < RiichiBetAmount) return false;
			if (Wall.TilesRemaining < 4) return false;

			// Pre-validate: the discard tile must actually be in the hand.
			// Without this check, if discardTile is absent, Discard() would fail AFTER
			// riichi flags were already set and points deducted — leaving the hand at 14
			// tiles with IsRiichi=true, making Tsumo/Ron impossible on the next draw.
			if (!player.Hand.ClosedTiles.Any(t => t == discardTile)) return false;

			// Verify tenpai AFTER the proposed discard (13-tile test)
			var testTiles = player.Hand.ClosedTiles.ToList();
			testTiles.Remove(discardTile);   // List<T>.Remove uses value equality
			var tenpaiCheck = new Hand();
			tenpaiCheck.AddTiles(testTiles);
			if (!tenpaiCheck.IsTenpai()) return false;

			// All checks passed — now commit the bet and state changes
			player.Points -= RiichiBetAmount;
			RiichiBetsOnTable++;

			player.Hand.DeclareRiichi(isDouble);
			player.DeclaredRiichi = true;
			player.RiichiBetTurn  = TurnNumber;

			OnRiichiDeclared?.Invoke(playerIndex);

			// Discard the tile sideways. Guaranteed to succeed because we verified
			// the tile is in hand above — Discard() only fails when RemoveTile returns
			// false, which cannot happen now.
			return Discard(playerIndex, discardTile);
		}

		/// <summary>
		/// Player claims the pending discard to form a Pon.
		/// </summary>
		public bool ClaimPon(int claimingPlayerIndex)
		{
			if (Phase != TurnPhase.ClaimWindow) return false;
			if (PendingDiscard == null) return false;
			if (claimingPlayerIndex == DiscarderIndex) return false;

			var player = Players[claimingPlayerIndex];
			int count = player.Hand.ClosedTiles.Count(t => t == PendingDiscard);
			if (count < 2) return false;

			var source = GetClaimSource(DiscarderIndex, claimingPlayerIndex);
			player.Hand.ApplyPon(PendingDiscard, PendingDiscard, source);

			// Clear ippatsu; mark discarder's tile as claimed (for Nagashi Mangan)
			BreakAllIppatsu();
			Players[DiscarderIndex].HasDiscardBeenClaimed = true;
			_firstRoundUninterrupted = false;

			var meld = player.Hand.OpenMelds.Last();
			OnMeldDeclared?.Invoke(claimingPlayerIndex, meld);

			// Move to that player's action phase (they must discard next)
            CurrentPlayerIndex = claimingPlayerIndex;
            PendingDiscard     = null;
            Phase              = TurnPhase.ActionPhase;
            return true;
        }

        /// <summary>
        /// Player claims the pending discard to form a Chi (sequence).
        /// <paramref name="t1"/> and <paramref name="t2"/> are the two tiles from hand.
        /// Can only be claimed from the player to the LEFT (the previous player in turn order).
        /// </summary>
        public bool ClaimChi(int claimingPlayerIndex, Tile t1, Tile t2)
        {
            if (Phase != TurnPhase.ClaimWindow) return false;
            if (PendingDiscard == null) return false;

            // Chi can only be claimed from the player directly to the left
            int leftOf = (claimingPlayerIndex - 1 + 4) % 4;
            if (DiscarderIndex != leftOf) return false;

            // Riichi players cannot call chi
            if (Players[claimingPlayerIndex].DeclaredRiichi) return false;

            // Validate the sequence
            if (!IsValidChi(t1, t2, PendingDiscard)) return false;

            var player = Players[claimingPlayerIndex];
            player.Hand.ApplyChi(t1, t2, PendingDiscard, ClaimSource.Left);

            BreakAllIppatsu();
            Players[DiscarderIndex].HasDiscardBeenClaimed = true;
            _firstRoundUninterrupted = false;

            var meld = player.Hand.OpenMelds.Last();
            OnMeldDeclared?.Invoke(claimingPlayerIndex, meld);

            // Kuikae: record which tiles this player cannot immediately discard.
            SetKuikaeForbidden(t1, t2, PendingDiscard);

            CurrentPlayerIndex = claimingPlayerIndex;
            PendingDiscard     = null;
            Phase              = TurnPhase.ActionPhase;
            return true;
        }

        /// <summary>
        /// Player wins by ron — claiming the pending discard as their winning tile.
        /// </summary>
        public bool ClaimRon(int claimingPlayerIndex)
        {
            if (Phase != TurnPhase.ClaimWindow) return false;
            if (PendingDiscard == null) return false;
            if (claimingPlayerIndex == DiscarderIndex) return false;

            var player = Players[claimingPlayerIndex];

            // Furiten check
            if (player.Furiten.IsFuriten) return false;

            // Add winning tile and verify win
            player.Hand.AddTile(PendingDiscard);
            var winCheck = WinChecker.Check(
                player.Hand.ClosedTiles.ToList(),
                player.Hand.OpenMelds.ToList());
            if (!winCheck.IsWin)
            {
                player.Hand.RemoveTile(PendingDiscard);
                return false;
            }

            // Determine the correct Ron win method:
            //   Chankan  — robbing an opponent's kakan extension
            //   Houtei   — ron on the very last discard (wall is empty)
            //   Ron      — standard ron
            WinMethod winMethod = IsChankanWindow    ? WinMethod.Chankan
                                : Wall.TilesRemaining == 0 ? WinMethod.Houtei
                                : WinMethod.Ron;

            var bestResult = EvaluateBestDecomposition(claimingPlayerIndex, winCheck, winMethod);
            if (!bestResult.HasYaku)
            {
                player.Hand.RemoveTile(PendingDiscard);
                return false;
            }

            IsChankanWindow = false;   // consume the chankan window on a successful ron
            ResolveRon(claimingPlayerIndex, DiscarderIndex, bestResult, winCheck);
            return true;
        }

        /// <summary>
        /// Validate multiple potential ron claimants simultaneously, then resolve:
        ///   • 1 valid claimant → normal Ron payment
        ///   • 2 valid claimants → Double Ron (both paid in full)
        ///   • 3 valid claimants → Sanchahou (abortive draw — no payments)
        /// Returns false only when no candidate can validly win.
        /// </summary>
        public bool ClaimRonMulti(int[] candidateSeats)
        {
            if (Phase != TurnPhase.ClaimWindow) return false;
            if (PendingDiscard == null) return false;

            WinMethod winMethod = IsChankanWindow    ? WinMethod.Chankan
                                : Wall.TilesRemaining == 0 ? WinMethod.Houtei
                                : WinMethod.Ron;

            // Validate each candidate and collect successful ones
            var valid = new List<(int winner, YakuCheckResult yaku, WinCheckResult winCheck)>();
            foreach (int seat in candidateSeats)
            {
                if (seat == DiscarderIndex) continue;
                var p = Players[seat];
                if (p.Furiten.IsFuriten) continue;

                p.Hand.AddTile(PendingDiscard);
                var wc = WinChecker.Check(p.Hand.ClosedTiles.ToList(), p.Hand.OpenMelds.ToList());
                if (!wc.IsWin) { p.Hand.RemoveTile(PendingDiscard); continue; }
                var best = EvaluateBestDecomposition(seat, wc, winMethod);
                if (!best.HasYaku) { p.Hand.RemoveTile(PendingDiscard); continue; }

                // Keep the tile in-hand; winner is confirmed.
                valid.Add((seat, best, wc));
            }

            if (valid.Count == 0) return false;

            IsChankanWindow = false;

            if (valid.Count >= 3)
            {
                // Sanchahou — remove winning tiles and abort
                foreach (var (seat, _, _) in valid)
                    Players[seat].Hand.RemoveTile(PendingDiscard);
                Phase = TurnPhase.HandEnd;
                OnHandEnd?.Invoke(HandEndReason.AbortiveDraw, Array.Empty<int>());
                return true;
            }

            // Sort by seating order from discarder (closest first)
            valid.Sort((a, b) =>
                ((a.winner - DiscarderIndex + 4) % 4)
                    .CompareTo((b.winner - DiscarderIndex + 4) % 4));

            if (valid.Count == 1)
            {
                var (winner, yaku, wc) = valid[0];
                ResolveRon(winner, DiscarderIndex, yaku, wc);
            }
            else
            {
                ResolveDoubleRon(valid, winMethod);
            }

            return true;
        }

        /// <summary>
        /// Current player wins by tsumo (self-draw).
        /// </summary>
        public bool DeclareTsumo()
        {
            if (Phase != TurnPhase.ActionPhase) return false;

            int playerIndex = CurrentPlayerIndex;
            var player = Players[playerIndex];

            var winCheck = WinChecker.Check(
                player.Hand.ClosedTiles.ToList(),
                player.Hand.OpenMelds.ToList());
            if (!winCheck.IsWin) return false;

            var winMethod = GetTsumoMethod(playerIndex);
            var bestResult = EvaluateBestDecomposition(playerIndex, winCheck, winMethod);
            if (!bestResult.HasYaku) return false;

            ResolveTsumo(playerIndex, bestResult, winCheck);
            return true;
        }

        /// <summary>
        /// Declare a concealed Kan (Ankan): player has all 4 copies in their closed hand.
        /// Removes the 4 tiles, draws a rinshan tile from the dead wall, reveals a new dora.
        /// Player remains in ActionPhase and must discard after.
        /// </summary>
        public bool DeclareAnkan(int playerIndex, Tile tile)
        {
            if (Phase != TurnPhase.ActionPhase) return false;
            if (playerIndex != CurrentPlayerIndex) return false;
            if (Wall.KanCount >= 4) return false;

            var player = Players[playerIndex];
            if (player.Hand.IsRiichi && !player.Hand.CanRiichiAnkan(tile)) return false;
            if (player.Hand.ClosedTiles.Count(t => t == tile) < 4) return false;

            player.Hand.ApplyKanClosed(tile);
            _kanDeclarants.Add(playerIndex);

            // Fire meld event before drawing so dora can be broadcast with meld
            var meld = player.Hand.OpenMelds.Last();
            OnMeldDeclared?.Invoke(playerIndex, meld);

            // Draw rinshan tile and reveal new dora — no chankan window for ankan
            var rinshan = Wall.DrawKanReplacement();
            player.Hand.AddTile(rinshan);
            player.Hand.Sort();
            IsRinshanDraw = true;
            Players[playerIndex].WallDrawCount++;

            // Phase/CurrentPlayerIndex unchanged — stay in ActionPhase
            OnTileDrawn?.Invoke(playerIndex);
            return true;
        }

        /// <summary>
        /// Declare an extended Kan (Kakan): player adds the 4th tile to an existing open Pon.
        /// Opens a chankan claim window — opponents in tenpai on that tile may rob it for Ron.
        /// If no one robs, call <see cref="ResolveChankan"/> to draw the rinshan tile.
        /// </summary>
        public bool DeclareKakan(int playerIndex, Tile tile)
        {
            if (Phase != TurnPhase.ActionPhase) return false;
            if (playerIndex != CurrentPlayerIndex) return false;
            if (Wall.KanCount >= 4) return false;

            var player = Players[playerIndex];
            if (player.Hand.IsRiichi) return false;
            if (!player.Hand.OpenMelds.Any(m => m.Type == MeldType.Pon && m.Lead == tile)) return false;
            if (!player.Hand.ClosedTiles.Any(t => t == tile)) return false;

            if (!player.Hand.ApplyKanExtended(tile)) return false;
            _kanDeclarants.Add(playerIndex);

            BreakAllIppatsu();

            // Broadcast the meld immediately (before rinshan — dora unchanged for now)
            var meld = player.Hand.OpenMelds.Last();
            OnMeldDeclared?.Invoke(playerIndex, meld);

            // Open the chankan claim window — rinshan is drawn in ResolveChankan()
            IsChankanWindow = true;
            PendingDiscard  = tile;          // the kakan tile is the "claimable" tile
            DiscarderIndex  = playerIndex;   // so ClaimRon knows who the "discarder" is
            Phase           = TurnPhase.ClaimWindow;

            OnChankanOpened?.Invoke(playerIndex, tile);
            return true;
        }

        /// <summary>
        /// Complete a kakan after the chankan claim window passes with no rob.
        /// Draws the rinshan tile, reveals the new dora, and returns the game to ActionPhase.
        /// </summary>
        public void ResolveChankan()
        {
            if (!IsChankanWindow) return;

            IsChankanWindow = false;
            PendingDiscard  = null;
            // DiscarderIndex is the kakan player — CurrentPlayerIndex hasn't changed.

            // Draw rinshan and reveal new dora indicator
            var player  = Players[CurrentPlayerIndex];
            var rinshan = Wall.DrawKanReplacement();
            player.Hand.AddTile(rinshan);
            player.Hand.Sort();
            IsRinshanDraw = true;
            player.WallDrawCount++;
            Phase         = TurnPhase.ActionPhase;

            OnTileDrawn?.Invoke(CurrentPlayerIndex);
        }

        /// <summary>
        /// Claim the pending discard to form an open Kan (Daiminkan).
        /// Player must have 3 copies in their closed hand; the discard provides the 4th.
        /// Draws a rinshan replacement after claiming.
        /// </summary>
        public bool ClaimDaiminkan(int claimingPlayerIndex)
        {
            if (Phase != TurnPhase.ClaimWindow) return false;
            if (PendingDiscard == null) return false;
            if (claimingPlayerIndex == DiscarderIndex) return false;
            if (Wall.KanCount >= 4) return false;

            var player = Players[claimingPlayerIndex];
            if (player.Hand.IsRiichi) return false;
            if (player.Hand.ClosedTiles.Count(t => t == PendingDiscard) < 3) return false;

            var source = GetClaimSource(DiscarderIndex, claimingPlayerIndex);
            player.Hand.ApplyKanOpen(PendingDiscard, PendingDiscard, source);
            _kanDeclarants.Add(claimingPlayerIndex);

            BreakAllIppatsu();
            Players[DiscarderIndex].HasDiscardBeenClaimed = true;
            _firstRoundUninterrupted = false;

            var rinshan = Wall.DrawKanReplacement();
            player.Hand.AddTile(rinshan);
            player.Hand.Sort();
            IsRinshanDraw = true;
            Players[claimingPlayerIndex].WallDrawCount++;

            // Fire meld event BEFORE resetting DiscarderIndex so UI can remove the discard
            var meld = player.Hand.OpenMelds.Last();
            OnMeldDeclared?.Invoke(claimingPlayerIndex, meld);

            // Now advance to the claiming player's action phase
            CurrentPlayerIndex = claimingPlayerIndex;
            PendingDiscard     = null;
            DiscarderIndex     = -1;
            Phase              = TurnPhase.ActionPhase;

            OnTileDrawn?.Invoke(claimingPlayerIndex);
            return true;
        }

        /// <summary>
        /// Advance from DrawPhase — draw the next tile for the current player.
        /// </summary>
        public Tile? DrawForCurrentPlayer()
        {
            if (Phase != TurnPhase.DrawPhase) return null;
            if (Wall.IsEmpty)
            {
                ResolveExhaustiveDraw();
                return null;
            }

            var tile = Wall.DrawTile();
            Players[CurrentPlayerIndex].Hand.AddTile(tile);
            Players[CurrentPlayerIndex].Furiten.OnDraw();
            Players[CurrentPlayerIndex].Hand.Sort();
            Players[CurrentPlayerIndex].WallDrawCount++;

            Phase = TurnPhase.ActionPhase;
            OnTileDrawn?.Invoke(CurrentPlayerIndex);
            return tile;
        }

        /// <summary>
		/// Pass on all claims — advance to the next player's draw.
		/// Call this when no one wants to claim the pending discard.
		/// Do NOT call for a chankan window — call <see cref="ResolveChankan"/> instead.
		/// </summary>
		public void PassAllClaims()
		{
			if (Phase != TurnPhase.ClaimWindow) return;
			if (IsChankanWindow) return;  // caller should use ResolveChankan() for chankan

			// Now that everyone has passed, record missed Ron opportunities.
			// This is intentionally here rather than in Discard() — furiten only
			// applies when the player PASSES on a winning tile, not the moment the
			// tile appears. Calling RecordMissedDiscard in Discard() sets furiten
			// before the claim window opens, permanently blocking ClaimRon.
			var missed   = PendingDiscard!;
			int discarder = DiscarderIndex;
			for (int i = 0; i < 4; i++)
			{
				if (i == discarder) continue;
				var otherHand = Players[i].Hand;
				bool isWait   = otherHand.IsTenpai() && otherHand.IsWaitingFor(missed);
				Players[i].Furiten.RecordMissedDiscard(missed, isWait, Players[i].DeclaredRiichi);
			}

			PendingDiscard = null;
			CurrentPlayerIndex = (discarder + 1) % 4;
			TurnNumber++;
			Phase = TurnPhase.DrawPhase;

			// Suufonren da: all 4 players discarded the same wind as their FIRST tile,
			// with no calls interrupting the round.
			if (_firstRoundUninterrupted && Players.All(p => p.Discards.Count == 1))
			{
				var d0 = Players[0].Discards[0];
				if (d0.Suit == TileSuit.Wind && Players.All(p => p.Discards[0] == d0))
				{
					Phase = TurnPhase.HandEnd;
					OnHandEnd?.Invoke(HandEndReason.AbortiveDraw, Array.Empty<int>());
					return;
				}
			}
		}

		// =====================================================================
		// Resolution helpers
		// =====================================================================

		private void ResolveRon(int winner, int discarder, YakuCheckResult yakuResult, WinCheckResult winCheck)
		{
			var decomp  = winCheck.Decompositions[0];
			var ctx     = BuildContext(winner, WinMethod.Ron);
			var score   = ScoreCalculator.Calculate(decomp, yakuResult, ctx, Counters, RiichiBetsOnTable);

			// Store for UI scoring display
			LastScoreResult   = score;
			LastYakuResult    = yakuResult;
			LastWinContext    = ctx;
			LastWinnerSeat    = winner;
			LastDiscarderSeat = discarder;

			// Transfer points
			Players[discarder].Points -= score.RonPayment + score.CounterBonus;
			Players[winner].Points    += score.RonPayment + score.CounterBonus + score.RiichiBetsWon;
			RiichiBetsOnTable          = 0;

			// Counter management
			bool dealerWon = winner == DealerIndex;
			if (dealerWon) Counters++;
			else           { Counters = 0; AdvanceDealer(); }

			Phase = TurnPhase.HandEnd;
			OnHandEnd?.Invoke(HandEndReason.Ron, new[] { winner });
		}

		/// <summary>
		/// Two players win by ron on the same discard. Each receives full payment from
		/// the discarder. Riichi bets go to the first winner (closest in seating order).
		/// </summary>
		private void ResolveDoubleRon(
			List<(int winner, YakuCheckResult yaku, WinCheckResult winCheck)> winners,
			WinMethod winMethod)
		{
			int discarder = DiscarderIndex;

			// Calculate both scores BEFORE modifying state
			var scores = new List<(int winner, YakuCheckResult yaku, YakuContext ctx, ScoreResult score)>();
			for (int i = 0; i < winners.Count; i++)
			{
				var (seat, yaku, wc) = winners[i];
				var ctx   = BuildContext(seat, winMethod);
				int bets  = i == 0 ? RiichiBetsOnTable : 0;  // first winner takes riichi bets
				var score = ScoreCalculator.Calculate(wc.Decompositions[0], yaku, ctx, Counters, bets);
				scores.Add((seat, yaku, ctx, score));
			}

			// Apply payments
			foreach (var (seat, _, _, score) in scores)
			{
				Players[discarder].Points -= score.RonPayment + score.CounterBonus;
				Players[seat].Points      += score.TotalPointsWon;
			}
			RiichiBetsOnTable = 0;

			// Counter management — dealer wins if either winner is the dealer
			bool dealerWon = winners.Any(w => w.winner == DealerIndex);
			if (dealerWon) Counters++;
			else           { Counters = 0; AdvanceDealer(); }

			// Store first winner's data for the scoring panel
			var (firstSeat, firstYaku, firstCtx, firstScore) = scores[0];
			LastScoreResult   = firstScore;
			LastYakuResult    = firstYaku;
			LastWinContext    = firstCtx;
			LastWinnerSeat    = firstSeat;
			LastDiscarderSeat = discarder;

			Phase = TurnPhase.HandEnd;
			OnHandEnd?.Invoke(HandEndReason.Ron, winners.Select(w => w.winner).ToArray());
		}

		private void ResolveTsumo(int winner, YakuCheckResult yakuResult, WinCheckResult winCheck)
		{
			var decomp = winCheck.Decompositions[0];
			var ctx    = BuildContext(winner, WinMethod.Tsumo);
			var score  = ScoreCalculator.Calculate(decomp, yakuResult, ctx, Counters, RiichiBetsOnTable);

			// Store for UI scoring display
			LastScoreResult   = score;
			LastYakuResult    = yakuResult;
			LastWinContext    = ctx;
			LastWinnerSeat    = winner;
			LastDiscarderSeat = -1;   // Tsumo has no discarder

			bool dealerWon = winner == DealerIndex;
			for (int i = 0; i < 4; i++)
			{
				if (i == winner) continue;
				int pay = i == DealerIndex
					? score.TsumoPaymentEast
					: score.TsumoPaymentOther;
				Players[i].Points      -= pay;
				Players[winner].Points += pay;
			}
			Players[winner].Points += score.RiichiBetsWon + score.CounterBonus;
			RiichiBetsOnTable       = 0;

			if (dealerWon) Counters++;
			else           { Counters = 0; AdvanceDealer(); }

			Phase = TurnPhase.HandEnd;
			OnHandEnd?.Invoke(HandEndReason.Tsumo, new[] { winner });
		}

		private void ResolveExhaustiveDraw()
		{
			// ----------------------------------------------------------------
			// Nagashi Mangan — all discards terminals/honours, no one claimed
			// ----------------------------------------------------------------
			var nagashiWinners = new List<int>();
			for (int i = 0; i < 4; i++)
			{
				var p = Players[i];
				if (p.Discards.Count > 0
				    && !p.HasDiscardBeenClaimed
				    && p.Discards.All(t => t.IsTerminalOrHonour))
					nagashiWinners.Add(i);
			}

			if (nagashiWinners.Count > 0)
			{
				// Apply mangan-tsumo payments for each nagashi winner.
				// Riichi bets remain on the table (not awarded).
				foreach (int winner in nagashiWinners)
				{
					bool isDealer = winner == DealerIndex;
					if (isDealer)
					{
						// Dealer nagashi: each other player pays 4 000
						for (int s = 0; s < 4; s++)
							if (s != winner) { Players[winner].Points += 4000; Players[s].Points -= 4000; }
					}
					else
					{
						// Non-dealer nagashi: dealer pays 4 000, others pay 2 000
						Players[DealerIndex].Points -= 4000;
						Players[winner].Points      += 4000;
						for (int s = 0; s < 4; s++)
							if (s != winner && s != DealerIndex)
								{ Players[winner].Points += 2000; Players[s].Points -= 2000; }
					}
				}

				// Dealer stays if they won nagashi; otherwise rotate
				if (nagashiWinners.Contains(DealerIndex)) Counters++;
				else AdvanceDealer();

				Phase = TurnPhase.HandEnd;
				OnHandEnd?.Invoke(HandEndReason.NagashiMangan, nagashiWinners.ToArray());
				return;
			}

			// ----------------------------------------------------------------
			// Normal tenpai / noten payment
			// ----------------------------------------------------------------
			// Determine tenpai/noten for each player
			var tenpaiPlayers = new List<int>();
			var notenPlayers  = new List<int>();

			for (int i = 0; i < 4; i++)
			{
				if (Players[i].Hand.IsTenpai())
					tenpaiPlayers.Add(i);
				else
					notenPlayers.Add(i);
			}

			// Tenpai penalty exchange
			if (tenpaiPlayers.Count > 0 && notenPlayers.Count > 0)
			{
				int totalPot = 3000;  // Always 3,000 points exchanged total
				int perTenpai = totalPot / tenpaiPlayers.Count;
				int perNoten  = totalPot / notenPlayers.Count;

				foreach (int i in tenpaiPlayers) Players[i].Points += perTenpai;
				foreach (int i in notenPlayers)  Players[i].Points -= perNoten;
			}

			// Counter placed if dealer is tenpai; riichi bets stay on table
			if (tenpaiPlayers.Contains(DealerIndex))
				Counters++;
			// Else: dealer rotates (handled in AdvanceDealer)

			// Dealer stays if they were tenpai
			if (!tenpaiPlayers.Contains(DealerIndex))
				AdvanceDealer();

			Phase = TurnPhase.HandEnd;
			OnHandEnd?.Invoke(HandEndReason.ExhaustiveDraw, tenpaiPlayers.ToArray());
		}

		// =====================================================================
		// Hand transition helpers
		// =====================================================================

		/// <summary>Human-readable reason the game ended (set just before OnGameOver fires).</summary>
		public string GameOverReason { get; private set; } = "";

		public void BeginNextHand()
		{
			// Tobi rule: a player with negative points ends the game immediately.
			// Check this BEFORE the standard round-completion check.
			var bankruptPlayer = Players.FirstOrDefault(p => p.Points < 0);
			if (bankruptPlayer != null)
			{
				GameOverReason = $"{bankruptPlayer.Name} went bankrupt!";
				Phase = TurnPhase.GameOver;
				OnGameOver?.Invoke();
				return;
			}

			if (IsGameOver())
			{
				GameOverReason = "The game has ended.";
				Phase = TurnPhase.GameOver;
				OnGameOver?.Invoke();
				return;
			}
			StartNewHand();
		}

		private void AdvanceDealer()
		{
			DealerIndex = (DealerIndex + 1) % 4;
			Counters    = 0;

			// When dealer wraps past North back to the start, advance the round wind
			if (DealerIndex == 0)
			{
				RoundWind = RoundWind == WindDirection.East
					? WindDirection.South
					: WindDirection.West;  // West round = game-ending overtime
			}
		}

		private bool IsGameOver()
		{
			// Standard game ends after South round completes (dealer returns to original East)
			return RoundWind == WindDirection.West && DealerIndex == 0;
		}

		// =====================================================================
		// Context and evaluation helpers
		// =====================================================================

		private YakuContext BuildContext(int playerIndex, WinMethod winMethod)
		{
			var player = Players[playerIndex];
			var seatWind = player.GetSeatWind(DealerIndex);

			return new YakuContext
			{
				WinMethod       = winMethod,
				SeatWind        = seatWind,
				RoundWind       = RoundWind,
				IsDealer        = playerIndex == DealerIndex,
				IsRiichi                = player.DeclaredRiichi,
				IsDoubleRiichi          = player.Hand.IsDoubleRiichi,
				IsIppatsu               = player.Hand.IsIppatsu,
				WinnerWallDrawCount     = player.WallDrawCount,
				FirstRoundUninterrupted = _firstRoundUninterrupted,
				WinningTile             = player.Hand.DrawnTile,
				OpenMelds       = player.Hand.OpenMelds.ToList(),
				DoraCount       = CountDora(player, Wall.GetActiveDoraTiles()),
				UraDoraCount    = player.DeclaredRiichi ? CountDora(player, Wall.GetUradDoraTiles()) : 0,
				KanDoraCount    = 0,  // Included in DoraCount already via GetActiveDoraTiles
			};
		}

		private YakuCheckResult EvaluateBestDecomposition(
			int playerIndex, WinCheckResult winCheck, WinMethod winMethod)
		{
			var ctx  = BuildContext(playerIndex, winMethod);
			YakuCheckResult? best = null;

			foreach (var decomp in winCheck.Decompositions)
			{
				var result = YakuChecker.Evaluate(decomp, ctx);
				if (!result.HasYaku) continue;
				if (best == null || result.YakuFan > best.YakuFan)
					best = result;
			}

			return best ?? new YakuCheckResult();
		}

		private static int CountDora(PlayerState player, List<Tile> doraTiles)
		{
			int count = 0;
			foreach (var dora in doraTiles)
			{
				count += player.Hand.ClosedTiles.Count(t => t == dora);
				count += player.Hand.OpenMelds.Sum(m => m.Tiles.Count(t => t == dora));
			}
			return count;
		}

		private WinMethod GetTsumoMethod(int playerIndex)
		{
			// Rinshan Kaihou — drew from dead wall after a kan
			if (IsRinshanDraw) return WinMethod.Rinshan;
			// Haitei Raoyue — last tile drawn from the live wall
			if (Wall.TilesRemaining == 0) return WinMethod.Haitei;
			return WinMethod.Tsumo;
		}

		// =====================================================================
		// Utility
		// =====================================================================

		private static ClaimSource GetClaimSource(int discarderIndex, int claimerIndex)
		{
			int diff = (claimerIndex - discarderIndex + 4) % 4;
			return diff switch
			{
				1 => ClaimSource.Left,     // Claimer is 1 seat after discarder
				2 => ClaimSource.Opposite,
				3 => ClaimSource.Right,
				_ => ClaimSource.None,
			};
		}

		private static bool IsValidChi(Tile t1, Tile t2, Tile claimed)
		{
			if (t1.Suit != t2.Suit || t1.Suit != claimed.Suit) return false;
			if (t1.IsHonour) return false;

			var vals = new[] { t1.Value, t2.Value, claimed.Value };
			Array.Sort(vals);
			return vals[1] == vals[0] + 1 && vals[2] == vals[1] + 1;
		}

		/// <summary>
		/// Compute and store kuikae-forbidden tiles after a successful chi.
		/// Always forbidden: the claimed tile itself.
		/// Also forbidden when the claimed tile is at one end of the sequence:
		///   the tile at the opposite ryanmen end (if in range 1–9).
		/// Kanchan (middle-tile) calls only forbid the claimed tile itself.
		/// </summary>
		private void SetKuikaeForbidden(Tile t1, Tile t2, Tile claimed)
		{
			_kuikaeForbidden.Clear();
			_kuikaeForbidden.Add((claimed.Suit, claimed.Value));

			// Determine the low and high values of the three-tile sequence
			int lo = Math.Min(Math.Min(t1.Value, t2.Value), claimed.Value);
			int hi = Math.Max(Math.Max(t1.Value, t2.Value), claimed.Value);

			if (claimed.Value == lo)
			{
				// Claimed is the low end (e.g. claimed 3, hand has 4-5).
				// The hand tiles form a ryanmen that also waits on hi+1.
				if (hi + 1 <= 9) _kuikaeForbidden.Add((claimed.Suit, hi + 1));
			}
			else if (claimed.Value == hi)
			{
				// Claimed is the high end (e.g. claimed 5, hand has 3-4).
				// The hand tiles form a ryanmen that also waits on lo-1.
				if (lo - 1 >= 1) _kuikaeForbidden.Add((claimed.Suit, lo - 1));
			}
			// claimed.Value == mid (kanchan): no ryanmen extension, only claimed tile is forbidden.
		}

		private void BreakAllIppatsu()
		{
			foreach (var p in Players)
				p.Hand.BreakIppatsu();
		}

		// ---- Abortive draw declarations -------------------------------------

		/// <summary>
		/// Returns true when the current player is eligible to declare Kyuushu Kyuuhai
		/// (nine different terminal/honour types in their opening hand).
		/// Valid only before their first discard, with no calls made this hand.
		/// </summary>
		public bool CanDeclareKyuushu(int playerIndex)
		{
			if (Phase != TurnPhase.ActionPhase) return false;
			if (playerIndex != CurrentPlayerIndex) return false;
			if (!_firstRoundUninterrupted) return false;
			var player = Players[playerIndex];
			if (player.Discards.Count > 0) return false;  // must be before their first discard
			int distinctCount = player.Hand.ClosedTiles
				.Where(t => t.IsTerminalOrHonour)
				.Select(t => (t.Suit, t.Value))
				.Distinct()
				.Count();
			return distinctCount >= 9;
		}

		/// <summary>
		/// Player declares Kyuushu Kyuuhai — optional abortive draw.
		/// </summary>
		public bool DeclareKyuushuKyuuhai(int playerIndex)
		{
			if (!CanDeclareKyuushu(playerIndex)) return false;
			Phase = TurnPhase.HandEnd;
			OnHandEnd?.Invoke(HandEndReason.AbortiveDraw, Array.Empty<int>());
			return true;
		}

		// ---- Public accessors -----------------------------------------------

		public PlayerState CurrentPlayer => Players[CurrentPlayerIndex];
		public PlayerState HumanPlayer   => Players[HumanSeat];
		public bool        IsHumanTurn   => CurrentPlayerIndex == HumanSeat && Phase == TurnPhase.ActionPhase;

		/// <summary>
		/// Quick check: can this player currently win by tsumo?
		/// Used by the UI to decide whether to show the Tsumo button.
		/// </summary>
		public bool WinChecker_CanWinTsumo(int playerIndex)
		{
			if (Phase != TurnPhase.ActionPhase) return false;
			if (playerIndex != CurrentPlayerIndex) return false;
			var player   = Players[playerIndex];
			var winCheck = WinChecker.Check(
				player.Hand.ClosedTiles.ToList(),
				player.Hand.OpenMelds.ToList());
			if (!winCheck.IsWin) return false;
			var ctx = BuildContext(playerIndex, GetTsumoMethod(playerIndex));
			return winCheck.Decompositions.Any(d => YakuChecker.Evaluate(d, ctx).HasYaku);
		}
	}
}
