// =============================================================================
// YakuReferenceOverlay.cs
// Full-screen in-game reference panel listing every implemented yaku.
//
// Usage:
//   var overlay = YakuReferenceOverlay.Create();
//   AddChild(overlay);                // adds to current scene
//   overlay.CloseRequested += () => overlay.QueueFree();
// =============================================================================

using Godot;
using System.Collections.Generic;

namespace RiichiMahjong.UI
{
    public partial class YakuReferenceOverlay : Control
    {
        public event System.Action? CloseRequested;

        // ---- Palette (matches LobbyController / HUD style) -------------------
        private static readonly Color BgDim      = new(0f, 0f, 0f, 0.82f);
        private static readonly Color CardBg     = new(0.09f, 0.11f, 0.17f, 1f);
        private static readonly Color CardBorder = new(0.28f, 0.42f, 0.68f, 1f);
        private static readonly Color Header     = new(0.60f, 0.82f, 1.00f, 1f);
        private static readonly Color TextMain   = new(0.88f, 0.92f, 1.00f, 1f);
        private static readonly Color TextDim    = new(0.55f, 0.62f, 0.76f, 1f);
        private static readonly Color TextJP     = new(0.75f, 0.88f, 0.65f, 1f);
        private static readonly Color FanGold    = new(1.00f, 0.84f, 0.30f, 1f);
        private static readonly Color YakumanClr = new(1.00f, 0.55f, 0.20f, 1f);
        private static readonly Color SectionHdr = new(0.22f, 0.36f, 0.62f, 1f);

        // =====================================================================
        // Factory
        // =====================================================================

        public static YakuReferenceOverlay Create()
        {
            var o = new YakuReferenceOverlay();
            o.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            o.MouseFilter = MouseFilterEnum.Stop;   // block clicks reaching game
            o.ZIndex = 100;
            return o;
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        public override void _Ready()
        {
            // Dim backdrop
            var backdrop = new ColorRect();
            backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            backdrop.Color = BgDim;
            AddChild(backdrop);

            // Central card
            var card = new PanelContainer();
            card.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
            card.OffsetLeft   = -460;
            card.OffsetTop    = -340;
            card.OffsetRight  =  460;
            card.OffsetBottom =  340;

            var cardStyle = new StyleBoxFlat();
            cardStyle.BgColor = CardBg;
            cardStyle.SetBorderWidthAll(2);
            cardStyle.BorderColor = CardBorder;
            cardStyle.SetCornerRadiusAll(12);
            card.AddThemeStyleboxOverride("panel", cardStyle);
            AddChild(card);

            // Layout inside card
            var root = new VBoxContainer();
            root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            root.OffsetLeft   = 20;
            root.OffsetTop    = 14;
            root.OffsetRight  = -20;
            root.OffsetBottom = -14;
            root.AddThemeConstantOverride("separation", 6);
            card.AddChild(root);

            // Title row
            var titleRow = new HBoxContainer();
            var title = new Label { Text = "📖  Yaku Reference" };
            title.AddThemeFontSizeOverride("font_size", 22);
            title.AddThemeColorOverride("font_color", Header);
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            titleRow.AddChild(title);

            var closeBtn = new Button { Text = "✕" };
            closeBtn.CustomMinimumSize = new Vector2(36, 36);
            closeBtn.AddThemeFontSizeOverride("font_size", 18);
            var closeBtnStyle = new StyleBoxFlat();
            closeBtnStyle.BgColor = new Color(0.50f, 0.12f, 0.12f);
            closeBtnStyle.SetCornerRadiusAll(6);
            closeBtn.AddThemeStyleboxOverride("normal", closeBtnStyle);
            var closeBtnHover = (StyleBoxFlat)closeBtnStyle.Duplicate();
            closeBtnHover.BgColor = closeBtnStyle.BgColor.Lightened(0.2f);
            closeBtn.AddThemeStyleboxOverride("hover", closeBtnHover);
            closeBtn.AddThemeColorOverride("font_color", Colors.White);
            closeBtn.Pressed += () => CloseRequested?.Invoke();
            titleRow.AddChild(closeBtn);
            root.AddChild(titleRow);

            root.AddChild(new HSeparator());

            // Subtitle
            var subtitle = new Label { Text = "A hand must contain at least one yaku to be valid. Dora adds fan but does not count as yaku." };
            subtitle.AddThemeFontSizeOverride("font_size", 13);
            subtitle.AddThemeColorOverride("font_color", TextDim);
            subtitle.AutowrapMode = TextServer.AutowrapMode.Word;
            root.AddChild(subtitle);

            // Scrollable content
            var scroll = new ScrollContainer();
            scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            root.AddChild(scroll);

            var content = new VBoxContainer();
            content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            content.AddThemeConstantOverride("separation", 4);
            scroll.AddChild(content);

            foreach (var section in GetYakuData())
            {
                // Section header
                var secPanel = new PanelContainer();
                secPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                var secStyle = new StyleBoxFlat();
                secStyle.BgColor = SectionHdr;
                secStyle.SetCornerRadiusAll(6);
                secPanel.AddThemeStyleboxOverride("panel", secStyle);
                var secLabel = new Label { Text = section.Name };
                secLabel.AddThemeFontSizeOverride("font_size", 15);
                secLabel.AddThemeColorOverride("font_color", Colors.White);
                secPanel.AddChild(secLabel);
                content.AddChild(secPanel);

                foreach (var y in section.Yaku)
                    content.AddChild(BuildYakuRow(y));

                content.AddChild(Spacer(4));
            }

            // Keyboard close
            SetProcessInput(true);
        }

        public override void _Input(InputEvent ev)
        {
            if (ev is InputEventKey k && k.Pressed && k.Keycode == Key.Escape)
            {
                CloseRequested?.Invoke();
                AcceptEvent();
            }
        }

        // =====================================================================
        // Row builder
        // =====================================================================

        private Control BuildYakuRow(YakuEntry y)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

            // Fan badge
            var badge = new PanelContainer();
            badge.CustomMinimumSize = new Vector2(52, 0);
            var badgeStyle = new StyleBoxFlat();
            badgeStyle.BgColor = y.IsYakuman
                ? new Color(0.30f, 0.12f, 0.06f)
                : new Color(0.12f, 0.18f, 0.30f);
            badgeStyle.SetCornerRadiusAll(4);
            badge.AddThemeStyleboxOverride("panel", badgeStyle);
            var fanLabel = new Label { Text = y.IsYakuman ? "役満" : $"{y.FanClosed}飜" };
            fanLabel.HorizontalAlignment = HorizontalAlignment.Center;
            fanLabel.VerticalAlignment   = VerticalAlignment.Center;
            fanLabel.AddThemeFontSizeOverride("font_size", 13);
            fanLabel.AddThemeColorOverride("font_color", y.IsYakuman ? YakumanClr : FanGold);
            badge.AddChild(fanLabel);
            row.AddChild(badge);

            // Name block
            var nameCol = new VBoxContainer();
            nameCol.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            nameCol.AddThemeConstantOverride("separation", 0);

            var nameRow = new HBoxContainer();
            nameRow.AddThemeConstantOverride("separation", 8);

            var engLabel = new Label { Text = y.EnglishName };
            engLabel.AddThemeFontSizeOverride("font_size", 15);
            engLabel.AddThemeColorOverride("font_color", TextMain);
            nameRow.AddChild(engLabel);

            var jpLabel = new Label { Text = y.JapaneseName };
            jpLabel.AddThemeFontSizeOverride("font_size", 13);
            jpLabel.AddThemeColorOverride("font_color", TextJP);
            jpLabel.VerticalAlignment = VerticalAlignment.Center;
            nameRow.AddChild(jpLabel);

            // Open fan if different
            if (!y.IsYakuman && y.FanOpen >= 0 && y.FanOpen != y.FanClosed)
            {
                var openTag = new Label { Text = $"({y.FanOpen}飜 open)" };
                openTag.AddThemeFontSizeOverride("font_size", 12);
                openTag.AddThemeColorOverride("font_color", TextDim);
                openTag.VerticalAlignment = VerticalAlignment.Center;
                nameRow.AddChild(openTag);
            }
            else if (!y.IsYakuman && y.FanOpen < 0)
            {
                var closedTag = new Label { Text = "(closed only)" };
                closedTag.AddThemeFontSizeOverride("font_size", 12);
                closedTag.AddThemeColorOverride("font_color", TextDim);
                closedTag.VerticalAlignment = VerticalAlignment.Center;
                nameRow.AddChild(closedTag);
            }

            nameCol.AddChild(nameRow);

            var descLabel = new Label { Text = y.Description };
            descLabel.AddThemeFontSizeOverride("font_size", 12);
            descLabel.AddThemeColorOverride("font_color", TextDim);
            descLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            nameCol.AddChild(descLabel);

            row.AddChild(nameCol);
            return row;
        }

        private static Control Spacer(float h)
        {
            var c = new Control();
            c.CustomMinimumSize = new Vector2(0, h);
            return c;
        }

        // =====================================================================
        // Yaku data
        // =====================================================================

        private record YakuEntry(
            string EnglishName,
            string JapaneseName,
            int    FanClosed,       // -1 = yakuman
            int    FanOpen,         // -1 = closed only, -2 = yakuman
            string Description,
            bool   IsYakuman = false);

        private record YakuSection(string Name, List<YakuEntry> Yaku);

        private static List<YakuSection> GetYakuData() => new()
        {
            new("Basic — Special Conditions", new()
            {
                new("Riichi",            "立直",   1, -1, "Declare tenpai with a closed hand and pay 1000 points."),
                new("Double Riichi",     "ダブル立直", 2, -1, "Riichi declared on your very first discard of the hand."),
                new("Ippatsu",           "一発",   1, -1, "Win within one full round of draws after declaring riichi (no claims in between)."),
                new("Menzen Tsumo",      "門前清自摸和", 1, -1, "Win by self-draw with a fully closed hand."),
                new("Pinfu",             "平和",   1, -1, "All four sets are sequences, the pair is not a value tile, and the wait is two-sided (ryanmen)."),
                new("Tanyao",            "断么九", 1, 1,  "All tiles are simples (2–8); no terminals or honours."),
            }),

            new("Sequences", new()
            {
                new("Pure Double Sequence", "一盃口",  1, -1, "Two identical sequences (closed only)."),
                new("Twice Pure Double Sequence", "二盃口", 3, -1, "Two pairs of identical sequences — supersedes Iipeikou (closed only)."),
                new("Mixed Triple Sequence", "三色同順", 2, 1, "The same sequence in all three suits."),
                new("Pure Straight",     "一気通貫", 2, 1,  "1-2-3, 4-5-6, and 7-8-9 sequences all in the same suit."),
            }),

            new("Triplets & Sets", new()
            {
                new("All Pungs",         "対々和",  2, 2,  "All four sets are triplets (or quads)."),
                new("Three Concealed Pungs", "三暗刻", 2, 2, "Three triplets formed without claiming any discards."),
                new("Three Kongs",       "三槓子",  2, 2,  "Three kans declared in the same hand."),
                new("Triple Pung",       "三色同刻", 2, 2,  "The same triplet in all three suits."),
            }),

            new("Honours & Terminals", new()
            {
                new("White Dragon",      "白",    1, 1,   "A triplet (or quad) of white dragon tiles (中)."),
                new("Green Dragon",      "發",    1, 1,   "A triplet (or quad) of green dragon tiles (發)."),
                new("Red Dragon",        "中",    1, 1,   "A triplet (or quad) of red dragon tiles (白)."),
                new("Seat Wind",         "門風牌", 1, 1,   "A triplet of your own seat wind tile."),
                new("Round Wind",        "場風牌", 1, 1,   "A triplet of the current round wind tile."),
                new("Little Three Dragons","小三元", 2, 2, "Triplets of two dragons and a pair of the third."),
                new("Outside Hand",      "混全帯么九", 2, 1, "Every set and the pair contain a terminal or honour tile."),
                new("Terminals in All Sets", "純全帯么九", 3, 2, "Every set and the pair contain a terminal (no honours)."),
                new("All Terminals & Honours", "混老頭", 2, 2, "Every tile is a terminal or honour."),
            }),

            new("Flush", new()
            {
                new("Half Flush",        "混一色", 3, 2,  "All tiles are from one suit plus honours."),
                new("Full Flush",        "清一色", 6, 5,  "All tiles are from one suit — no honours."),
            }),

            new("Special Wins", new()
            {
                new("After a Kong",      "嶺上開花", 1, 1, "Win on the replacement tile drawn after declaring a kan."),
                new("Robbing the Kong",  "槍槓",   1, 1,  "Ron declared on a tile used to extend a pon into a kan (kakan)."),
                new("Under the Sea",     "海底摸月", 1, 1, "Win by tsumo on the very last tile in the wall."),
                new("Under the River",   "河底撈魚", 1, 1, "Win by ron on the very last discard."),
                new("Seven Pairs",       "七対子",  2, -1, "Seven different pairs (closed only). Fixed fu of 25."),
            }),

            new("Special Deals (Yakuman)", new()
            {
                new("Blessing of Heaven","天和",   0, -2, "Dealer wins on their initial 14-tile hand before any discard.", true),
                new("Blessing of Earth", "地和",   0, -2, "Non-dealer wins by tsumo on their very first draw.", true),
                new("Blessing of Man",   "人和",   0, -2, "Non-dealer wins by ron before their first draw.", true),
            }),

            new("Yakuman — Standard", new()
            {
                new("Thirteen Orphans",  "国士無双", 0, -2, "One each of 1m 9m 1p 9p 1s 9s and all seven honours, plus any duplicate.", true),
                new("Nine Gates",        "九蓮宝燈", 0, -2, "Closed hand: 1112345678999 in one suit, plus any tile of that suit.", true),
                new("Four Concealed Pungs","四暗刻", 0, -2, "Four triplets formed with no claimed discards.", true),
                new("Four Kongs",        "四槓子",  0, -2, "Four kans declared in the same hand.", true),
                new("Big Three Dragons", "大三元",  0, -2, "Triplets of all three dragon tiles.", true),
                new("Little Four Winds", "小四喜",  0, -2, "Triplets of three winds and a pair of the fourth.", true),
                new("Big Four Winds",    "大四喜",  0, -2, "Triplets of all four wind tiles. (Double yakuman in some rules.)", true),
                new("All Green",         "緑一色",  0, -2, "Hand composed entirely of green tiles: 2s 3s 4s 6s 8s and/or green dragon.", true),
                new("All Terminals",     "清老頭",  0, -2, "All tiles are 1s or 9s — no honours.", true),
                new("All Honours",       "字一色",  0, -2, "Every tile is an honour (winds and dragons).", true),
            }),
        };
    }
}
