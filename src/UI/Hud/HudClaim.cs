// =============================================================================
// HudClaim.cs
// The claim window: what the player sees when someone discards a tile they can
// take, and the twenty seconds they have to decide.
//
// Pass 08 changes three things about the old action bar:
//
//   - The calls become a window over the felt rather than buttons in a strip at
//     the bottom of the screen, so the decision sits near the tile it is about.
//   - The countdown is a ring drawn on the discarded tile instead of a bar
//     elsewhere. The thing running out of time is that tile.
//   - Chi with more than one interpretation opens a picker before sending,
//     rather than the client silently choosing on the player's behalf.
//
// The five call buttons keep their five unrelated hues. That is deliberate in
// the handoff and pass 10 does not depend on them: the calls are ranked by
// position and by label, so the colour is decoration rather than information.
// =============================================================================

using Godot;
using System.Collections.Generic;
using RiichiMahjong.Core;

namespace RiichiMahjong.UI
{
    public partial class HUD
    {
        /// <summary>Raised with the chosen interpretation once the player picks a chi.</summary>
        [Signal] public delegate void ChiVariantChosenEventHandler(int variantIndex);

        private Panel?         _chiPicker;
        private VBoxContainer? _chiPickerRows;

        // The tile currently wearing the countdown ring, so it can be cleared again.
        private TileNode? _countdownTile;

        // =====================================================================
        // Countdown ring
        // =====================================================================

        /// <summary>
        /// Put the countdown ring on the given seat's most recent discard. Called when
        /// the claim window opens; the ring is then driven by UpdateCountdown.
        /// </summary>
        public void SetCountdownTile(int playerIndex)
        {
            ClearCountdownTile();
            _countdownTile = GetLastDiscardTileNode(playerIndex);
            _countdownTile?.SetCountdown(1f);
        }

        /// <summary>Take the ring off whichever tile has it.</summary>
        public void ClearCountdownTile()
        {
            if (_countdownTile != null && IsInstanceValid(_countdownTile))
                _countdownTile.SetCountdown(-1f);
            _countdownTile = null;
        }

        /// <summary>Advance the ring. Safe to call when no tile is counting down.</summary>
        private void UpdateCountdownRing(float fraction)
        {
            if (_countdownTile != null && IsInstanceValid(_countdownTile))
                _countdownTile.SetCountdown(fraction);
        }

        // =====================================================================
        // Chi variant picker
        // =====================================================================

        /// <summary>
        /// Offer the player each way the discarded tile could complete a run.
        /// Each row shows the two tiles from hand plus the claimable tile ringed gold,
        /// so the choice is made on the actual shapes rather than on a description.
        /// </summary>
        public void ShowChiPicker(IReadOnlyList<(Tile t1, Tile t2)> variants, Tile claimed)
        {
            EnsureChiPicker();

            foreach (var child in _chiPickerRows!.GetChildren()) child.QueueFree();

            for (int i = 0; i < variants.Count; i++)
            {
                int index = i;
                _chiPickerRows.AddChild(BuildChiRow(variants[i], claimed, () =>
                {
                    HideChiPicker();
                    EmitSignal(SignalName.ChiVariantChosen, index);
                }));
            }

            _chiPicker!.Visible = true;
        }

        public void HideChiPicker()
        {
            if (_chiPicker != null) _chiPicker.Visible = false;
        }

        private Control BuildChiRow((Tile t1, Tile t2) variant, Tile claimed,
                                    System.Action onChosen)
        {
            var button = new Button
            {
                CustomMinimumSize = new Vector2(0, DoloLayout.IsTouch ? 76 : 84),
            };
            button.ThemeTypeVariation = DoloTheme.ButtonSecondary;
            button.Pressed += onChosen;

            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            row.AddThemeConstantOverride("separation", 4);
            row.Alignment = BoxContainer.AlignmentMode.Center;

            // The run in ascending order, so it reads as the sequence it will become.
            var ordered = new List<(Tile tile, bool isClaimed)>
            {
                (variant.t1, false), (variant.t2, false), (claimed, true),
            };
            ordered.Sort((a, b) => a.tile.Value.CompareTo(b.tile.Value));

            var size = DoloLayout.IsTouch ? new Vector2I(38, 52) : new Vector2I(44, 60);
            foreach (var (tile, isClaimed) in ordered)
            {
                var node = new TileNode();
                node.SetCellMetrics(size, size);
                node.SetTile(tile, faceDown: false);
                node.SetInteractive(false);

                // The tile being claimed is ringed, so which one comes from the river
                // is obvious without a caption.
                if (isClaimed) node.SetDoraGlow(true);

                row.AddChild(node);
            }

            button.AddChild(row);
            return button;
        }

        private void EnsureChiPicker()
        {
            if (_chiPicker != null) return;

            _chiPicker = new Panel { Visible = false, MouseFilter = MouseFilterEnum.Stop };
            _chiPicker.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);

            float width  = DoloLayout.IsTouch ? 260 : 320;
            float height = DoloLayout.IsTouch ? 240 : 300;
            _chiPicker.OffsetLeft   = -width  * 0.5f;
            _chiPicker.OffsetTop    = -height * 0.5f;
            _chiPicker.OffsetRight  =  width  * 0.5f;
            _chiPicker.OffsetBottom =  height * 0.5f;
            _chiPicker.AddThemeStyleboxOverride("panel", DoloStyles.Card(20));

            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 10);

            var title = new Label { Text = "CHOOSE YOUR CHI" };
            title.ThemeTypeVariation = DoloTheme.PlateName;
            column.AddChild(title);
            column.AddChild(DoloStyles.HairlineRow(0.28f));

            _chiPickerRows = new VBoxContainer();
            _chiPickerRows.AddThemeConstantOverride("separation", 8);
            _chiPickerRows.SizeFlagsVertical = SizeFlags.ExpandFill;
            column.AddChild(_chiPickerRows);

            var cancel = DoloWidgets.IconButton(DoloIcon.Close, "Cancel",
                                                DoloTheme.ButtonGhost, height: 44);
            cancel.Pressed += HideChiPicker;
            column.AddChild(cancel);

            _chiPicker.AddChild(column);
            AddChild(_chiPicker);
        }
    }
}
