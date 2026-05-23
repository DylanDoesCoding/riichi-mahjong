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

			// Reset player hand state
			foreach (var p in Players)
			{
				p.Hand.Reset();  // We'll add a Reset() to Hand
                p.Furiten.Reset();
                p.Discards.Clear();
                p.DeclaredRiichi = false;
                p.RiichiBetTurn  = -1;
                p.IsTenpaiAtDraw = false;
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

            var player = Players[playerIndex];
            if (!player.Hand.RemoveTile(tile)) return false;

            // Record discard
            player.Discards.Add(tile);
            PendingDiscard = tile;
            DiscarderIndex = playerIndex;

			// Record in the discarding player's own furiten tracker.
			// Checks whether this discard matches any of the player's own historical discards
			// that they're currently waiting for (permanent furiten rule).
            player.Furiten.RecordOwnDiscardFast(tile, t => player.Hand.IsWaitingFor(t));

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

			// Clear ippatsu for all riichi players (a call breaks it)
			BreakAllIppatsu();

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

            // Check kuikae (swap-calling) prohibition
            if (IsKuikae(t1, t2, PendingDiscard)) return false;

            var player = Players[claimingPlayerIndex];
            player.Hand.ApplyChi(t1, t2, PendingDiscard, ClaimSource.Left);

            BreakAllIppatsu();

            var meld = player.Hand.OpenMelds.Last();
            OnMeldDeclared?.Invoke(claimingPlayerIndex, meld);

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

            // Build context and evaluate
            var bestResult = EvaluateBestDecomposition(claimingPlayerIndex, winCheck, WinMethod.Ron);
            if (!bestResult.HasYaku)
            {
                player.Hand.RemoveTile(PendingDiscard);
                return false;
            }

            ResolveRon(claimingPlayerIndex, DiscarderIndex, bestResult, winCheck);
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
            if (player.Hand.IsRiichi) return false;  // Simplified: no riichi-ankan for now
            if (player.Hand.ClosedTiles.Count(t => t == tile) < 4) return false;

            player.Hand.ApplyKanClosed(tile);

            // Draw rinshan tile and reveal new dora
            var rinshan = Wall.DrawKanReplacement();
            player.Hand.AddTile(rinshan);
            player.Hand.Sort();

            // Fire meld event (Phase/CurrentPlayerIndex unchanged — stay in ActionPhase)
            var meld = player.Hand.OpenMelds.Last();
            OnMeldDeclared?.Invoke(playerIndex, meld);
            OnTileDrawn?.Invoke(playerIndex);
            return true;
        }

        /// <summary>
        /// Declare an extended Kan (Kakan): player adds the 4th tile to an existing open Pon.
        /// Draws a rinshan replacement from the dead wall, reveals a new dora.
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

            BreakAllIppatsu();

            var rinshan = Wall.DrawKanReplacement();
            player.Hand.AddTile(rinshan);
            player.Hand.Sort();

            var meld = player.Hand.OpenMelds.Last();
            OnMeldDeclared?.Invoke(playerIndex, meld);
            OnTileDrawn?.Invoke(playerIndex);
            return true;
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

            BreakAllIppatsu();

            var rinshan = Wall.DrawKanReplacement();
            player.Hand.AddTile(rinshan);
            player.Hand.Sort();

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

            Phase = TurnPhase.ActionPhase;
            OnTileDrawn?.Invoke(CurrentPlayerIndex);
            return tile;
        }

        /// <summary>
		/// Pass on all claims — advance to the next player's draw.
		/// Call this when no one wants to claim the pending discard.
		/// </summary>
		public void PassAllClaims()
		{
			if (Phase != TurnPhase.ClaimWindow) return;

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
				IsRiichi        = player.DeclaredRiichi,
				IsDoubleRiichi  = player.Hand.IsDoubleRiichi,
				IsIppatsu       = player.Hand.IsIppatsu,
				IsFirstRoundWin = TurnNumber == 0,
				WinningTile     = player.Hand.DrawnTile,
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
			// Check if this is a rinshan draw (after kan) or haitei (last tile)
			// These flags would be set on the player or wall state
			// For now return standard Tsumo; game controller will set special cases
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

		private static bool IsKuikae(Tile t1, Tile t2, Tile claimed)
		{
			// Swap-calling: cannot claim for chi and immediately discard either end of the chi
			// (The actual discard check happens in the Discard() call — this is a placeholder)
			return false;
		}

		private void BreakAllIppatsu()
		{
			foreach (var p in Players)
				p.Hand.BreakIppatsu();
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
			var ctx = BuildContext(playerIndex, WinMethod.Tsumo);
			return winCheck.Decompositions.Any(d => YakuChecker.Evaluate(d, ctx).HasYaku);
		}
	}
}
