// =============================================================================
// MainMenu.cs — the DOLO start screen and the Settings overlay.
//
// Pass 05 of the redesign. Three things changed beyond the restyle:
//
//   - The emoji title (🀄 RIICHI MAHJONG) becomes the drawn DOLO wordmark. The
//     mark is the table's own four-wedge geometry, since the game is named
//     after a person's handle and "make the table yours" is the thesis.
//   - The tile-set toggle moves off the menu and into Settings, which is where
//     a display preference belongs and which frees the menu to be three ways
//     to start a game plus a way out.
//   - Server URL lives in Settings rather than in the lobby (pass 06), so the
//     lobby can be about starting a game rather than about configuration.
//
// The Settings card lays its sections across two columns so every row is visible
// at once - the earlier single-column card held more rows than its height could
// show, so the body scrolled and cut off mid section.
// =============================================================================

using Godot;
using System.Collections.Generic;

namespace RiichiMahjong.UI
{
    public partial class MainMenu : Control
    {
        // ---- Scene nodes (from .tscn) ----------------------------------------
        private Button  _quickPlayBtn = null!;
        private Control _centrePanel  = null!;

        // ---- Settings overlay (built in code) --------------------------------
        private Control  _optionsPanel  = null!;
        private Label    _musicPctLabel = null!;
        private Label    _sfxPctLabel   = null!;
        private LineEdit _nameEdit      = null!;
        private LineEdit _urlEdit       = null!;
        private Button[] _tileSetButtons = System.Array.Empty<Button>();
        private Button[] _layoutButtons  = System.Array.Empty<Button>();

        // ---- Quick Play state -----------------------------------------------
        private bool _quickPlayActive = false;

        // ---- Audio -----------------------------------------------------------
        private AudioStreamPlayer _musicPlayer = null!;

        // ---- Paths -----------------------------------------------------------
        private const string MusicPath =
            "res://Assets/Sounds/Whispering_Bamboo_Garden_2026-05-18T203144.wav";

        // =====================================================================
        // Lifecycle
        // =====================================================================

        public override void _Ready()
        {
            DoloTheme.Apply(this);

            _centrePanel = GetNode<Control>("CentrePanel");
            var playBtn        = GetNode<Button>("CentrePanel/PlayButton");
            var multiplayerBtn = GetNode<Button>("CentrePanel/MultiplayerButton");
            var tableBtn       = GetNode<Button>("CentrePanel/TableButton");
            var optionsBtn     = GetNode<Button>("CentrePanel/OptionsButton");
            var quitBtn        = GetNode<Button>("CentrePanel/QuitButton");
            _quickPlayBtn      = GetNode<Button>("CentrePanel/QuickPlayButton");

            playBtn.Pressed        += OnPlayPressed;
            multiplayerBtn.Pressed += OnMultiplayerPressed;
            tableBtn.Pressed       += OnYourTablePressed;
            optionsBtn.Pressed     += OnOptionsPressed;
            quitBtn.Pressed        += OnQuitPressed;
            _quickPlayBtn.Pressed  += OnQuickPlayPressed;

            // Play vs CPU is the one primary action; everything else is secondary.
            DoloWidgets.DecorateButton(playBtn,        DoloIcon.Play,   DoloTheme.ButtonPrimary);
            DoloWidgets.DecorateButton(multiplayerBtn, DoloIcon.Globe);
            DoloWidgets.DecorateButton(_quickPlayBtn,  DoloIcon.Bolt);
            DoloWidgets.DecorateButton(tableBtn,       DoloIcon.Tile,  DoloTheme.ButtonGhost, 18);
            DoloWidgets.DecorateButton(optionsBtn,     DoloIcon.Gear,  DoloTheme.ButtonGhost, 18);
            DoloWidgets.DecorateButton(quitBtn,        DoloIcon.Close, DoloTheme.ButtonGhost, 18);

            BuildWordmark();
            BuildFooter();
            BuildOptionsPanel();
            StartMusic();
        }

        public override void _ExitTree() => UnwireQuickPlay();

        private void BuildWordmark()
        {
            var slot = GetNode<Control>("CentrePanel/WordmarkSlot");
            var wordmark = new DoloWordmark();
            wordmark.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            slot.AddChild(wordmark);
        }

        /// <summary>Version and engine line, sitting quietly at the bottom of the screen.</summary>
        private void BuildFooter()
        {
            // Build the version from its parts rather than the full string, whose trailing
            // "(official)" is build provenance and reads as noise on a title screen.
            var v = Engine.GetVersionInfo();
            string version = $"{v["major"]}.{v["minor"]}.{v["patch"]}-{v["status"]}";

            var footer = new Label
            {
                Text                = $"DOLO MAHJONG  ·  GODOT {version}",
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter         = MouseFilterEnum.Ignore,
            };
            footer.ThemeTypeVariation = DoloTheme.MonoSmall;
            footer.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
            footer.OffsetTop    = -34;
            footer.OffsetBottom = -14;
            AddChild(footer);
        }

        private void StartMusic()
        {
            _musicPlayer = new AudioStreamPlayer { Bus = "Master" };
            AddChild(_musicPlayer);

            var music = GD.Load<AudioStream>(MusicPath);
            if (music == null) return;

            _musicPlayer.Stream   = music;
            _musicPlayer.VolumeDb = GameSettings.LinearToDb(GameSettings.MusicVolume);
            _musicPlayer.Autoplay = false;
            _musicPlayer.Finished += () => _musicPlayer.Play();
            _musicPlayer.Play();
        }

        // =====================================================================
        // Menu actions
        // =====================================================================

        private void OnPlayPressed()
        {
            _musicPlayer.Stop();
            GetTree().ChangeSceneToFile("res://Scenes/GameTable.tscn");
        }

        private void OnMultiplayerPressed()
        {
            _musicPlayer.Stop();
            GetTree().ChangeSceneToFile("res://Scenes/Lobby.tscn");
        }

        private void OnQuitPressed() => GetTree().Quit();

        /// <summary>
        /// The cosmetics picker. It is a menu entry rather than a Settings row because
        /// making the table yours is the product, not a preference.
        /// </summary>
        private void OnYourTablePressed()
        {
            _musicPlayer.Stop();
            GetTree().ChangeSceneToFile("res://Scenes/Cosmetics.tscn");
        }

        private void OnOptionsPressed()
        {
            // Refresh from saved settings each time the panel opens.
            _nameEdit.Text = GameSettings.PlayerName;
            _urlEdit.Text  = GameSettings.ServerUrl;
            RefreshTileSetButtons();
            RefreshLayoutButtons();

            _centrePanel.Visible  = false;
            _optionsPanel.Visible = true;
        }

        // =====================================================================
        // Quick Play
        // =====================================================================

        private void OnQuickPlayPressed()
        {
            if (_quickPlayActive) return;

            var nm = NetworkManager.Instance;
            if (nm == null) return;

            var url = GameSettings.ServerUrl.Length > 0
                ? GameSettings.ServerUrl
                : "ws://localhost:5000/ws";
            if (GameSettings.PlayerName.Length == 0) GameSettings.PlayerName = "Player";

            _quickPlayActive       = true;
            _quickPlayBtn.Disabled = true;
            SetQuickPlayLabel("Connecting…");

            nm.OnConnected   += OnQuickPlayConnected;
            nm.OnRoomCreated += OnQuickPlayRoomCreated;
            nm.OnGameStarted += OnQuickPlayGameStarted;
            nm.OnError       += OnQuickPlayError;

            nm.Connect(url);
        }

        private void OnQuickPlayConnected()
            => NetworkManager.Instance?.CreateRoom(GameSettings.PlayerName);

        private void OnQuickPlayRoomCreated(string code, int seat, List<NetPlayerInfo> players)
            => NetworkManager.Instance?.StartGame();

        private void OnQuickPlayGameStarted(int seat, string[] names)
        {
            UnwireQuickPlay();
            _musicPlayer.Stop();
            GetTree().ChangeSceneToFile("res://Scenes/GameTable.tscn");
        }

        private void OnQuickPlayError(string error)
        {
            _quickPlayActive       = false;
            _quickPlayBtn.Disabled = false;
            SetQuickPlayLabel("Quick Play");
            UnwireQuickPlay();
            NetworkManager.Instance?.ResetSession();
        }

        /// <summary>
        /// The button's label now lives in the icon row rather than in Button.Text, so
        /// status changes have to reach into it.
        /// </summary>
        private void SetQuickPlayLabel(string text)
        {
            foreach (var child in _quickPlayBtn.GetChildren())
                if (child is HBoxContainer row)
                    foreach (var inner in row.GetChildren())
                        if (inner is Label label) { label.Text = text; return; }
        }

        private void UnwireQuickPlay()
        {
            var nm = NetworkManager.Instance;
            if (nm == null) return;
            nm.OnConnected   -= OnQuickPlayConnected;
            nm.OnRoomCreated -= OnQuickPlayRoomCreated;
            nm.OnGameStarted -= OnQuickPlayGameStarted;
            nm.OnError       -= OnQuickPlayError;
        }

        // =====================================================================
        // Settings overlay
        // =====================================================================

        private void BuildOptionsPanel()
        {
            _optionsPanel = new Control { Visible = false };
            _optionsPanel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

            var backdrop = new ColorRect { Color = DoloTokens.Page };
            backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _optionsPanel.AddChild(backdrop);

            // Two columns rather than one scrolling column: the single-column card held
            // more rows than 590px could show, so the body scrolled and cut off mid
            // section. Splitting the sections across two columns lets every row show at
            // once on a card that still fits the viewport - no scroll, nothing hidden.
            var card = new PanelContainer();
            card.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
            card.OffsetLeft   = -440;
            card.OffsetTop    = -300;
            card.OffsetRight  =  440;
            card.OffsetBottom =  300;
            card.AddThemeStyleboxOverride("panel", DoloStyles.Card(28));

            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 18);

            var title = new Label { Text = "SETTINGS" };
            title.ThemeTypeVariation = DoloTheme.NameSmall;
            column.AddChild(title);
            column.AddChild(DoloStyles.HairlineRow(0.28f));

            var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            columns.AddThemeConstantOverride("separation", 36);

            var leftCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            leftCol.AddThemeConstantOverride("separation", 18);
            var rightCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            rightCol.AddThemeConstantOverride("separation", 18);

            BuildAudioSection(leftCol);
            BuildIdentitySection(leftCol);

            BuildDisplaySection(rightCol);
            BuildCreditsSection(rightCol);

            columns.AddChild(leftCol);
            columns.AddChild(rightCol);
            column.AddChild(columns);

            var saveBtn = DoloWidgets.IconButton(DoloIcon.Check, "Save & Back",
                                                 DoloTheme.ButtonPrimary);
            saveBtn.Pressed += CloseAndSave;
            column.AddChild(saveBtn);

            card.AddChild(column);
            _optionsPanel.AddChild(card);
            AddChild(_optionsPanel);
        }

        private void BuildAudioSection(Container body)
        {
            body.AddChild(DoloWidgets.SectionHeading("Audio"));

            body.AddChild(MakeSliderRow(DoloIcon.Music, "Music", GameSettings.MusicVolume,
                                        out var musicSlider, out _musicPctLabel));
            musicSlider.ValueChanged += v =>
            {
                GameSettings.MusicVolume = (float)v;
                _musicPlayer.VolumeDb    = GameSettings.LinearToDb(GameSettings.MusicVolume);
                _musicPctLabel.Text      = $"{(int)(v * 100)}%";
            };

            body.AddChild(MakeSliderRow(DoloIcon.Speaker, "Effects", GameSettings.SfxVolume,
                                        out var sfxSlider, out _sfxPctLabel));
            sfxSlider.ValueChanged += v =>
            {
                GameSettings.SfxVolume = (float)v;
                _sfxPctLabel.Text      = $"{(int)(v * 100)}%";
                // Preview the new level with a tile clack.
                SoundManager.Instance?.Play(Sound.TileDiscard);
            };
        }

        private void BuildIdentitySection(Container body)
        {
            body.AddChild(DoloWidgets.SectionHeading("Identity"));
            body.AddChild(DoloWidgets.LabelledField(
                "Display name", "e.g. Dolo", GameSettings.PlayerName, 24, out _nameEdit));

            body.AddChild(DoloWidgets.SectionHeading("Server"));
            var urlSection = DoloWidgets.LabelledField(
                "Server URL", "wss://host/ws", GameSettings.ServerUrl, 200, out _urlEdit);

            // URLs read as mono at wide tracking, like room codes.
            _urlEdit.AddThemeFontOverride("font", DoloTheme.MonoFont);
            _urlEdit.AddThemeFontSizeOverride("font_size", DoloTokens.SizeBodySmall);
            body.AddChild(urlSection);
        }

        private void BuildDisplaySection(Container body)
        {
            body.AddChild(DoloWidgets.SectionHeading("Tile set"));
            body.AddChild(DoloWidgets.SegmentedToggle(
                new[] { (DoloIcon.Sun, "Regular"), (DoloIcon.Moon, "Black") },
                GameSettings.UseBlackTiles ? 1 : 0,
                index =>
                {
                    GameSettings.UseBlackTiles = index == 1;
                    RefreshTileSetButtons();
                },
                out _tileSetButtons));

            body.AddChild(DoloWidgets.SectionHeading("Layout"));
            body.AddChild(DoloWidgets.SegmentedToggle(
                new[]
                {
                    (DoloIcon.Gear,   "Auto"),
                    (DoloIcon.Tile,   "Desktop"),
                    (DoloIcon.Person, "Touch"),
                },
                (int)GameSettings.LayoutPreference,
                index =>
                {
                    GameSettings.LayoutPreference = (LayoutPreference)index;
                    DoloLayout.ResetCache();
                    RefreshLayoutButtons();
                },
                out _layoutButtons));

            var note = new Label
            {
                Text          = "Auto follows the device. Takes effect on the next game.",
                AutowrapMode  = TextServer.AutowrapMode.WordSmart,
            };
            note.ThemeTypeVariation = DoloTheme.Dim;
            body.AddChild(note);
        }

        /// <summary>
        /// Attribution for the open-licence prop sprites. The art budget was met by
        /// sourcing CC-licensed props rather than commissioning them, and that carries
        /// an attribution obligation the interface has to honour somewhere.
        /// </summary>
        private static void BuildCreditsSection(Container body)
        {
            body.AddChild(DoloWidgets.SectionHeading("Credits"));

            var credits = new Label
            {
                Text = "Tile art: riichi-mahjong-tiles. Prop sprites: open-licence "
                     + "sources, CC-BY. Type: Source Sans 3 and IBM Plex Mono, SIL OFL.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            credits.ThemeTypeVariation = DoloTheme.Dim;
            body.AddChild(credits);
        }

        private void CloseAndSave()
        {
            string name = _nameEdit.Text.Trim();
            GameSettings.PlayerName = name.Length == 0 ? "Player" : name;

            string url = _urlEdit.Text.Trim();
            if (url.Length > 0) GameSettings.ServerUrl = url;

            GameSettings.Save();

            _optionsPanel.Visible = false;
            _centrePanel.Visible  = true;
        }

        private void RefreshTileSetButtons()
            => DoloWidgets.ApplySegmentedSelection(_tileSetButtons,
                                                   GameSettings.UseBlackTiles ? 1 : 0);

        private void RefreshLayoutButtons()
            => DoloWidgets.ApplySegmentedSelection(_layoutButtons,
                                                   (int)GameSettings.LayoutPreference);

        /// <summary>Icon, label, slider and a mono percentage that keeps its column.</summary>
        private static Control MakeSliderRow(DoloIcon icon, string labelText, float initial,
                                             out HSlider slider, out Label pctLabel)
        {
            var section = new VBoxContainer();
            section.AddThemeConstantOverride("separation", 8);

            var header = new HBoxContainer();
            header.AddThemeConstantOverride("separation", 10);
            header.AddChild(new DoloIconRect(icon, 18, DoloTokens.BodyText));

            var label = new Label { Text = labelText, VerticalAlignment = VerticalAlignment.Center };
            label.ThemeTypeVariation  = DoloTheme.Row;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            header.AddChild(label);

            pctLabel = new Label { Text = $"{(int)(initial * 100)}%" };
            pctLabel.ThemeTypeVariation  = DoloTheme.Mono;
            pctLabel.HorizontalAlignment = HorizontalAlignment.Right;
            pctLabel.CustomMinimumSize   = new Vector2(52, 0);
            header.AddChild(pctLabel);

            slider = new HSlider
            {
                MinValue            = 0.0,
                MaxValue            = 1.0,
                Step                = 0.01,
                Value               = initial,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize   = new Vector2(0, 28),
            };

            section.AddChild(header);
            section.AddChild(slider);
            return section;
        }
    }
}
