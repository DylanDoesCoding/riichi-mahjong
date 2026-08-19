// =============================================================================
// ResultsScreen.cs
// The end-of-game screen. New in pass 09 — the client had nowhere to show how a
// game finished beyond a list of four totals.
//
// 1920 x 1080, two columns: a 1000px placement list and a flexible right rail,
// 40px apart.
//
// Two decisions carry most of the design:
//
//   - Nothing else on the screen is gold, so first place reads as first without
//     a legend or a label saying so.
//   - Placement points always show their arithmetic. A player who has just lost
//     46.5 points should be able to see where the number came from rather than
//     having to trust the client.
// =============================================================================

using Godot;
using System.Collections.Generic;
using System.Linq;
using RiichiMahjong.Core;

namespace RiichiMahjong.UI
{
    /// <summary>
    /// Carries the finished game across the scene change. A scene swap drops the old
    /// tree, so the record is handed over here rather than through a node.
    /// </summary>
    public static class MatchResultsHandoff
    {
        public static MatchRecord?      Record { get; set; }
        public static List<SeatResult>? Results { get; set; }

        /// <summary>Seat the local player occupied, so their row can be marked.</summary>
        public static int LocalSeat { get; set; } = -1;

        public static bool HasResults => Record != null && Results is { Count: > 0 };

        public static void Clear()
        {
            Record  = null;
            Results = null;
            LocalSeat = -1;
        }
    }

    public partial class ResultsScreen : Control
    {
        private const int PagePaddingX  = 96;
        private const int PagePaddingY  = 72;
        private const int ColumnGap     = 40;
        private const int ListWidth     = 1000;

        private const int ChartHeight   = 270;

        // Rank numeral tints, second through fourth, descending in prominence.
        private static readonly Color[] RankTints =
        {
            DoloTokens.DoraGold,   // first — the only gold on the screen
            DoloTokens.BodyText,
            DoloTokens.DimText,
            DoloTokens.MonoDim,
        };

        // Brass inset alpha for places two, three and four.
        private static readonly float[] RowEdgeAlpha = { 0.28f, 0.18f, 0.14f };

        private VBoxContainer _handLogRows = null!;

        public override void _Ready()
        {
            DoloTheme.Apply(this);
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

            var background = new ColorRect { Color = DoloTokens.Page };
            background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            AddChild(background);

            if (!MatchResultsHandoff.HasResults)
            {
                BuildEmptyState();
                return;
            }

            BuildLayout(MatchResultsHandoff.Results!, MatchResultsHandoff.Record!);
        }

        /// <summary>
        /// Shown if the screen is reached without a finished game behind it — from the
        /// menu, say. Better than four blank rows implying a game that never happened.
        /// </summary>
        private void BuildEmptyState()
        {
            var label = new Label
            {
                Text                = "No finished game to show yet.",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            label.ThemeTypeVariation = DoloTheme.SectionHead;
            label.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            AddChild(label);

            var menu = DoloWidgets.IconButton(DoloIcon.Back, "Menu", DoloTheme.ButtonGhost, 60);
            menu.CustomMinimumSize = new Vector2(240, 60);
            menu.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom);
            menu.OffsetLeft   = -120;
            menu.OffsetRight  =  120;
            menu.OffsetTop    = -160;
            menu.OffsetBottom = -100;
            menu.Pressed += GoToMenu;
            AddChild(menu);
        }

        // =====================================================================
        // Layout
        // =====================================================================

        private void BuildLayout(List<SeatResult> results, MatchRecord record)
        {
            var page = new HBoxContainer();
            page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            page.OffsetLeft   =  PagePaddingX;
            page.OffsetRight  = -PagePaddingX;
            page.OffsetTop    =  PagePaddingY;
            page.OffsetBottom = -PagePaddingY;
            page.AddThemeConstantOverride("separation", ColumnGap);

            page.AddChild(BuildPlacementColumn(results));
            page.AddChild(BuildRightRail(results, record));

            AddChild(page);
        }

        private Control BuildPlacementColumn(List<SeatResult> results)
        {
            var column = new VBoxContainer
            {
                CustomMinimumSize = new Vector2(ListWidth, 0),
            };
            column.AddThemeConstantOverride("separation", 12);

            var title = new Label { Text = "RESULTS" };
            title.ThemeTypeVariation = DoloTheme.ScreenTitle;
            column.AddChild(title);

            var subtitle = new Label
            {
                Text = FormatRuleLine(MatchResultsHandoff.Record!.Rules),
            };
            subtitle.ThemeTypeVariation = DoloTheme.Mono;
            column.AddChild(subtitle);

            column.AddChild(DoloStyles.HairlineRow(0.28f));

            for (int place = 0; place < results.Count; place++)
                column.AddChild(BuildPlacementRow(results[place], place));

            return column;
        }

        /// <summary>The ruleset, stated rather than assumed, since it drives every figure.</summary>
        private static string FormatRuleLine(MatchRules rules)
        {
            string uma = string.Join(" / ", rules.Uma.Select(u => $"{u:0.#}"));
            return $"{rules.StartingPoints:N0} START   ·   {rules.ReturnPoints:N0} RETURN   "
                 + $"·   UMA {uma}   ·   OKA +{rules.Oka:0.#}";
        }

        /// <summary>
        /// One player's row. First place is taller, ringed in brass and gradient-filled;
        /// the rest step down in weight so rank is legible from the shape of the list
        /// before any number is read.
        /// </summary>
        private Control BuildPlacementRow(SeatResult result, int place)
        {
            bool isFirst = place == 0;

            // First place is emphasised by a taller fixed height, the brass ring and the
            // gradient — not by ExpandFill, which let it swallow all the column's leftover
            // vertical space and balloon to ~430px against its 150px minimum.
            var frame = new PanelContainer
            {
                CustomMinimumSize = new Vector2(0, isFirst ? 168 : 112),
                SizeFlagsVertical = SizeFlags.Fill,
            };

            var style = isFirst
                ? DoloStyles.Flat(new Color("#1d1610"), DoloTokens.RadiusCard,
                                  DoloTokens.Brass, borderWidth: 2)
                : DoloStyles.Flat(new Color("#1a140e"), DoloTokens.RadiusCard,
                                  DoloTokens.Hairline(RowEdgeAlpha[place - 1]), borderWidth: 1);
            style.ContentMarginLeft   = 28;
            style.ContentMarginRight  = 28;
            style.ContentMarginTop    = isFirst ? 20 : 14;
            style.ContentMarginBottom = isFirst ? 20 : 14;
            frame.AddThemeStyleboxOverride("panel", style);

            // First place gets the warm gradient the design calls for, which a
            // StyleBoxFlat cannot draw, so it goes behind the content.
            if (isFirst)
            {
                var gradient = DoloStyles.GradientRect(new Color("#2a2113"), new Color("#1d1610"));
                frame.AddChild(gradient);
            }

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 24);

            row.AddChild(BuildRankNumeral(result, place, isFirst));
            row.AddChild(BuildIdentityBlock(result, isFirst));
            row.AddChild(BuildScoreBlock(result, isFirst));
            row.AddChild(BuildVerticalHairline());
            row.AddChild(BuildPlacementPointsBlock(result, isFirst));

            frame.AddChild(row);
            return frame;
        }

        private static Control BuildRankNumeral(SeatResult result, int place, bool isFirst)
        {
            var numeral = new Label
            {
                Text                = result.Placement.ToString(),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                CustomMinimumSize   = new Vector2(isFirst ? 90 : 70, 0),
            };
            numeral.ThemeTypeVariation = DoloTheme.MonoLarge;
            numeral.AddThemeFontSizeOverride("font_size",
                isFirst ? DoloTokens.SizeRankFirst : DoloTokens.SizeRankOther);
            numeral.AddThemeColorOverride("font_color", RankTints[place]);
            return numeral;
        }

        private static Control BuildIdentityBlock(SeatResult result, bool isFirst)
        {
            var column = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical   = SizeFlags.ShrinkCenter,
            };
            column.AddThemeConstantOverride("separation", 6);

            var nameRow = new HBoxContainer();
            nameRow.AddThemeConstantOverride("separation", 12);

            var name = new Label { Text = result.Name, VerticalAlignment = VerticalAlignment.Center };
            name.ThemeTypeVariation = isFirst ? DoloTheme.NameLarge : DoloTheme.NameSmall;
            nameRow.AddChild(name);

            nameRow.AddChild(BuildWindChip(result.Seat));

            if (result.Seat == MatchResultsHandoff.LocalSeat)
            {
                var you = new Label { Text = "YOU", VerticalAlignment = VerticalAlignment.Center };
                you.ThemeTypeVariation = DoloTheme.Mono;
                nameRow.AddChild(you);
            }

            column.AddChild(nameRow);

            var record = new Label { Text = result.RecordLine };
            record.ThemeTypeVariation = DoloTheme.Body;
            column.AddChild(record);

            return column;
        }

        /// <summary>The seat wind as a small chip, matching the table's plates.</summary>
        private static Control BuildWindChip(int seat)
        {
            string[] winds = { "EAST", "SOUTH", "WEST", "NORTH" };

            var chip = new PanelContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
            var style = DoloStyles.Flat(DoloTokens.InsetPane, DoloTokens.RadiusInset,
                                        DoloTokens.Hairline(0.22f), borderWidth: 1);
            style.ContentMarginLeft   = 8;
            style.ContentMarginRight  = 8;
            style.ContentMarginTop    = 3;
            style.ContentMarginBottom = 3;
            chip.AddThemeStyleboxOverride("panel", style);

            var label = new Label { Text = winds[seat % 4] };
            label.ThemeTypeVariation = DoloTheme.MonoSmall;
            chip.AddChild(label);
            return chip;
        }

        private static Control BuildScoreBlock(SeatResult result, bool isFirst)
        {
            var column = new VBoxContainer
            {
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                CustomMinimumSize = new Vector2(170, 0),
            };
            column.AddThemeConstantOverride("separation", 2);

            var score = new Label
            {
                Text                = $"{result.FinalScore:N0}",
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            score.ThemeTypeVariation = DoloTheme.MonoLarge;
            score.AddThemeFontSizeOverride("font_size",
                isFirst ? DoloTokens.SizeScoreFirst : DoloTokens.SizeScoreOther);
            column.AddChild(score);

            var delta = new Label
            {
                Text                = result.Delta >= 0 ? $"+{result.Delta:N0}" : $"{result.Delta:N0}",
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            delta.ThemeTypeVariation = DoloTheme.Mono;
            delta.AddThemeColorOverride("font_color",
                result.Delta >= 0 ? DoloTokens.Positive : DoloTokens.Negative);
            column.AddChild(delta);

            return column;
        }

        private static Control BuildVerticalHairline()
        {
            var line = new ColorRect
            {
                Color             = DoloTokens.Hairline(0.18f),
                CustomMinimumSize = new Vector2(1, 0),
                SizeFlagsVertical = SizeFlags.ExpandFill,
            };
            return line;
        }

        private static Control BuildPlacementPointsBlock(SeatResult result, bool isFirst)
        {
            var column = new VBoxContainer
            {
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                CustomMinimumSize = new Vector2(230, 0),
            };
            column.AddThemeConstantOverride("separation", 2);

            bool gained = result.PlacementPoints >= 0;

            var points = new Label
            {
                Text = gained ? $"+{result.PlacementPoints:0.#}" : $"{result.PlacementPoints:0.#}",
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            points.ThemeTypeVariation = DoloTheme.MonoLarge;
            points.AddThemeFontSizeOverride("font_size",
                isFirst ? DoloTokens.SizePointsFirst : DoloTokens.SizePointsOther);
            points.AddThemeColorOverride("font_color",
                !gained     ? DoloTokens.Negative
                : isFirst   ? DoloTokens.DoraGold
                            : DoloTokens.GainText);
            column.AddChild(points);

            // Always show the sum, never just the answer.
            var arithmetic = new Label
            {
                Text                = result.Arithmetic,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            arithmetic.ThemeTypeVariation = DoloTheme.MonoSmall;
            column.AddChild(arithmetic);

            return column;
        }

        // =====================================================================
        // Right rail
        // =====================================================================

        private Control BuildRightRail(List<SeatResult> results, MatchRecord record)
        {
            var rail = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            rail.AddThemeConstantOverride("separation", 16);

            rail.AddChild(BuildChartCard(record));

            var rematch = DoloWidgets.IconButton(DoloIcon.Play, "Rematch",
                                                 DoloTheme.ButtonPrimary, height: 72);
            rematch.Pressed += OnRematch;
            rail.AddChild(rematch);

            var reviewRow = new HBoxContainer();
            reviewRow.AddThemeConstantOverride("separation", 12);

            var review = DoloWidgets.IconButton(DoloIcon.Tile, "Review hands",
                                                DoloTheme.ButtonSecondary, height: 60);
            review.Pressed += OnReviewHands;

            var menu = DoloWidgets.IconButton(DoloIcon.Back, "Menu",
                                              DoloTheme.ButtonGhost, height: 60);
            menu.Pressed += GoToMenu;

            reviewRow.AddChild(review);
            reviewRow.AddChild(menu);
            rail.AddChild(reviewRow);

            return rail;
        }

        /// <summary>The chart, with the hand log pinned to its bottom under a hairline.</summary>
        private Control BuildChartCard(MatchRecord record)
        {
            var card = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            card.AddThemeStyleboxOverride("panel", DoloStyles.Card(24));

            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 12);

            var title = new Label { Text = "POINTS BY HAND" };
            title.ThemeTypeVariation = DoloTheme.Mono;
            column.AddChild(title);

            // Width fills the flexible rail rather than a fixed minimum: at 1920 the list
            // (1000) plus gaps leaves the rail ~688px, so a hard 760 min pushed the rail
            // past the viewport and clipped the Rematch / Menu buttons. The chart redraws
            // on resize, so filling is safe.
            var chart = new PointsChart
            {
                CustomMinimumSize = new Vector2(0, ChartHeight),
            };
            column.AddChild(chart);

            // Names in seat order, since the chart indexes its series by seat.
            var namesBySeat = new string[4];
            foreach (var result in MatchResultsHandoff.Results!)
                namesBySeat[result.Seat] = result.Name;

            chart.SetData(record.Trajectory, namesBySeat,
                          record.Hands.Select(h => h.Label.Replace("East ", "E")
                                                          .Replace("South ", "S")
                                                          .Replace("West ", "W")
                                                          .Replace("North ", "N")).ToArray());

            column.AddChild(DoloStyles.HairlineRow(0.18f));

            var scroll = new ScrollContainer
            {
                SizeFlagsVertical    = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };

            _handLogRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _handLogRows.AddThemeConstantOverride("separation", 0);
            FillHandLog(record);

            scroll.AddChild(_handLogRows);
            column.AddChild(scroll);

            card.AddChild(column);
            return card;
        }

        private void FillHandLog(MatchRecord record)
        {
            foreach (var hand in record.Hands)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 12);
                row.CustomMinimumSize = new Vector2(0, 26);

                var label = new Label { Text = hand.Label + (hand.Honba > 0 ? $" · {hand.Honba}b" : "") };
                label.ThemeTypeVariation = DoloTheme.MonoSmall;
                label.CustomMinimumSize  = new Vector2(90, 0);
                row.AddChild(label);

                string outcome = hand.IsDraw
                    ? "Draw"
                    : $"{NameOf(hand.WinnerSeat)} · {hand.Han} han"
                      + (string.IsNullOrEmpty(hand.Yaku) ? "" : $" · {hand.Yaku}");

                var outcomeLabel = new Label
                {
                    Text                = outcome,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                };
                outcomeLabel.ThemeTypeVariation = DoloTheme.BodySmall;
                row.AddChild(outcomeLabel);

                _handLogRows.AddChild(row);
            }
        }

        private static string NameOf(int seat)
        {
            if (seat < 0) return "—";
            var match = MatchResultsHandoff.Results?.FirstOrDefault(r => r.Seat == seat);
            return match?.Name ?? $"Seat {seat + 1}";
        }

        // =====================================================================
        // Actions
        // =====================================================================

        /// <summary>Rematch: the same four seats, a fresh game.</summary>
        private void OnRematch()
        {
            MatchResultsHandoff.Clear();
            GetTree().ChangeSceneToFile("res://Scenes/GameTable.tscn");
        }

        /// <summary>
        /// Review hands. The log is already on this screen, so this scrolls it into view
        /// rather than opening a second surface that shows the same rows.
        /// </summary>
        private void OnReviewHands()
        {
            var scroll = _handLogRows.GetParent() as ScrollContainer;
            scroll?.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, 0);
            _handLogRows.GrabFocus();
        }

        private void GoToMenu()
        {
            MatchResultsHandoff.Clear();
            GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
        }
    }
}
