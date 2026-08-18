// =============================================================================
// HudScoring.cs
// The scoring card, in three layouts.
//
// Pass 07 puts ron, tsumo and the exhaustive draw on one 740 x 620 card. The
// difference between them is which regions exist, not which regions are empty:
// a draw has no yaku and no winning hand, so those regions are removed rather
// than left blank. That is why the ryuukyoku result now renders here instead of
// on a second card of its own - there is one scoring surface with three shapes.
//
// Payments always show their arithmetic ("8,000 + 300 honba"). A player who has
// just lost points should be able to see where the number came from rather than
// having to trust the client.
// =============================================================================

using Godot;
using System.Collections.Generic;
using RiichiMahjong.Core;

namespace RiichiMahjong.UI
{
    /// <summary>Which of the three layouts the scoring card is currently showing.</summary>
    public enum ScoringVariant
    {
        Ron,
        Tsumo,
        Draw,
    }

    public partial class HUD
    {
        private const int ScoringCardWidth  = 740;
        private const int ScoringCardHeight = 620;

        /// <summary>Display-only tiles on this card, per the design.</summary>
        private static readonly Vector2I ScoringTileSize = new(44, 60);

        private const int YakuRowHeight = 38;

        // Regions that appear in some layouts and not others.
        private Control       _scoringHandSection = null!;   // winning hand - ron/tsumo only
        private HBoxContainer _scoringHandRow     = null!;
        private Control       _scoringYakuSection = null!;   // yaku list + han/fu - ron/tsumo only
        private VBoxContainer _scoringDrawRows    = null!;   // tenpai/noten - draw only

        // The payout block, which every layout has.
        private Label _scoringPayerLabel = null!;
        private Label _scoringArithmetic = null!;

        private void BuildScoringPanel()
        {
            // Full-screen dim backdrop — added last in the HUD tree so it sits on top.
            _scoringBackdrop = new ColorRect
            {
                Color       = new Color(0f, 0f, 0f, 0.72f),
                MouseFilter = MouseFilterEnum.Stop,   // block all clicks through
                Visible     = false,
            };
            _scoringBackdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

            // A PanelContainer (not a bare Panel) so it lays the column out to fill the
            // card, insetting it by the stylebox's 28px content margins. A plain Panel is
            // not a container: the column would sit at its minimum width in the top-left
            // corner and the card would collapse into its left third.
            _scoringPanel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
            _scoringPanel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
            _scoringPanel.OffsetLeft   = -ScoringCardWidth  * 0.5f;
            _scoringPanel.OffsetTop    = -ScoringCardHeight * 0.5f;
            _scoringPanel.OffsetRight  =  ScoringCardWidth  * 0.5f;
            _scoringPanel.OffsetBottom =  ScoringCardHeight * 0.5f;
            _scoringPanel.AddThemeStyleboxOverride("panel", DoloStyles.Card(28));

            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 14);

            // ---- Title ----
            _scoringTitle = new Label();
            _scoringTitle.ThemeTypeVariation = DoloTheme.NameSmall;
            column.AddChild(_scoringTitle);
            column.AddChild(DoloStyles.HairlineRow(0.28f));

            column.AddChild(BuildWinningHandSection());
            column.AddChild(BuildYakuSection());
            column.AddChild(BuildDrawSection());
            column.AddChild(BuildPayoutSection());
            column.AddChild(BuildStandingsSection());
            column.AddChild(BuildScoringButtons());

            _scoringPanel.AddChild(column);
            _scoringBackdrop.AddChild(_scoringPanel);
            AddChild(_scoringBackdrop);
        }

        // =====================================================================
        // Regions
        // =====================================================================

        private Control BuildWinningHandSection()
        {
            var section = new VBoxContainer();
            section.AddThemeConstantOverride("separation", 8);

            _scoringHandRow = new HBoxContainer();
            _scoringHandRow.AddThemeConstantOverride("separation", 3);
            section.AddChild(_scoringHandRow);

            _scoringHandSection = section;
            return section;
        }

        private Control BuildYakuSection()
        {
            var section = new VBoxContainer();
            section.AddThemeConstantOverride("separation", 0);

            _scoringYakuRows = new VBoxContainer();
            _scoringYakuRows.AddThemeConstantOverride("separation", 0);
            section.AddChild(_scoringYakuRows);

            // Han / fu on the left, the limit name on the right.
            var summaryRow = new HBoxContainer();
            summaryRow.AddThemeConstantOverride("separation", 10);

            _scoringHanFuLabel = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _scoringHanFuLabel.ThemeTypeVariation = DoloTheme.MonoNumber;

            _scoringLimitLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right };
            _scoringLimitLabel.ThemeTypeVariation = DoloTheme.PlateName;
            _scoringLimitLabel.AddThemeColorOverride("font_color", DoloTokens.DoraGold);

            summaryRow.AddChild(_scoringHanFuLabel);
            summaryRow.AddChild(_scoringLimitLabel);

            section.AddChild(DoloStyles.HairlineRow(0.28f));
            section.AddChild(summaryRow);

            _scoringYakuSection = section;
            return section;
        }

        private Control BuildDrawSection()
        {
            _scoringDrawRows = new VBoxContainer();
            _scoringDrawRows.AddThemeConstantOverride("separation", 0);
            _scoringDrawRows.Visible = false;
            return _scoringDrawRows;
        }

        /// <summary>
        /// The payout block: who pays, the arithmetic, and the total. An inset panel so
        /// it reads as the settled result rather than as another list row.
        /// </summary>
        private Control BuildPayoutSection()
        {
            var panel = new PanelContainer();
            panel.AddThemeStyleboxOverride("panel", DoloStyles.Inset(16));

            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 8);

            _scoringPayerLabel = new Label();
            _scoringPayerLabel.ThemeTypeVariation = DoloTheme.Mono;
            column.AddChild(_scoringPayerLabel);

            _scoringPayRows = new VBoxContainer();
            _scoringPayRows.AddThemeConstantOverride("separation", 0);
            column.AddChild(_scoringPayRows);

            var totalRow = new HBoxContainer();
            totalRow.AddThemeConstantOverride("separation", 10);

            _scoringArithmetic = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _scoringArithmetic.ThemeTypeVariation = DoloTheme.Mono;
            _scoringArithmetic.VerticalAlignment  = VerticalAlignment.Center;

            _scoringTotalWon = new Label { HorizontalAlignment = HorizontalAlignment.Right };
            _scoringTotalWon.ThemeTypeVariation = DoloTheme.MonoLarge;
            _scoringTotalWon.AddThemeColorOverride("font_color", DoloTokens.DoraGold);

            totalRow.AddChild(_scoringArithmetic);
            totalRow.AddChild(_scoringTotalWon);

            column.AddChild(DoloStyles.HairlineRow(0.18f));
            column.AddChild(totalRow);

            panel.AddChild(column);
            return panel;
        }

        private Control BuildStandingsSection()
        {
            var section = new VBoxContainer();
            section.AddThemeConstantOverride("separation", 0);
            section.SizeFlagsVertical = SizeFlags.ExpandFill;

            section.AddChild(DoloWidgets.SectionHeading("Scores after this hand"));

            _scoringAllScores = new VBoxContainer();
            _scoringAllScores.AddThemeConstantOverride("separation", 0);
            section.AddChild(_scoringAllScores);

            return section;
        }

        private Control BuildScoringButtons()
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);

            _scoringNextBtn = DoloWidgets.IconButton(DoloIcon.Play, "Next Hand",
                                                     DoloTheme.ButtonPrimary);
            _scoringNextBtn.Pressed += () => EmitSignal(SignalName.ScoringNextHandPressed);

            _scoringMenuBtn = DoloWidgets.IconButton(DoloIcon.Back, "Menu",
                                                     DoloTheme.ButtonGhost);
            _scoringMenuBtn.Pressed += () => EmitSignal(SignalName.ScoringMenuPressed);

            row.AddChild(_scoringNextBtn);
            row.AddChild(_scoringMenuBtn);
            return row;
        }

        // =====================================================================
        // Layout switching
        // =====================================================================

        /// <summary>
        /// Choose which of the three layouts is on screen. A draw removes the winning
        /// hand and the yaku list rather than showing them empty.
        /// </summary>
        private void SetScoringVariant(ScoringVariant variant)
        {
            bool isWin = variant != ScoringVariant.Draw;

            _scoringHandSection.Visible = isWin;
            _scoringYakuSection.Visible = isWin;
            _scoringDrawRows.Visible    = !isWin;
        }

        /// <summary>
        /// Fill the payout block's header and its arithmetic line.
        /// <paramref name="arithmetic"/> is the sum as the player would work it out,
        /// e.g. "8,000 + 300 honba".
        /// </summary>
        private void SetPayout(string payerLine, string arithmetic, string total, Color totalTint)
        {
            _scoringPayerLabel.Text = payerLine.ToUpperInvariant();
            _scoringArithmetic.Text = arithmetic;
            _scoringTotalWon.Text   = total;
            _scoringTotalWon.AddThemeColorOverride("font_color", totalTint);
        }

        /// <summary>Show the tiles of the winning hand, display-only at 44 x 60.</summary>
        private void SetWinningHand(IReadOnlyList<Tile>? tiles, Tile? winningTile)
        {
            foreach (var child in _scoringHandRow.GetChildren()) child.QueueFree();
            if (tiles == null || tiles.Count == 0)
            {
                _scoringHandSection.Visible = false;
                return;
            }

            foreach (var tile in tiles)
            {
                var node = new TileNode();
                node.SetCellMetrics(ScoringTileSize, ScoringTileSize);
                node.SetTile(tile, faceDown: false);
                node.SetInteractive(false);

                // The tile that completed the hand is ringed, so the win is legible
                // without counting the hand back.
                if (winningTile != null && tile == winningTile)
                    node.SetDoraGlow(true);

                _scoringHandRow.AddChild(node);
            }
        }

        // =====================================================================
        // Rows
        // =====================================================================

        /// <summary>
        /// One 38px row: name on the left, value right-aligned in mono so the column of
        /// han values lines up however long the yaku names are.
        /// </summary>
        private static Control MakeScoringRow(string label, string value, Color valueColor,
                                              bool goldWedge = false)
        {
            var holder = new Control
            {
                CustomMinimumSize = new Vector2(0, YakuRowHeight),
            };
            holder.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

            var line = DoloStyles.HairlineRow(0.14f);
            line.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
            holder.AddChild(line);

            var row = new HBoxContainer();
            row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            row.AddThemeConstantOverride("separation", 12);

            var nameLabel = new Label
            {
                Text                = label.Trim(),
                VerticalAlignment   = VerticalAlignment.Center,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            nameLabel.ThemeTypeVariation = DoloTheme.Row;

            var valueLabel = new Label
            {
                Text                = value,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            valueLabel.ThemeTypeVariation = DoloTheme.MonoNumber;
            valueLabel.AddThemeColorOverride("font_color", valueColor);

            row.AddChild(nameLabel);
            row.AddChild(valueLabel);
            holder.AddChild(row);

            // Dora rows carry the same corner wedge the tiles do, so "this is dora"
            // is one mark wherever it appears.
            if (goldWedge)
            {
                var wedge = new DoraCornerWedge();
                wedge.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                holder.AddChild(wedge);
            }

            return holder;
        }

        // =====================================================================
        // Exhaustive draw
        // =====================================================================

        /// <summary>
        /// Populate and show the draw layout. All arrays are indexed by VISUAL seat
        /// (0 = bottom/self, 1 = right, 2 = top, 3 = left).
        /// <paramref name="reason"/> names the ryuukyoku variant - nine terminals, four
        /// riichi, four kan - and defaults to the ordinary exhaustive draw.
        /// </summary>
        public void ShowRyuukyokuPanel(
            string[]     names,
            int[]        currentPoints,
            int          dealerVisualSeat,
            bool[]       isTenpai,
            List<Tile>[] waitingTiles,
            int[]        pointDeltas,
            string       reason = "Exhaustive draw")
        {
            SetScoringVariant(ScoringVariant.Draw);

            _scoringTitle.Text = reason.ToUpperInvariant();
            _scoringTitle.AddThemeColorOverride("font_color", DoloTokens.Ivory);

            foreach (var child in _scoringDrawRows.GetChildren()) child.QueueFree();

            for (int seat = 0; seat < 4; seat++)
                _scoringDrawRows.AddChild(BuildDrawRow(seat, names, dealerVisualSeat,
                                                       isTenpai, waitingTiles));

            // Tenpai payments are the payout for a draw, so they fill the same block.
            int tenpaiCount = 0;
            foreach (bool t in isTenpai) if (t) tenpaiCount++;

            string arithmetic = tenpaiCount is 0 or 4
                ? "no exchange — all seats equal"
                : $"3,000 split {tenpaiCount} ways";

            int selfDelta = pointDeltas.Length > 0 ? pointDeltas[0] : 0;
            SetPayout(
                $"{tenpaiCount} tenpai · {4 - tenpaiCount} noten",
                arithmetic,
                selfDelta >= 0 ? $"+{selfDelta:N0}" : $"{selfDelta:N0}",
                selfDelta >= 0 ? DoloTokens.Positive : DoloTokens.Negative);

            FillStandings(names, currentPoints, dealerVisualSeat, winnerSeat: -1);

            _scoringNextBtn.Visible  = true;
            _scoringBackdrop.Visible = true;
        }

        private Control BuildDrawRow(int seat, string[] names, int dealerVisualSeat,
                                     bool[] isTenpai, List<Tile>[] waitingTiles)
        {
            var holder = new Control { CustomMinimumSize = new Vector2(0, YakuRowHeight) };

            var line = DoloStyles.HairlineRow(0.14f);
            line.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
            holder.AddChild(line);

            var row = new HBoxContainer();
            row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            row.AddThemeConstantOverride("separation", 12);

            var nameLabel = new Label
            {
                Text              = (seat == dealerVisualSeat ? "· " : "") + names[seat],
                VerticalAlignment = VerticalAlignment.Center,
                CustomMinimumSize = new Vector2(180, 0),
                ClipText          = true,
            };
            nameLabel.ThemeTypeVariation = DoloTheme.Row;
            row.AddChild(nameLabel);

            // TENPAI / NOTEN as the word, not as a colour.
            var badge = new Label
            {
                Text                = isTenpai[seat] ? "TENPAI" : "NOTEN",
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                CustomMinimumSize   = new Vector2(80, 0),
            };
            badge.ThemeTypeVariation = DoloTheme.Mono;
            badge.AddThemeColorOverride("font_color",
                isTenpai[seat] ? DoloTokens.Positive : DoloTokens.DimText);
            row.AddChild(badge);

            // Waits, as real tiles rather than text pills.
            var waitsRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            waitsRow.AddThemeConstantOverride("separation", 3);
            if (isTenpai[seat])
            {
                var seen = new HashSet<int>();
                foreach (var tile in waitingTiles[seat])
                {
                    if (!seen.Add(tile.TileId)) continue;
                    var node = new TileNode();
                    node.SetCellMetrics(new Vector2I(22, 30), new Vector2I(22, 30));
                    node.SetTile(tile, faceDown: false);
                    node.SetInteractive(false);
                    waitsRow.AddChild(node);
                }
            }
            row.AddChild(waitsRow);

            holder.AddChild(row);
            return holder;
        }

        /// <summary>Hide the scoring card. Kept under its old name for the call sites.</summary>
        public void HideRyuukyokuPanel() => _scoringBackdrop.Visible = false;

        // =====================================================================
        // Standings
        // =====================================================================

        /// <summary>
        /// The score table under the payout, ordered by points. Shared by all three
        /// layouts, since every hand ends with four totals whatever caused it to end.
        /// </summary>
        private void FillStandings(string[] names, int[] points, int dealerSeat, int winnerSeat)
        {
            foreach (var child in _scoringAllScores.GetChildren()) child.QueueFree();

            string[] windLetters = { "E", "S", "W", "N" };

            var order = new List<int> { 0, 1, 2, 3 };
            order.Sort((a, b) => points[b].CompareTo(points[a]));

            for (int rank = 0; rank < order.Count; rank++)
            {
                int seat = order[rank];
                string wind = windLetters[(seat - dealerSeat + 4) % 4];
                bool isWinner = seat == winnerSeat;

                var row = MakeScoringRow(
                    $"{rank + 1}   {names[seat]}   ({wind})",
                    $"{points[seat]:N0}",
                    isWinner ? DoloTokens.DoraGold : DoloTokens.Ivory);

                _scoringAllScores.AddChild(row);
            }
        }
    }
}
