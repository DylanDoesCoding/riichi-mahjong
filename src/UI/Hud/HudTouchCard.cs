// =============================================================================
// HudTouchCard.cs
// The touch replacement for desktop hover.
//
// On a phone there is no hover, so the information the desktop shows while the
// pointer rests on a tile has to come from the first tap instead:
//
//   Tap one   - selects the tile, lifts it, rings it in brass, highlights every
//               matching tile in the rivers, and opens this card.
//   Tap two   - on the card's DISCARD button or on the raised tile again,
//               commits the discard.
//
// The card is 56px and sits above the hand, replacing the outlined text the
// desktop draws straight onto the felt.  The desktop hover path is untouched.
// =============================================================================

using Godot;

namespace RiichiMahjong.UI
{
    public partial class HUD
    {
        /// <summary>Raised when the player confirms a discard from the card's button.</summary>
        [Signal] public delegate void TouchDiscardPressedEventHandler();

        private Panel?  _touchCard;
        private Label?  _touchCardLabel;
        private Button? _touchCardDiscard;

        /// <summary>
        /// Build the card lazily: it only exists in the touch layout, and building it
        /// on demand keeps the desktop node tree unchanged.
        /// </summary>
        private void EnsureTouchCard()
        {
            if (_touchCard != null) return;

            int handHeight = DoloLayout.HandCell.Y;
            const int gap  = 8;

            _touchCard = new Panel { Visible = false };
            _touchCard.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
            _touchCard.OffsetLeft   =  16;
            _touchCard.OffsetRight  = -16;
            _touchCard.OffsetTop    = -(handHeight + gap + DoloLayout.InfoCardHeight);
            _touchCard.OffsetBottom = -(handHeight + gap);
            _touchCard.AddThemeStyleboxOverride("panel", TouchCardStyle());

            var row = new HBoxContainer();
            row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            row.AddThemeConstantOverride("separation", 12);
            row.Alignment = BoxContainer.AlignmentMode.Center;

            _touchCardLabel = new Label();
            _touchCardLabel.ThemeTypeVariation  = DoloTheme.Body;
            _touchCardLabel.VerticalAlignment   = VerticalAlignment.Center;
            _touchCardLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _touchCardLabel.AddThemeFontSizeOverride("font_size", DoloTokens.PhoneSizeBody);

            _touchCardDiscard = new Button { Text = "DISCARD" };
            _touchCardDiscard.ThemeTypeVariation = DoloTheme.ButtonPrimary;
            _touchCardDiscard.CustomMinimumSize  = new Vector2(DoloLayout.DiscardButton.X,
                                                               DoloLayout.DiscardButton.Y);
            _touchCardDiscard.AddThemeFontSizeOverride("font_size", DoloTokens.PhoneSizeName);
            _touchCardDiscard.Pressed += () => EmitSignal(SignalName.TouchDiscardPressed);

            row.AddChild(_touchCardLabel);
            row.AddChild(_touchCardDiscard);
            _touchCard.AddChild(row);
            AddChild(_touchCard);
        }

        private static StyleBoxFlat TouchCardStyle()
        {
            var box = DoloStyles.Flat(DoloTokens.Card, DoloTokens.RadiusCard,
                                      DoloTokens.HairlineStrong, borderWidth: 1);
            box.ContentMarginLeft   = 16;
            box.ContentMarginRight  = 16;
            box.ContentMarginTop    = 6;
            box.ContentMarginBottom = 6;
            box.ShadowColor  = DoloTokens.ShadowCardColor;
            box.ShadowSize   = 20;
            box.ShadowOffset = new Vector2(0, 6);
            return box;
        }

        /// <summary>Open the card with the same line the desktop shows on hover.</summary>
        public void ShowTouchInfoCard(string text)
        {
            EnsureTouchCard();
            _touchCardLabel!.Text = text;
            _touchCard!.Visible   = true;
        }

        public void HideTouchInfoCard()
        {
            if (_touchCard != null) _touchCard.Visible = false;
        }

        /// <summary>
        /// Enable or disable the confirm button. A tile that cannot legally be discarded
        /// - the wrong tile during riichi selection - shows the card but not the action.
        /// </summary>
        public void SetTouchDiscardEnabled(bool enabled)
        {
            EnsureTouchCard();
            _touchCardDiscard!.Disabled = !enabled;
        }
    }
}
