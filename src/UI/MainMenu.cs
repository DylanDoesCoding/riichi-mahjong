// =============================================================================
// MainMenu.cs — Start screen with tile-theme selector and options panel.
//
// The options panel is built entirely in code as a full-screen overlay.
// It exposes Music and SFX volume sliders that update GameSettings in real-time.
// Background music starts here and stops when the game scene loads.
// =============================================================================

using Godot;
using System.Collections.Generic;

namespace RiichiMahjong.UI
{
    public partial class MainMenu : Control
    {
        // ---- Scene nodes (from .tscn) ----------------------------------------
        private Button _regularBtn    = null!;
        private Button _blackBtn      = null!;
        private Button _quickPlayBtn  = null!;
        private Control _centrePanel  = null!;

        // ---- Options overlay (built in code) ---------------------------------
        private Control _optionsPanel   = null!;
        private Label   _musicPctLabel  = null!;
        private Label   _sfxPctLabel    = null!;

        // ---- Quick Play state -----------------------------------------------
        private bool _quickPlayActive = false;

        // ---- Audio -----------------------------------------------------------
        private AudioStreamPlayer _musicPlayer = null!;
        private AudioStreamPlayer _sfxPreview  = null!;   // Plays a clack on SFX slider change

        // ---- Theme colours ---------------------------------------------------
        private static readonly Color ActiveBg   = new(0.20f, 0.55f, 0.90f);
        private static readonly Color InactiveBg = new(0.25f, 0.25f, 0.30f);

        // ---- Paths -----------------------------------------------------------
        private const string MusicPath    = "res://Assets/Sounds/Whispering_Bamboo_Garden_2026-05-18T203144.wav";
        private const string ClackPath    = "res://Assets/Sounds/mahjong_tile_clack_#1-1779136185604.wav";

        // =====================================================================
        // Lifecycle
        // =====================================================================

        public override void _Ready()
        {
            // ---- Wire existing scene buttons ---------------------------------
            _centrePanel = GetNode<Control>("CentrePanel");
            var playBtn         = GetNode<Button>("CentrePanel/PlayButton");
            var multiplayerBtn  = GetNode<Button>("CentrePanel/MultiplayerButton");
            var optionsBtn      = GetNode<Button>("CentrePanel/OptionsButton");
            var quitBtn         = GetNode<Button>("CentrePanel/QuitButton");
            _regularBtn         = GetNode<Button>("CentrePanel/ThemeRow/RegularButton");
            _blackBtn           = GetNode<Button>("CentrePanel/ThemeRow/BlackButton");
            _quickPlayBtn       = GetNode<Button>("CentrePanel/QuickPlayButton");

            playBtn.Pressed        += OnPlayPressed;
            multiplayerBtn.Pressed += OnMultiplayerPressed;
            optionsBtn.Pressed     += OnOptionsPressed;
            quitBtn.Pressed        += OnQuitPressed;
            _regularBtn.Pressed    += OnRegularPressed;
            _blackBtn.Pressed      += OnBlackPressed;
            _quickPlayBtn.Pressed  += OnQuickPlayPressed;

            RefreshThemeButtons();

            // ---- Build options panel (hidden by default) ---------------------
            BuildOptionsPanel();

            // ---- Background music -------------------------------------------
            _musicPlayer = new AudioStreamPlayer();
            _musicPlayer.Bus = "Master";
            AddChild(_musicPlayer);

            var music = GD.Load<AudioStream>(MusicPath);
            if (music != null)
            {
                _musicPlayer.Stream     = music;
                _musicPlayer.VolumeDb   = GameSettings.LinearToDb(GameSettings.MusicVolume);
                _musicPlayer.Autoplay   = false;
                _musicPlayer.Finished   += () => _musicPlayer.Play();
                _musicPlayer.Play();
            }

            // SFX preview player (one-shot, for slider feedback)
            _sfxPreview = new AudioStreamPlayer();
            _sfxPreview.Bus = "Master";
            AddChild(_sfxPreview);
            var clack = GD.Load<AudioStream>(ClackPath);
            if (clack != null)
                _sfxPreview.Stream = clack;
        }

        public override void _ExitTree()
        {
            UnwireQuickPlay();
        }

        // =====================================================================
        // Scene-button handlers
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

        private void OnQuickPlayPressed()
        {
            if (_quickPlayActive) return;

            var nm = NetworkManager.Instance;
            if (nm == null) return;

            var url = GameSettings.ServerUrl.Length > 0 ? GameSettings.ServerUrl : "ws://localhost:5000/ws";
            if (GameSettings.PlayerName.Length == 0) GameSettings.PlayerName = "Player";

            _quickPlayActive       = true;
            _quickPlayBtn.Disabled = true;
            _quickPlayBtn.Text     = "Connecting…";

            nm.OnConnected   += OnQuickPlayConnected;
            nm.OnRoomCreated += OnQuickPlayRoomCreated;
            nm.OnGameStarted += OnQuickPlayGameStarted;
            nm.OnError       += OnQuickPlayError;

            nm.Connect(url);
        }

        private void OnQuickPlayConnected()
        {
            NetworkManager.Instance?.CreateRoom(GameSettings.PlayerName);
        }

        private void OnQuickPlayRoomCreated(string code, int seat, List<NetPlayerInfo> players)
        {
            NetworkManager.Instance?.StartGame();
        }

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
            _quickPlayBtn.Text     = "⚡  Quick Play";
            UnwireQuickPlay();
            NetworkManager.Instance?.ResetSession();
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

        private void OnQuitPressed() => GetTree().Quit();

        private void OnRegularPressed() { GameSettings.UseBlackTiles = false; RefreshThemeButtons(); }
        private void OnBlackPressed()   { GameSettings.UseBlackTiles = true;  RefreshThemeButtons(); }

        private void OnOptionsPressed()
        {
            _centrePanel.Visible  = false;
            _optionsPanel.Visible = true;
        }

        // =====================================================================
        // Options panel construction
        // =====================================================================

        private void BuildOptionsPanel()
        {
            // Full-screen overlay — same dark background as the menu
            _optionsPanel = new Control();
            _optionsPanel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _optionsPanel.Visible = false;

            var bg = new ColorRect();
            bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            bg.Color = new Color(0.08f, 0.10f, 0.15f, 1f);
            _optionsPanel.AddChild(bg);

            // Centred card
            var card = new PanelContainer();
            card.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
            card.OffsetLeft   = -240;
            card.OffsetTop    = -220;
            card.OffsetRight  =  240;
            card.OffsetBottom =  220;

            var cardStyle = new StyleBoxFlat();
            cardStyle.BgColor     = new Color(0.10f, 0.12f, 0.18f, 1f);
            cardStyle.BorderColor = new Color(0.30f, 0.45f, 0.70f, 1f);
            cardStyle.BorderWidthTop    = cardStyle.BorderWidthBottom =
            cardStyle.BorderWidthLeft   = cardStyle.BorderWidthRight  = 2;
            cardStyle.CornerRadiusTopLeft    = cardStyle.CornerRadiusTopRight    =
            cardStyle.CornerRadiusBottomLeft = cardStyle.CornerRadiusBottomRight = 10;
            card.AddThemeStyleboxOverride("panel", cardStyle);

            var vbox = new VBoxContainer();
            vbox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            vbox.OffsetLeft   = 28;
            vbox.OffsetTop    = 24;
            vbox.OffsetRight  = -28;
            vbox.OffsetBottom = -24;
            vbox.AddThemeConstantOverride("separation", 12);

            // Title
            var title = new Label { Text = "⚙  Options" };
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.AddThemeFontSizeOverride("font_size", 26);
            title.AddThemeColorOverride("font_color", new Color(0.85f, 0.90f, 1f));
            vbox.AddChild(title);
            vbox.AddChild(new HSeparator());

            // Music volume row
            vbox.AddChild(MakeSliderSection(
                "🎵  Music Volume",
                GameSettings.MusicVolume,
                out var musicSlider,
                out _musicPctLabel));

            musicSlider.ValueChanged += v =>
            {
                GameSettings.MusicVolume   = (float)v;
                _musicPlayer.VolumeDb      = GameSettings.LinearToDb(GameSettings.MusicVolume);
                _musicPctLabel.Text        = $"{(int)(v * 100)} %";
            };

            vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });

            // SFX volume row
            vbox.AddChild(MakeSliderSection(
                "🔊  SFX Volume",
                GameSettings.SfxVolume,
                out var sfxSlider,
                out _sfxPctLabel));

            sfxSlider.ValueChanged += v =>
            {
                GameSettings.SfxVolume  = (float)v;
                _sfxPctLabel.Text       = $"{(int)(v * 100)} %";

                // Play a clack preview so the player can hear the new level
                if (_sfxPreview.Stream != null)
                {
                    _sfxPreview.VolumeDb = GameSettings.LinearToDb(GameSettings.SfxVolume);
                    if (!_sfxPreview.Playing)
                        _sfxPreview.Play();
                }
            };

            vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 16) });
            vbox.AddChild(new HSeparator());
            vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });

            // Back button
            var backBtn = MakeOptionButton("← Back to Menu", new Color(0.22f, 0.35f, 0.55f));
            backBtn.Pressed += () =>
            {
                _optionsPanel.Visible = false;
                _centrePanel.Visible  = true;
            };
            vbox.AddChild(backBtn);

            card.AddChild(vbox);
            _optionsPanel.AddChild(card);
            AddChild(_optionsPanel);
        }

        /// <summary>Build a label + slider + percentage label row and return the slider.</summary>
        private static Control MakeSliderSection(string labelText, float initialValue,
            out HSlider slider, out Label pctLabel)
        {
            var section = new VBoxContainer();
            section.AddThemeConstantOverride("separation", 6);

            var lbl = new Label { Text = labelText };
            lbl.AddThemeFontSizeOverride("font_size", 16);
            lbl.AddThemeColorOverride("font_color", new Color(0.85f, 0.90f, 1f));
            section.AddChild(lbl);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);

            slider = new HSlider();
            slider.MinValue              = 0.0;
            slider.MaxValue              = 1.0;
            slider.Step                  = 0.01;
            slider.Value                 = initialValue;
            slider.SizeFlagsHorizontal   = SizeFlags.ExpandFill;
            slider.CustomMinimumSize     = new Vector2(0, 28);

            pctLabel = new Label { Text = $"{(int)(initialValue * 100)} %" };
            pctLabel.CustomMinimumSize   = new Vector2(50, 0);
            pctLabel.HorizontalAlignment = HorizontalAlignment.Right;
            pctLabel.AddThemeFontSizeOverride("font_size", 15);
            pctLabel.AddThemeColorOverride("font_color", new Color(0.75f, 0.85f, 1f));

            row.AddChild(slider);
            row.AddChild(pctLabel);
            section.AddChild(row);

            return section;
        }

        private static Button MakeOptionButton(string text, Color color)
        {
            var btn = new Button { Text = text };
            btn.CustomMinimumSize = new Vector2(0, 48);
            btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            btn.AddThemeFontSizeOverride("font_size", 17);

            var style = new StyleBoxFlat();
            style.BgColor              = color;
            style.CornerRadiusTopLeft  = style.CornerRadiusTopRight  =
            style.CornerRadiusBottomLeft = style.CornerRadiusBottomRight = 6;
            btn.AddThemeStyleboxOverride("normal", style);

            var hover = (StyleBoxFlat)style.Duplicate();
            hover.BgColor = color.Lightened(0.2f);
            btn.AddThemeStyleboxOverride("hover", hover);

            btn.AddThemeColorOverride("font_color", Colors.White);
            return btn;
        }

        // =====================================================================
        // Theme button helpers
        // =====================================================================

        private void RefreshThemeButtons()
        {
            StyleButton(_regularBtn, !GameSettings.UseBlackTiles);
            StyleButton(_blackBtn,    GameSettings.UseBlackTiles);
        }

        private static void StyleButton(Button btn, bool active)
        {
            var style = new StyleBoxFlat();
            style.BgColor              = active ? ActiveBg : InactiveBg;
            style.CornerRadiusTopLeft  = style.CornerRadiusTopRight  =
            style.CornerRadiusBottomLeft = style.CornerRadiusBottomRight = 6;
            if (active)
            {
                style.BorderColor     = new Color(0.60f, 0.85f, 1.00f);
                style.BorderWidthTop  = style.BorderWidthBottom =
                style.BorderWidthLeft = style.BorderWidthRight  = 2;
            }
            btn.AddThemeStyleboxOverride("normal", style);

            var hover = (StyleBoxFlat)style.Duplicate();
            hover.BgColor = active ? ActiveBg.Lightened(0.1f) : InactiveBg.Lightened(0.1f);
            btn.AddThemeStyleboxOverride("hover", hover);

            btn.AddThemeColorOverride("font_color", Colors.White);
        }
    }
}
