// =============================================================================
// CosmeticsScreen.cs
// The cosmetics picker: four slot groups over one live preview.
//
// The preview is the south wedge drawn exactly as the table draws it, at 1:1.
// That matters more than it sounds - a swatch grid shows you colours, but the
// question a player is actually asking is "what does my spot at the table look
// like", and only the real geometry answers it.
//
// Picking updates the preview immediately. Persistence is per account where
// there is one, and to settings.cfg otherwise: sign-in is optional by design,
// so a guest still gets the free set rather than an empty table.
// =============================================================================

using Godot;
using System.Collections.Generic;
using RiichiMahjong.Core;

namespace RiichiMahjong.UI
{
    public partial class CosmeticsScreen : Control
    {
        private const int PreviewSize = 420;

        private CosmeticSet _set = new();

        private TableFelt?     _previewFelt;
        private PanelContainer _previewPlate = null!;
        private Label          _previewName  = null!;
        private Control?       _previewEmblem;
        private HBoxContainer  _plateRow     = null!;

        private readonly Dictionary<CosmeticSlot, Button[]> _slotButtons = new();

        public override void _Ready()
        {
            DoloTheme.Apply(this);
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

            _set = GameSettings.CosmeticSet;

            var background = new ColorRect { Color = DoloTokens.Page };
            background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            AddChild(background);

            BuildLayout();
            RefreshPreview();
        }

        // =====================================================================
        // Layout
        // =====================================================================

        private void BuildLayout()
        {
            var page = new HBoxContainer();
            page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            page.OffsetLeft   =  96;
            page.OffsetRight  = -96;
            page.OffsetTop    =  72;
            page.OffsetBottom = -72;
            page.AddThemeConstantOverride("separation", 40);

            page.AddChild(BuildSlotColumn());
            page.AddChild(BuildPreviewColumn());

            AddChild(page);
        }

        private Control BuildSlotColumn()
        {
            var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            column.AddThemeConstantOverride("separation", 18);

            var title = new Label { Text = "YOUR TABLE" };
            title.ThemeTypeVariation = DoloTheme.ScreenTitle;
            column.AddChild(title);

            var subtitle = new Label
            {
                Text = GameSettings.IsLoggedIn
                    ? $"Saved to {GameSettings.AuthUsername}"
                    : "Saved on this device — sign in to unlock more and carry them with you",
            };
            subtitle.ThemeTypeVariation = DoloTheme.Mono;
            column.AddChild(subtitle);

            column.AddChild(DoloStyles.HairlineRow(0.28f));

            var scroll = new ScrollContainer
            {
                SizeFlagsVertical    = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };

            var slots = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            slots.AddThemeConstantOverride("separation", 20);

            slots.AddChild(BuildSlotGroup(CosmeticSlot.Surface, "Wedge surface"));
            slots.AddChild(BuildSlotGroup(CosmeticSlot.Frame,   "Nameplate frame"));
            slots.AddChild(BuildSlotGroup(CosmeticSlot.Prop,    "Personal prop"));
            slots.AddChild(BuildSlotGroup(CosmeticSlot.Emblem,  "Emblem"));

            scroll.AddChild(slots);
            column.AddChild(scroll);

            var buttons = new HBoxContainer();
            buttons.AddThemeConstantOverride("separation", 12);

            var save = DoloWidgets.IconButton(DoloIcon.Check, "Save", DoloTheme.ButtonPrimary);
            save.Pressed += SaveAndLeave;

            var back = DoloWidgets.IconButton(DoloIcon.Back, "Back", DoloTheme.ButtonGhost);
            back.Pressed += LeaveWithoutSaving;

            buttons.AddChild(save);
            buttons.AddChild(back);
            column.AddChild(buttons);

            return column;
        }

        /// <summary>
        /// One slot's row of swatches: 130px wide with a 100px preview tile, selected
        /// marked by a 2px brass ring, and locked entries carrying a "· locked" suffix.
        /// </summary>
        private Control BuildSlotGroup(CosmeticSlot slot, string heading)
        {
            var group = new VBoxContainer();
            group.AddThemeConstantOverride("separation", 10);
            group.AddChild(DoloWidgets.SectionHeading(heading));

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);

            var options = CosmeticCatalogue.For(slot);
            var buttons = new Button[options.Count];

            for (int i = 0; i < options.Count; i++)
            {
                var option = options[i];
                var button = new Button
                {
                    CustomMinimumSize = new Vector2(130, 148),
                    Disabled          = IsLocked(option),
                };
                button.ThemeTypeVariation = DoloTheme.ButtonSecondary;
                button.Pressed += () =>
                {
                    _set.Set(slot, option.Id);
                    RefreshPreview();
                    RefreshSlotSelection(slot);
                };

                var content = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
                content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                content.AddThemeConstantOverride("separation", 8);
                content.Alignment = BoxContainer.AlignmentMode.Center;

                content.AddChild(BuildSwatch(slot, option.Id));

                var label = new Label
                {
                    Text                = option.Name + (IsLocked(option) ? "  · locked" : ""),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                };
                label.ThemeTypeVariation = IsLocked(option) ? DoloTheme.MonoSmall : DoloTheme.BodySmall;
                content.AddChild(label);

                button.AddChild(content);
                row.AddChild(button);
                buttons[i] = button;
            }

            _slotButtons[slot] = buttons;
            group.AddChild(row);
            RefreshSlotSelection(slot);
            return group;
        }

        /// <summary>The 100px tile inside a swatch, showing what that option actually is.</summary>
        private static Control BuildSwatch(CosmeticSlot slot, string id)
        {
            var frame = new PanelContainer
            {
                CustomMinimumSize   = new Vector2(100, 76),
                MouseFilter         = MouseFilterEnum.Ignore,
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            };

            switch (slot)
            {
                case CosmeticSlot.Surface:
                    frame.AddThemeStyleboxOverride("panel",
                        DoloStyles.Flat(CosmeticVisuals.Surface(id), DoloTokens.RadiusInset,
                                        DoloTokens.HairlineFaint, borderWidth: 1));
                    break;

                case CosmeticSlot.Frame:
                    frame.AddThemeStyleboxOverride("panel", CosmeticVisuals.Frame(id));
                    break;

                case CosmeticSlot.Prop:
                {
                    frame.AddThemeStyleboxOverride("panel",
                        DoloStyles.Flat(DoloTokens.FeltQuiet, DoloTokens.RadiusInset,
                                        DoloTokens.HairlineFaint, borderWidth: 1));

                    var texture = CosmeticVisuals.Prop(id);
                    if (texture != null)
                    {
                        frame.AddChild(new TextureRect
                        {
                            Texture     = texture,
                            ExpandMode  = TextureRect.ExpandModeEnum.IgnoreSize,
                            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                            MouseFilter = MouseFilterEnum.Ignore,
                        });
                    }
                    else if (id != "none")
                    {
                        // Art not made yet — show the dashed pocket, same as the table.
                        frame.AddChild(new DashedRing { RingColor = DoloTokens.HairlineMedium });
                    }
                    break;
                }

                case CosmeticSlot.Emblem:
                {
                    frame.AddThemeStyleboxOverride("panel",
                        DoloStyles.Flat(DoloTokens.InsetPane, DoloTokens.RadiusInset,
                                        DoloTokens.HairlineFaint, borderWidth: 1));

                    var emblem = CosmeticVisuals.Emblem(id, 32);
                    if (emblem != null)
                    {
                        emblem.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
                        emblem.SizeFlagsVertical   = SizeFlags.ShrinkCenter;
                        frame.AddChild(emblem);
                    }
                    break;
                }
            }

            return frame;
        }

        // =====================================================================
        // Preview
        // =====================================================================

        private Control BuildPreviewColumn()
        {
            var column = new VBoxContainer
            {
                CustomMinimumSize = new Vector2(PreviewSize + 60, 0),
            };
            column.AddThemeConstantOverride("separation", 14);

            column.AddChild(DoloWidgets.SectionHeading("Live preview"));

            var card = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            card.AddThemeStyleboxOverride("panel", DoloStyles.Card(20));

            var holder = new Control
            {
                CustomMinimumSize = new Vector2(PreviewSize, PreviewSize),
                ClipContents      = true,
            };

            // The real felt node, so the preview is the table rather than a mock-up of it.
            _previewFelt = new TableFelt();
            _previewFelt.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            holder.AddChild(_previewFelt);

            // The nameplate, drawn at table scale so the frame and emblem read true.
            _previewPlate = new PanelContainer();
            _previewPlate.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom);
            _previewPlate.OffsetLeft   = -80;
            _previewPlate.OffsetRight  =  80;
            _previewPlate.OffsetTop    = -110;
            _previewPlate.OffsetBottom = -20;

            _plateRow = new HBoxContainer();
            _plateRow.AddThemeConstantOverride("separation", 8);

            var plateColumn = new VBoxContainer();
            plateColumn.AddThemeConstantOverride("separation", 2);

            _previewName = new Label
            {
                Text = GameSettings.PlayerName.Length > 0 ? GameSettings.PlayerName : "You",
            };
            _previewName.ThemeTypeVariation = DoloTheme.PlateName;

            var score = new Label { Text = "25,000" };
            score.ThemeTypeVariation = DoloTheme.MonoLarge;

            plateColumn.AddChild(_previewName);
            plateColumn.AddChild(score);
            _plateRow.AddChild(plateColumn);

            _previewPlate.AddChild(_plateRow);
            holder.AddChild(_previewPlate);

            card.AddChild(holder);
            column.AddChild(card);

            var note = new Label
            {
                Text = "Everyone at the table sees your wedge, so this is what the other "
                     + "three players will be looking at.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            note.ThemeTypeVariation = DoloTheme.Dim;
            column.AddChild(note);

            return column;
        }

        private void RefreshPreview()
        {
            if (_previewFelt == null) return;

            // Only the self wedge is the player's; the rest stay quiet so the preview
            // shows the contrast they will actually see.
            _previewFelt.SetSeatSurface(TableFelt.SeatSelf, CosmeticVisuals.Surface(_set.Surface));
            _previewFelt.SetSeatProp(TableFelt.SeatSelf, CosmeticVisuals.Prop(_set.Prop));

            _previewPlate.AddThemeStyleboxOverride("panel", CosmeticVisuals.Frame(_set.Frame));

            _previewEmblem?.QueueFree();
            _previewEmblem = CosmeticVisuals.Emblem(_set.Emblem);
            if (_previewEmblem != null)
            {
                _previewEmblem.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                _plateRow.AddChild(_previewEmblem);
            }
        }

        private void RefreshSlotSelection(CosmeticSlot slot)
        {
            if (!_slotButtons.TryGetValue(slot, out var buttons)) return;

            var options = CosmeticCatalogue.For(slot);
            for (int i = 0; i < buttons.Length && i < options.Count; i++)
            {
                var style = DoloStyles.ButtonSecondary();
                if (options[i].Id == _set.Get(slot))
                {
                    style.BorderColor = DoloTokens.Brass;
                    DoloStyles.SetBorder(style, 2);
                }
                buttons[i].AddThemeStyleboxOverride("normal", style);
                buttons[i].AddThemeStyleboxOverride("disabled", DoloStyles.ButtonSecondary(0.55f));
            }
        }

        /// <summary>
        /// Locked entries are shown rather than hidden, so a player can see what is
        /// there to earn - but a guest cannot equip one, since unlocks live on accounts.
        /// </summary>
        private static bool IsLocked(CosmeticOption option)
            => !option.IsFree && !GameSettings.IsLoggedIn;

        // =====================================================================
        // Persistence
        // =====================================================================

        private void SaveAndLeave()
        {
            GameSettings.Cosmetics = _set.Serialise();
            GameSettings.Save();

            // Signed-in players also store the set on the account, so it follows them
            // to another device. Guests keep it locally and that is the whole story.
            if (GameSettings.IsLoggedIn)
                NetworkManager.Instance?.SendCosmetics(_set.Serialise());

            GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
        }

        private void LeaveWithoutSaving()
            => GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
    }
}
