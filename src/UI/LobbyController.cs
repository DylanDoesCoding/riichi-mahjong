// =============================================================================
// LobbyController.cs
// Multiplayer lobby.
//
// Pass 06 splits what used to be one 440 x 660 card into four, each shown on
// its own by ShowCard:
//
//   _connectPanel    — play online: the three ways to start a game, an identity
//                      strip, and whether the server is reachable
//   _accountPanel    — sign in, register, forgot password, manage account
//   _waitingPanel    — room code, four player slots, Start / Leave
//   _searchingPanel  — matchmaking, with one way out
//
// The old card held all of that at once, with two sub-panels toggling open
// inside it and silently changing its height. Server URL has moved out of the
// lobby entirely and into Settings.
//
// NetworkManager is an autoload that persists across scenes.
// =============================================================================

using Godot;
using System.Collections.Generic;
using RiichiMahjong.UI;

namespace RiichiMahjong.UI
{
    public partial class LobbyController : Control
    {
        // ---- Panels ----------------------------------------------------------
        // The four cards of pass 06, plus the leaderboard. Exactly one is visible at a
        // time, which is what ShowCard enforces — the old lobby toggled sub-panels open
        // inside a single card and silently changed its height.
        private Control _connectPanel     = null!;   // play online
        private Control _accountPanel     = null!;
        private Control _waitingPanel     = null!;
        private Control _searchingPanel   = null!;
        private Control _leaderboardPanel = null!;

        private readonly List<Control> _cards = new();

        // Identity strip on the play-online card
        private Label  _identityLabel     = null!;
        private Button _accountBtn        = null!;
        private Panel  _serverDot         = null!;
        private Label  _serverStatusLabel = null!;

        // ---- Leaderboard widgets ----------------------------------------------
        private VBoxContainer _leaderboardRows   = null!;
        private Label         _leaderboardStatus = null!;

        // ---- Connect panel widgets -------------------------------------------
        private LineEdit _nameInput     = null!;
        private LineEdit _joinCodeInput = null!;
        private Label   _connectStatus = null!;

        // ---- Account widgets ---------------------------------------------------
        private Control  _accountForm    = null!;  // username/password + Sign In / Register
        private Control  _loggedInBox    = null!;  // "Signed in as X" + Manage + Sign Out
        private LineEdit _accUserInput   = null!;
        private LineEdit _accPassInput   = null!;
        private Label    _loggedInLabel  = null!;

        // Manage-account sub-panel (visible while signed in, toggled by Manage)
        private Control  _manageBox        = null!;
        private LineEdit _emailInput       = null!;
        private LineEdit _oldPassInput     = null!;
        private LineEdit _newPassInput     = null!;

        // Forgot-password sub-panel (toggled from the login form)
        private Control  _resetBox         = null!;
        private LineEdit _resetCodeInput   = null!;
        private LineEdit _resetNewPassInput = null!;

        // Send waiting for the WebSocket handshake to finish (auth/manage actions)
        private System.Action? _pendingSend;

        // ---- Waiting panel widgets -------------------------------------------
        private Label           _roomCodeLabel    = null!;
        private Label           _waitingStatus    = null!;
        private Label[]         _playerNameLabels = new Label[4];
        private Label[]         _playerTagLabels  = new Label[4];
        private Button          _startBtn         = null!;
        private Button          _copyBtn          = null!;

        // ---- Audio -----------------------------------------------------------
        private AudioStreamPlayer _lobbyMusic = null!;

        // ---- State -----------------------------------------------------------
        private bool   _isHost             = false;
        private string _reconnectCode      = "";  // room code to rejoin after disconnect
        private string _reconnectServerUrl = "";
        private bool   _isSearching        = false;

        // ---- Searching-state widgets (shown inside connect panel while in queue) ---
        private Control  _searchingOverlay = null!;
        private Label    _searchingLabel   = null!;

        // ---- Style colours ---------------------------------------------------
        private static readonly Color BgColor   = DoloTokens.Page;
        private static readonly Color TextColor = DoloTokens.Ivory;
        private static readonly Color DimText   = DoloTokens.DimText;

        // =====================================================================
        // Lifecycle
        // =====================================================================

        public override void _Ready()
        {
            // Dark full-screen background
            var bg = new ColorRect();
            bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            bg.Color = BgColor;
            AddChild(bg);

            // Background music (same track as menu, loops)
            _lobbyMusic = new AudioStreamPlayer { Bus = "Master" };
            AddChild(_lobbyMusic);
            var music = GD.Load<AudioStream>(
                "res://Assets/Sounds/Whispering_Bamboo_Garden_2026-05-18T203144.wav");
            if (music != null)
            {
                _lobbyMusic.Stream   = music;
                _lobbyMusic.VolumeDb = GameSettings.LinearToDb(GameSettings.MusicVolume);
                _lobbyMusic.Finished += () => _lobbyMusic.Play();
                _lobbyMusic.Play();
            }

            BuildConnectPanel();
            BuildAccountCard();
            BuildWaitingPanel();
            BuildSearchingCard();
            BuildLeaderboardPanel();

            ShowConnect();

            // Wire NetworkManager events
            var nm = NetworkManager.Instance;
            if (nm == null) return;

            nm.OnRoomCreated  += HandleRoomCreated;
            nm.OnRoomJoined   += HandleRoomJoined;
            nm.OnPlayerJoined += HandlePlayerListUpdated;
            nm.OnPlayerLeft   += HandlePlayerLeft;
            nm.OnError        += HandleError;
            nm.OnGameStarted  += HandleGameStarted;
            nm.OnConnected    += HandleConnected;
            nm.OnDisconnected += HandleDisconnected;
            nm.OnQueueJoined     += HandleQueueJoined;
            nm.OnAuthOk          += HandleAuthOk;
            nm.OnAccountMessage  += HandleAccountMessage;
            nm.OnLeaderboard     += HandleLeaderboard;
        }

        public override void _ExitTree()
        {
            _lobbyMusic?.Stop();

            var nm = NetworkManager.Instance;
            if (nm == null) return;

            nm.OnRoomCreated  -= HandleRoomCreated;
            nm.OnRoomJoined   -= HandleRoomJoined;
            nm.OnPlayerJoined -= HandlePlayerListUpdated;
            nm.OnPlayerLeft   -= HandlePlayerLeft;
            nm.OnError        -= HandleError;
            nm.OnGameStarted  -= HandleGameStarted;
            nm.OnConnected    -= HandleConnected;
            nm.OnDisconnected -= HandleDisconnected;
            nm.OnQueueJoined     -= HandleQueueJoined;
            nm.OnAuthOk          -= HandleAuthOk;
            nm.OnAccountMessage  -= HandleAccountMessage;
            nm.OnLeaderboard     -= HandleLeaderboard;
        }

        // =====================================================================
        // Panel builders
        // =====================================================================

        /// <summary>
        /// Card one: the only three ways to start a game, plus who you are and whether
        /// the server is reachable.
        ///
        /// The old single card held all of that plus sign-in, register, forgot-password,
        /// manage-account, the searching state and the leaderboard, with two sub-panels
        /// toggling open inside it. Everything that is not "start a game" now lives on
        /// its own card.
        /// </summary>
        private void BuildConnectPanel()
        {
            _connectPanel = MakeCard(offsetV: 0, offsetH: 0, height: 604, width: 460);

            var vbox = MakeCardVBox(_connectPanel);

            var title = new Label { Text = "PLAY ONLINE" };
            title.ThemeTypeVariation = DoloTheme.NameSmall;
            vbox.AddChild(title);
            vbox.AddChild(DoloStyles.HairlineRow(0.28f));

            vbox.AddChild(BuildIdentityStrip());

            // ---- The three ways in ----
            var quickPlayBtn = MakeButton("Quick Play", DoloTheme.ButtonPrimary,
                                          icon: DoloIcon.Bolt);
            quickPlayBtn.Pressed += OnQuickPlay;
            vbox.AddChild(quickPlayBtn);

            var createBtn = MakeButton("Create Room", icon: DoloIcon.Plus);
            createBtn.Pressed += OnCreateRoom;
            vbox.AddChild(createBtn);

            vbox.AddChild(MakeLabel("Join with a room code"));
            var joinRow = new HBoxContainer();
            joinRow.AddThemeConstantOverride("separation", 10);
            _joinCodeInput = MakeCodeEdit("XXXXXX", 6);
            var joinBtn = MakeButton("Join", minWidth: 110);
            joinBtn.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            joinBtn.Pressed += OnJoinRoom;
            joinRow.AddChild(_joinCodeInput);
            joinRow.AddChild(joinBtn);
            vbox.AddChild(joinRow);

            // ---- Status ----
            _connectStatus = new Label { Text = "" };
            _connectStatus.ThemeTypeVariation = DoloTheme.Dim;
            _connectStatus.AutowrapMode       = TextServer.AutowrapMode.WordSmart;
            _connectStatus.SizeFlagsVertical  = SizeFlags.ExpandFill;
            vbox.AddChild(_connectStatus);

            // ---- Leave ----
            var bottomRow = new HBoxContainer();
            bottomRow.AddThemeConstantOverride("separation", 10);

            var backBtn = MakeButton("Menu", DoloTheme.ButtonGhost, icon: DoloIcon.Back);
            backBtn.Pressed += () =>
            {
                if (_isSearching) NetworkManager.Instance?.SendLeaveQueue();
                NetworkManager.Instance?.ResetSession();
                GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
            };

            var lbBtn = MakeButton("Leaderboard", DoloTheme.ButtonGhost, icon: DoloIcon.Trophy);
            lbBtn.Pressed += OnLeaderboardPressed;

            bottomRow.AddChild(backBtn);
            bottomRow.AddChild(lbBtn);
            vbox.AddChild(bottomRow);
        }

        /// <summary>
        /// Who you are and whether the server is up: display name, the account state,
        /// and a status dot. This replaces the sign-in block that used to sit in the
        /// middle of the card between the fields and the play buttons.
        /// </summary>
        private Control BuildIdentityStrip()
        {
            var strip = new PanelContainer();
            strip.AddThemeStyleboxOverride("panel", DoloStyles.Inset(14));

            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 8);

            var topRow = new HBoxContainer();
            topRow.AddThemeConstantOverride("separation", 10);
            topRow.AddChild(new DoloIconRect(DoloIcon.Person, 18, DoloTokens.BodyText));

            _identityLabel = new Label { Text = "Playing as guest" };
            _identityLabel.ThemeTypeVariation  = DoloTheme.Row;
            _identityLabel.VerticalAlignment   = VerticalAlignment.Center;
            _identityLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            topRow.AddChild(_identityLabel);

            _accountBtn = MakeButton("Account", DoloTheme.ButtonGhost, minWidth: 110);
            _accountBtn.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            _accountBtn.Pressed += ShowAccount;
            topRow.AddChild(_accountBtn);

            column.AddChild(topRow);

            _nameInput = MakeLineEdit("Your display name", GameSettings.PlayerName);
            column.AddChild(_nameInput);

            // Server reachability, as a dot plus the word — never the dot alone.
            var serverRow = new HBoxContainer();
            serverRow.AddThemeConstantOverride("separation", 8);

            _serverDot = new Panel
            {
                CustomMinimumSize = new Vector2(10, 10),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                MouseFilter       = MouseFilterEnum.Ignore,
            };
            serverRow.AddChild(_serverDot);

            _serverStatusLabel = new Label { Text = "SERVER UNKNOWN" };
            _serverStatusLabel.ThemeTypeVariation = DoloTheme.Mono;
            _serverStatusLabel.VerticalAlignment  = VerticalAlignment.Center;
            serverRow.AddChild(_serverStatusLabel);

            column.AddChild(serverRow);
            strip.AddChild(column);

            SetServerStatus(false, "unknown");
            return strip;
        }

        /// <summary>
        /// Paint the server dot and its label together. The label is what carries the
        /// meaning; the dot is a second channel, not the only one.
        /// </summary>
        private void SetServerStatus(bool ok, string text)
        {
            var tint = ok ? DoloTokens.Positive : DoloTokens.DimText;
            _serverDot.AddThemeStyleboxOverride("panel",
                DoloStyles.Flat(tint, 5, DoloTokens.Hairline(0.4f), borderWidth: 1));
            _serverStatusLabel.Text = $"SERVER {text.ToUpperInvariant()}";
            _serverStatusLabel.AddThemeColorOverride("font_color", tint);
        }

        private void BuildLeaderboardPanel()
        {
            _leaderboardPanel = MakeCard(offsetV: -300, offsetH: -270, height: 600, width: 540);
            _leaderboardPanel.Visible = false;

            var vbox = MakeCardVBox(_leaderboardPanel);

            var title = new Label { Text = "🏆  Leaderboard" };
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.AddThemeFontSizeOverride("font_size", 26);
            title.AddThemeColorOverride("font_color", TextColor);
            vbox.AddChild(title);
            vbox.AddChild(new HSeparator());
            vbox.AddChild(Spacer(6));

            // Column header
            vbox.AddChild(MakeLeaderboardRow("#", "Player", "Wins", "Games", "Win %", "Points",
                DimText, bold: true));
            vbox.AddChild(Spacer(2));

            // Scrollable rows
            var scroll = new ScrollContainer();
            scroll.SizeFlagsVertical   = SizeFlags.ExpandFill;
            scroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;

            _leaderboardRows = new VBoxContainer();
            _leaderboardRows.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _leaderboardRows.AddThemeConstantOverride("separation", 3);
            scroll.AddChild(_leaderboardRows);
            vbox.AddChild(scroll);

            _leaderboardStatus = new Label { Text = "" };
            _leaderboardStatus.HorizontalAlignment = HorizontalAlignment.Center;
            _leaderboardStatus.AddThemeFontSizeOverride("font_size", 14);
            _leaderboardStatus.AddThemeColorOverride("font_color", DimText);
            vbox.AddChild(_leaderboardStatus);

            vbox.AddChild(Spacer(4));
            var closeBtn = MakeButton("Back", DoloTheme.ButtonGhost, icon: DoloIcon.Back);
            closeBtn.Pressed += () => { ShowConnect(); };
            vbox.AddChild(closeBtn);
        }

        /// <summary>One fixed-column leaderboard row.</summary>
        private Control MakeLeaderboardRow(string rank, string name, string wins,
            string games, string rate, string points, Color color, bool bold = false)
        {
            var row = new HBoxContainer();
            row.SizeFlagsHorizontal = SizeFlags.ExpandFill;

            Label Cell(string text, int minWidth, HorizontalAlignment align, bool expand = false)
            {
                var lbl = new Label { Text = text };
                lbl.AddThemeFontSizeOverride("font_size", bold ? 14 : 15);
                lbl.AddThemeColorOverride("font_color", color);
                lbl.HorizontalAlignment = align;
                lbl.CustomMinimumSize   = new Vector2(minWidth, 0);
                if (expand) lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                lbl.ClipText = true;
                row.AddChild(lbl);
                return lbl;
            }

            Cell(rank,   34,  HorizontalAlignment.Left);
            Cell(name,   150, HorizontalAlignment.Left, expand: true);
            Cell(wins,   52,  HorizontalAlignment.Right);
            Cell(games,  56,  HorizontalAlignment.Right);
            Cell(rate,   62,  HorizontalAlignment.Right);
            Cell(points, 90,  HorizontalAlignment.Right);
            return row;
        }

        /// <summary>
        /// Card two: everything to do with an account. Sign-in is optional by design and
        /// stays optional, so this is reached from the identity strip rather than sitting
        /// in the path of someone who just wants to play.
        /// </summary>
        private void BuildAccountCard()
        {
            _accountPanel = MakeCard(offsetV: 0, offsetH: 0, height: 620, width: 480);
            var vbox = MakeCardVBox(_accountPanel);

            var title = new Label { Text = "ACCOUNT" };
            title.ThemeTypeVariation = DoloTheme.NameSmall;
            vbox.AddChild(title);
            vbox.AddChild(DoloStyles.HairlineRow(0.28f));

            var note = new Label
            {
                Text         = "Optional. An account keeps your name and your record "
                             + "across devices; guests can play without one.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            note.ThemeTypeVariation = DoloTheme.Dim;
            vbox.AddChild(note);

            BuildAccountSection(vbox);

            var backBtn = MakeButton("Back", DoloTheme.ButtonGhost, icon: DoloIcon.Back);
            backBtn.Pressed += ShowConnect;
            vbox.AddChild(backBtn);
        }

        /// <summary>
        /// Card four: the searching state. It was a sub-box inside the connect card that
        /// left every form widget behind it live; now it is a card of its own with one
        /// thing on it and one way out.
        /// </summary>
        private void BuildSearchingCard()
        {
            _searchingPanel = MakeCard(offsetV: 0, offsetH: 0, height: 300, width: 460);
            var vbox = MakeCardVBox(_searchingPanel);
            vbox.Alignment = BoxContainer.AlignmentMode.Center;

            var title = new Label { Text = "SEARCHING" };
            title.ThemeTypeVariation  = DoloTheme.NameSmall;
            title.HorizontalAlignment = HorizontalAlignment.Center;
            vbox.AddChild(title);

            _searchingLabel = new Label { Text = "Looking for players…" };
            _searchingLabel.ThemeTypeVariation  = DoloTheme.Row;
            _searchingLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _searchingLabel.AutowrapMode        = TextServer.AutowrapMode.WordSmart;
            vbox.AddChild(_searchingLabel);

            var hint = new Label
            {
                Text                = "Up to 30 seconds, then CPU players fill the empty seats.",
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode        = TextServer.AutowrapMode.WordSmart,
            };
            hint.ThemeTypeVariation = DoloTheme.Dim;
            vbox.AddChild(hint);

            var cancelBtn = MakeButton("Cancel Search", DoloTheme.ButtonGhost,
                                       icon: DoloIcon.Close);
            cancelBtn.Pressed += OnCancelQueue;
            vbox.AddChild(cancelBtn);

            _searchingOverlay = _searchingPanel;
        }

        private void BuildAccountSection(VBoxContainer vbox)
        {
            // ---- Logged-out form: username + password, Sign In / Register ----
            var form = new VBoxContainer();
            form.AddThemeConstantOverride("separation", 6);

            var credRow = new HBoxContainer();
            credRow.AddThemeConstantOverride("separation", 8);
            _accUserInput = MakeLineEdit("Username");
            _accPassInput = MakeLineEdit("Password");
            _accPassInput.Secret = true;
            credRow.AddChild(_accUserInput);
            credRow.AddChild(_accPassInput);
            form.AddChild(credRow);

            var btnRow = new HBoxContainer();
            btnRow.AddThemeConstantOverride("separation", 8);
            var loginBtn    = MakeButton("Sign In", DoloTheme.ButtonPrimary);
            var registerBtn = MakeButton("Register");
            var forgotBtn   = MakeButton("Forgot?", DoloTheme.ButtonGhost, minWidth: 110);
            forgotBtn.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            loginBtn.Pressed    += () => StartAuth("login");
            registerBtn.Pressed += () => StartAuth("register");
            forgotBtn.Pressed   += () => { _resetBox.Visible = !_resetBox.Visible; };
            btnRow.AddChild(loginBtn);
            btnRow.AddChild(registerBtn);
            btnRow.AddChild(forgotBtn);
            form.AddChild(btnRow);

            // ---- Forgot-password flow (hidden until "Forgot?" is pressed) ----
            var resetBox = new VBoxContainer();
            resetBox.AddThemeConstantOverride("separation", 6);
            resetBox.Visible = false;

            var resetHint = MakeLabel("Enter your username above, then request a code:");
            resetBox.AddChild(resetHint);

            var sendCodeBtn = MakeButton("Send Reset Code");
            sendCodeBtn.Pressed += OnRequestReset;
            resetBox.AddChild(sendCodeBtn);

            var resetRow = new HBoxContainer();
            resetRow.AddThemeConstantOverride("separation", 8);
            _resetCodeInput    = MakeLineEdit("6-digit code");
            _resetCodeInput.MaxLength = 6;
            _resetNewPassInput = MakeLineEdit("New password");
            _resetNewPassInput.Secret = true;
            resetRow.AddChild(_resetCodeInput);
            resetRow.AddChild(_resetNewPassInput);
            resetBox.AddChild(resetRow);

            var doResetBtn = MakeButton("Reset Password", DoloTheme.ButtonPrimary);
            doResetBtn.Pressed += OnResetPassword;
            resetBox.AddChild(doResetBtn);

            _resetBox = resetBox;
            form.AddChild(resetBox);

            _accountForm = form;
            vbox.AddChild(form);

            // ---- Logged-in view: status + Sign Out ----
            var inBox = new HBoxContainer();
            inBox.AddThemeConstantOverride("separation", 8);

            _loggedInLabel = new Label();
            _loggedInLabel.AddThemeFontSizeOverride("font_size", 15);
            _loggedInLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.90f, 0.65f));
            _loggedInLabel.VerticalAlignment = VerticalAlignment.Center;
            _loggedInLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            inBox.AddChild(_loggedInLabel);

            var manageBtn = MakeButton("Manage", DoloTheme.ButtonGhost, minWidth: 120);
            manageBtn.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            manageBtn.Pressed += () => { _manageBox.Visible = !_manageBox.Visible; };
            inBox.AddChild(manageBtn);

            var logoutBtn = MakeButton("Sign Out", DoloTheme.ButtonGhost, minWidth: 120);
            logoutBtn.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            logoutBtn.Pressed += OnLogout;
            inBox.AddChild(logoutBtn);

            _loggedInBox = inBox;
            vbox.AddChild(inBox);

            // ---- Manage-account sub-panel (hidden until "Manage" is pressed) ----
            var manageBox = new VBoxContainer();
            manageBox.AddThemeConstantOverride("separation", 6);
            manageBox.Visible = false;

            var emailRow = new HBoxContainer();
            emailRow.AddThemeConstantOverride("separation", 8);
            _emailInput = MakeLineEdit("Recovery email");
            var setEmailBtn = MakeButton("Set Email", minWidth: 130);
            setEmailBtn.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            setEmailBtn.Pressed += OnSetEmail;
            emailRow.AddChild(_emailInput);
            emailRow.AddChild(setEmailBtn);
            manageBox.AddChild(emailRow);

            var passRow = new HBoxContainer();
            passRow.AddThemeConstantOverride("separation", 8);
            _oldPassInput = MakeLineEdit("Current password");
            _oldPassInput.Secret = true;
            _newPassInput = MakeLineEdit("New password");
            _newPassInput.Secret = true;
            passRow.AddChild(_oldPassInput);
            passRow.AddChild(_newPassInput);
            manageBox.AddChild(passRow);

            var changePassBtn = MakeButton("Change Password");
            changePassBtn.Pressed += OnChangePassword;
            manageBox.AddChild(changePassBtn);

            _manageBox = manageBox;
            vbox.AddChild(manageBox);

            UpdateAccountUi();
        }

        private void BuildWaitingPanel()
        {
            _waitingPanel = MakeCard(offsetV: -280, offsetH: -240, height: 560, width: 480);
            _waitingPanel.Visible = false;

            var vbox = MakeCardVBox(_waitingPanel);

            // Title
            var title = new Label { Text = "🎮  Lobby" };
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.AddThemeFontSizeOverride("font_size", 26);
            title.AddThemeColorOverride("font_color", TextColor);
            vbox.AddChild(title);
            vbox.AddChild(new HSeparator());
            vbox.AddChild(Spacer(8));

            // Room code row
            var codeRow = new HBoxContainer();
            codeRow.AddThemeConstantOverride("separation", 10);

            var codeBox = new VBoxContainer();
            var codeLbl = MakeLabel("Room Code");
            _roomCodeLabel = new Label { Text = "------" };
            _roomCodeLabel.AddThemeFontSizeOverride("font_size", 32);
            _roomCodeLabel.AddThemeColorOverride("font_color", new Color(0.60f, 0.85f, 1f));
            codeBox.AddChild(codeLbl);
            codeBox.AddChild(_roomCodeLabel);

            _copyBtn = MakeButton("Copy", minWidth: 110);
            _copyBtn.CustomMinimumSize = new Vector2(90, 52);
            _copyBtn.Pressed += OnCopyCode;

            codeRow.AddChild(codeBox);
            codeRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            codeBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            codeRow.AddChild(_copyBtn);
            vbox.AddChild(codeRow);
            vbox.AddChild(Spacer(10));
            vbox.AddChild(new HSeparator());
            vbox.AddChild(Spacer(8));

            // Player slots
            var slotsLabel = MakeLabel("Players");
            vbox.AddChild(slotsLabel);
            vbox.AddChild(Spacer(4));

            for (int i = 0; i < 4; i++)
            {
                var slot = BuildPlayerSlot(i, out _playerNameLabels[i], out _playerTagLabels[i]);
                vbox.AddChild(slot);
                vbox.AddChild(Spacer(4));
            }

            vbox.AddChild(Spacer(8));
            vbox.AddChild(new HSeparator());
            vbox.AddChild(Spacer(6));

            // Status
            _waitingStatus = new Label { Text = "Waiting for host to start..." };
            _waitingStatus.HorizontalAlignment = HorizontalAlignment.Center;
            _waitingStatus.AddThemeFontSizeOverride("font_size", 14);
            _waitingStatus.AddThemeColorOverride("font_color", DimText);
            vbox.AddChild(_waitingStatus);

            vbox.AddChild(Spacer(6));

            // Start button (host only)
            _startBtn = MakeButton("Start Game", DoloTheme.ButtonPrimary, icon: DoloIcon.Play);
            _startBtn.Pressed += OnStartGame;
            _startBtn.Visible = false;
            vbox.AddChild(_startBtn);
            vbox.AddChild(Spacer(4));

            // Leave button
            var leaveBtn = MakeButton("Leave Room", DoloTheme.ButtonGhost, icon: DoloIcon.Close);
            leaveBtn.Pressed += OnLeaveRoom;
            vbox.AddChild(leaveBtn);
        }

        private Control BuildPlayerSlot(int index, out Label nameLabel, out Label tagLabel)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);

            var panel = new PanelContainer();
            panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;

            var style = new StyleBoxFlat();
            style.BgColor           = new Color(0.13f, 0.16f, 0.24f, 1f);
            style.BorderColor       = new Color(0.25f, 0.35f, 0.55f, 1f);
            style.BorderWidthTop    = style.BorderWidthBottom =
            style.BorderWidthLeft   = style.BorderWidthRight  = 1;
            style.CornerRadiusTopLeft = style.CornerRadiusTopRight =
            style.CornerRadiusBottomLeft = style.CornerRadiusBottomRight = 6;
            panel.AddThemeStyleboxOverride("panel", style);
            panel.CustomMinimumSize = new Vector2(0, 44);

            var inner = new HBoxContainer();
            inner.AddThemeConstantOverride("separation", 10);
            inner.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

            var seatLbl = new Label { Text = $"  {index + 1}" };
            seatLbl.AddThemeFontSizeOverride("font_size", 16);
            seatLbl.AddThemeColorOverride("font_color", DimText);
            seatLbl.VerticalAlignment = VerticalAlignment.Center;
            seatLbl.CustomMinimumSize = new Vector2(28, 0);

            nameLabel = new Label { Text = "Open" };
            nameLabel.AddThemeFontSizeOverride("font_size", 17);
            nameLabel.AddThemeColorOverride("font_color", DimText);
            nameLabel.VerticalAlignment = VerticalAlignment.Center;
            nameLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;

            tagLabel = new Label { Text = "" };
            tagLabel.AddThemeFontSizeOverride("font_size", 13);
            tagLabel.AddThemeColorOverride("font_color", new Color(0.50f, 0.75f, 0.50f));
            tagLabel.VerticalAlignment = VerticalAlignment.Center;
            tagLabel.CustomMinimumSize = new Vector2(60, 0);
            tagLabel.HorizontalAlignment = HorizontalAlignment.Right;

            inner.AddChild(seatLbl);
            inner.AddChild(nameLabel);
            inner.AddChild(tagLabel);

            panel.AddChild(inner);
            row.AddChild(panel);

            return row;
        }

        // =====================================================================
        // Button handlers
        // =====================================================================

        private void OnCreateRoom()
        {
            if (!ValidateAndConnect()) return;
            _isHost = true;
            SetConnectStatus("Connecting...");
            NetworkManager.Instance!.CreateRoom(_nameInput.Text.Trim());
        }

        private void OnJoinRoom()
        {
            var code = _joinCodeInput.Text.Trim().ToUpperInvariant();
            if (code.Length != 6)
            {
                SetConnectStatus("Enter a 6-character room code.");
                return;
            }
            if (!ValidateAndConnect()) return;
            _isHost = false;
            SetConnectStatus("Connecting...");
            NetworkManager.Instance!.JoinRoom(code, _nameInput.Text.Trim());
        }

        private bool ValidateAndConnect()
        {
            var name = _nameInput.Text.Trim();
            if (name.Length == 0)
            {
                SetConnectStatus("Please enter a display name.");
                return false;
            }

            var url = GameSettings.ServerUrl.Trim();
            if (url.Length == 0) url = "ws://localhost:5000/ws";

            // Save to settings (persisted to disk so name is remembered next session)
            GameSettings.PlayerName = name;
            GameSettings.ServerUrl  = url;
            GameSettings.Save();

            var nm = NetworkManager.Instance;
            if (nm == null) return false;

            if (!nm.IsSocketConnected)
            {
                var err = nm.Connect(url);
                if (err != Error.Ok)
                {
                    SetConnectStatus($"Connection failed ({err}). Is the server running?");
                    return false;
                }
            }
            return true;
        }

        private void OnStartGame()
        {
            _startBtn.Disabled = true;
            SetWaitingStatus("Starting game...");
            NetworkManager.Instance?.StartGame();
        }

        private void OnCopyCode()
        {
            DisplayServer.ClipboardSet(_roomCodeLabel.Text);
            _copyBtn.Text = "Copied!";
            GetTree().CreateTimer(1.5f).Timeout += () => { if (IsInstanceValid(_copyBtn)) _copyBtn.Text = "Copy"; };
        }

        private void OnLeaveRoom()
        {
            _reconnectCode = "";
            // If we were searching (not in a real room yet), cancel the queue
            if (_isSearching) NetworkManager.Instance?.SendLeaveQueue();
            NetworkManager.Instance?.ResetSession();
            ShowConnect();
            SetConnectStatus("");
        }

        private void OnQuickPlay()
        {
            var name = _nameInput.Text.Trim();
            if (name.Length == 0)
            {
                SetConnectStatus("Please enter a display name.");
                return;
            }

            if (!ValidateAndConnect()) return;

            // Save name preference
            GameSettings.PlayerName = name;
            GameSettings.Save();

            SetConnectStatus("");
            NetworkManager.Instance!.SendJoinQueue(name);
            // UI will switch to searching state via HandleQueueJoined once the server confirms
        }

        private void OnCancelQueue()
        {
            NetworkManager.Instance?.SendLeaveQueue();
            HideSearching();
            SetConnectStatus("Search cancelled.");
        }

        // =====================================================================
        // Account handlers
        // =====================================================================

        /// <summary>
        /// Run a send-action once the WebSocket is usable. Send() silently drops
        /// messages while the handshake is in flight, so the action is buffered
        /// and flushed from HandleConnected when needed.
        /// </summary>
        private void SendWhenConnected(System.Action send)
        {
            var nm = NetworkManager.Instance;
            if (nm == null) return;

            var url = GameSettings.ServerUrl.Trim();
            if (url.Length == 0) url = "ws://localhost:5000/ws";
            GameSettings.ServerUrl = url;
            GameSettings.Save();

            if (nm.IsSocketOpen)
            {
                send();
                return;
            }

            _pendingSend = send;
            if (!nm.IsSocketConnected)
            {
                var err = nm.Connect(url);
                if (err != Error.Ok)
                {
                    _pendingSend = null;
                    SetConnectStatus($"Connection failed ({err}). Is the server running?");
                    return;
                }
                SetConnectStatus("Connecting…");
            }
            // else: handshake in progress — HandleConnected flushes the action
        }

        private void FlushPendingSend()
        {
            var send = _pendingSend;
            _pendingSend = null;
            send?.Invoke();
        }

        private void StartAuth(string kind)
        {
            var user = _accUserInput.Text.Trim();
            var pass = _accPassInput.Text;
            if (user.Length == 0 || pass.Length == 0)
            {
                SetConnectStatus("Enter a username and password.");
                return;
            }

            SendWhenConnected(() =>
            {
                if (kind == "login") NetworkManager.Instance?.SendLogin(user, pass);
                else                 NetworkManager.Instance?.SendRegister(user, pass);
                SetConnectStatus(kind == "login" ? "Signing in…" : "Creating account…");
            });
        }

        private void OnSetEmail()
        {
            var email = _emailInput.Text.Trim();
            if (email.Length == 0) { SetConnectStatus("Enter an email address."); return; }
            SendWhenConnected(() =>
            {
                NetworkManager.Instance?.SendSetEmail(email);
                SetConnectStatus("Saving email…");
            });
        }

        private void OnChangePassword()
        {
            var oldPass = _oldPassInput.Text;
            var newPass = _newPassInput.Text;
            if (oldPass.Length == 0 || newPass.Length == 0)
            {
                SetConnectStatus("Enter your current and new password.");
                return;
            }
            SendWhenConnected(() =>
            {
                NetworkManager.Instance?.SendChangePassword(oldPass, newPass);
                SetConnectStatus("Changing password…");
            });
        }

        private void OnRequestReset()
        {
            var user = _accUserInput.Text.Trim();
            if (user.Length == 0)
            {
                SetConnectStatus("Enter your username first.");
                return;
            }
            SendWhenConnected(() =>
            {
                NetworkManager.Instance?.SendRequestReset(user);
                SetConnectStatus("Requesting reset code…");
            });
        }

        private void OnResetPassword()
        {
            var user    = _accUserInput.Text.Trim();
            var code    = _resetCodeInput.Text.Trim();
            var newPass = _resetNewPassInput.Text;
            if (user.Length == 0 || code.Length == 0 || newPass.Length == 0)
            {
                SetConnectStatus("Enter your username, the emailed code, and a new password.");
                return;
            }
            SendWhenConnected(() =>
            {
                NetworkManager.Instance?.SendResetPassword(user, code, newPass);
                SetConnectStatus("Resetting password…");
            });
        }

        private void HandleAccountMessage(string message)
            => SetConnectStatus(message);

        // =====================================================================
        // Leaderboard
        // =====================================================================

        private void OnLeaderboardPressed()
        {
            SendWhenConnected(() =>
            {
                NetworkManager.Instance?.SendGetLeaderboard();
                SetConnectStatus("Loading leaderboard…");
            });
        }

        private void HandleLeaderboard(List<NetLeaderboardEntry> entries)
        {
            foreach (var child in _leaderboardRows.GetChildren())
                child.QueueFree();

            string self = GameSettings.AuthUsername;
            foreach (var e in entries)
            {
                bool isSelf   = self.Length > 0
                                && string.Equals(e.Name, self, System.StringComparison.OrdinalIgnoreCase);
                var  color    = isSelf ? new Color(0.55f, 0.95f, 0.60f) : TextColor;
                var  medal    = e.Rank switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => e.Rank.ToString() };
                int  winRate  = e.GamesPlayed > 0
                                ? (int)System.Math.Round(100.0 * e.GamesWon / e.GamesPlayed) : 0;

                _leaderboardRows.AddChild(MakeLeaderboardRow(
                    medal,
                    isSelf ? $"{e.Name} (you)" : e.Name,
                    e.GamesWon.ToString(),
                    e.GamesPlayed.ToString(),
                    $"{winRate}%",
                    e.TotalPoints.ToString("N0"),
                    color));
            }

            _leaderboardStatus.Text = entries.Count == 0
                ? "No ranked games yet — sign in and finish an online game to appear here!"
                : $"Top {entries.Count} players by wins";

            ShowLeaderboard();
        }

        private void ShowLeaderboard() => ShowCard(_leaderboardPanel);

        private void ShowAccount() => ShowCard(_accountPanel);

        /// <summary>
        /// Reveal exactly one card. Making this the only way to change what is on screen
        /// is the point of the split: there is no state where two cards are half-open.
        /// </summary>
        private void ShowCard(Control card)
        {
            foreach (var c in _cards) c.Visible = c == card;
        }

        private void HandleAuthOk(string username, int gamesPlayed, int gamesWon)
        {
            _accPassInput.Text      = "";
            _oldPassInput.Text      = "";
            _newPassInput.Text      = "";
            _resetCodeInput.Text    = "";
            _resetNewPassInput.Text = "";
            _resetBox.Visible       = false;
            UpdateAccountUi();
            SetConnectStatus(gamesPlayed > 0
                ? $"Signed in as {username} — {gamesPlayed} games, {gamesWon} wins."
                : $"Signed in as {username}.");
        }

        private void OnLogout()
        {
            GameSettings.AuthToken    = "";
            GameSettings.AuthUsername = "";
            GameSettings.Save();
            UpdateAccountUi();
            SetConnectStatus("Signed out.");
        }

        /// <summary>Switch the account section between form and signed-in views.
        /// While signed in the display name comes from the account, so the name
        /// input mirrors it and is locked.</summary>
        private void UpdateAccountUi()
        {
            bool loggedIn = GameSettings.IsLoggedIn;
            _accountForm.Visible = !loggedIn;
            _loggedInBox.Visible = loggedIn;
            if (!loggedIn) _manageBox.Visible = false;

            if (loggedIn)
            {
                _loggedInLabel.Text = $"Signed in as {GameSettings.AuthUsername}";
                _nameInput.Text     = GameSettings.AuthUsername;
                _nameInput.Editable = false;

                _identityLabel.Text = $"Signed in as {GameSettings.AuthUsername}";
                _identityLabel.AddThemeColorOverride("font_color", DoloTokens.Ivory);
            }
            else
            {
                _nameInput.Editable = true;
                if (_nameInput.Text == GameSettings.AuthUsername)
                    _nameInput.Text = GameSettings.PlayerName;

                _identityLabel.Text = "Playing as guest";
                _identityLabel.AddThemeColorOverride("font_color", DoloTokens.BodyText);
            }
        }

        // =====================================================================
        // NetworkManager event handlers
        // =====================================================================

        private void HandleRoomCreated(string code, int seat, List<NetPlayerInfo> players)
        {
            _reconnectCode      = "";
            _roomCodeLabel.Text = code;
            _isHost             = true;
            _startBtn.Visible   = true;
            SetWaitingStatus("Share the code with friends. Start when ready.");
            UpdatePlayerSlots(players);
            ShowWaiting();
        }

        private void HandleRoomJoined(string code, int seat, List<NetPlayerInfo> players)
        {
            _reconnectCode      = "";  // clear any pending reconnect attempt
            _roomCodeLabel.Text = code;
            _isHost             = false;
            _startBtn.Visible   = false;
            SetWaitingStatus("Waiting for the host to start the game...");
            UpdatePlayerSlots(players);
            ShowWaiting();
        }

        private void HandlePlayerListUpdated(List<NetPlayerInfo> players)
            => UpdatePlayerSlots(players);

        private void HandlePlayerLeft(int seat, List<NetPlayerInfo> players)
            => UpdatePlayerSlots(players);

        private void HandleError(string message)
            => SetConnectStatus($"Error: {message}");

        private void HandleGameStarted(int yourSeat, string[] names)
        {
            // GameController in network mode will handle the actual game
            GetTree().ChangeSceneToFile("res://Scenes/GameTable.tscn");
        }

        private void HandleConnected()
        {
            SetServerStatus(true, "ok");

            // If we're reconnecting to a lobby, send the rejoin immediately
            if (_reconnectCode.Length > 0)
                NetworkManager.Instance?.SendRejoinRoom(_reconnectCode);

            // Send any account action that was waiting for the handshake
            FlushPendingSend();
        }

        private void HandleQueueJoined()
        {
            // Server confirmed we're in the matchmaking queue — switch to searching state
            ShowSearching();
        }

        private void HandleDisconnected()
        {
            SetServerStatus(false, "offline");

            var nm = NetworkManager.Instance;

            // If we were in a room (lobby or game), try to reconnect automatically
            string code = nm?.RoomCode ?? "";
            if (code.Length > 0)
            {
                _reconnectCode      = code;
                _reconnectServerUrl = GameSettings.ServerUrl;
                ShowWaiting();
                SetWaitingStatus("Connection lost. Reconnecting...");
                var err = nm!.Connect(_reconnectServerUrl);
                if (err != Error.Ok)
                {
                    // Can't even initiate — fall back to connect panel
                    _reconnectCode = "";
                    ShowConnect();
                    SetConnectStatus("Disconnected. Could not reconnect.");
                }
                return;
            }

            ShowConnect();
            SetConnectStatus("Disconnected from server.");
        }

        // =====================================================================
        // UI helpers
        // =====================================================================

        private void UpdatePlayerSlots(List<NetPlayerInfo> players)
        {
            // Reset all slots to "Open"
            for (int i = 0; i < 4; i++)
            {
                _playerNameLabels[i].Text = "Open";
                _playerNameLabels[i].AddThemeColorOverride("font_color", DimText);
                _playerTagLabels[i].Text  = "";
            }

            foreach (var p in players)
            {
                if (p.Seat < 0 || p.Seat >= 4) continue;

                if (p.IsCpu)
                {
                    _playerNameLabels[p.Seat].Text = "CPU";
                    _playerNameLabels[p.Seat].AddThemeColorOverride("font_color", DimText);
                    _playerTagLabels[p.Seat].Text  = "Bot";
                }
                else
                {
                    _playerNameLabels[p.Seat].Text = p.Name;
                    _playerNameLabels[p.Seat].AddThemeColorOverride("font_color", TextColor);

                    bool isLocalSeat = NetworkManager.Instance?.LocalSeat == p.Seat;
                    _playerTagLabels[p.Seat].Text  = isLocalSeat ? "You" : "";
                }
            }
        }

        private void ShowConnect()
        {
            _isSearching = false;
            ShowCard(_connectPanel);
        }

        private void ShowWaiting()
        {
            _isSearching = false;
            ShowCard(_waitingPanel);
        }

        /// <summary>
        /// Enter the searching state. This is now its own card rather than a box inside
        /// the connect card, so none of the form widgets behind it stay live.
        /// </summary>
        private void ShowSearching()
        {
            _isSearching = true;
            _searchingLabel.Text = "Looking for players…";
            ShowCard(_searchingPanel);
        }

        private void HideSearching()
        {
            _isSearching = false;
            if (!_waitingPanel.Visible) ShowConnect();
        }

        private void SetConnectStatus(string msg) => _connectStatus.Text = msg;
        private void SetWaitingStatus(string msg)  => _waitingStatus.Text  = msg;

        // =====================================================================
        // Widget factory helpers (matches MainMenu style)
        // =====================================================================

        /// <summary>
        /// One lobby card. Each of the four states — play online, account, waiting room,
        /// searching — is its own card at its own size, rather than sub-panels toggling
        /// open inside one card and silently changing its height.
        /// </summary>
        private Control MakeCard(float offsetV, float offsetH, float height, float width)
        {
            var card = new PanelContainer { Visible = false };
            card.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
            card.OffsetLeft   = -width  * 0.5f;
            card.OffsetTop    = -height * 0.5f;
            card.OffsetRight  =  width  * 0.5f;
            card.OffsetBottom =  height * 0.5f;
            card.AddThemeStyleboxOverride("panel", DoloStyles.Card(28));

            AddChild(card);
            _cards.Add(card);
            return card;
        }

        private static VBoxContainer MakeCardVBox(Control card)
        {
            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 12);
            card.AddChild(vbox);
            return vbox;
        }

        /// <summary>A field label: mono, above the field, never a placeholder.</summary>
        private static Label MakeLabel(string text)
        {
            var lbl = new Label { Text = text.ToUpperInvariant() };
            lbl.ThemeTypeVariation = DoloTheme.Mono;
            return lbl;
        }

        private static LineEdit MakeLineEdit(string placeholder, string initial = "")
        {
            var le = new LineEdit
            {
                PlaceholderText     = placeholder,
                Text                = initial,
                CustomMinimumSize   = new Vector2(0, DoloTokens.FieldHeight),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            return le;
        }

        /// <summary>
        /// A room code or any other machine-readable string: mono, tracked wide, so the
        /// characters separate and a code can be read aloud without ambiguity.
        /// </summary>
        private static LineEdit MakeCodeEdit(string placeholder, int maxLength)
        {
            var le = MakeLineEdit(placeholder);
            le.MaxLength = maxLength;
            le.Alignment = HorizontalAlignment.Center;
            le.AddThemeFontOverride("font", CodeFont());
            le.AddThemeFontSizeOverride("font_size", DoloTokens.SizeButton);
            return le;
        }

        private static Font? _codeFont;

        private static Font? CodeFont()
        {
            if (_codeFont != null) return _codeFont;
            if (DoloTheme.MonoSemiBoldFont is not Font baseFont) return null;
            _codeFont = new FontVariation { BaseFont = baseFont, SpacingGlyph = 4 };
            return _codeFont;
        }

        private static Button MakeButton(string text, StringName? variation = null,
                                         float minWidth = 0, DoloIcon? icon = null)
        {
            if (icon.HasValue)
            {
                var iconButton = DoloWidgets.IconButton(icon.Value, text, variation, height: 52);
                if (minWidth > 0) iconButton.CustomMinimumSize = new Vector2(minWidth, 52);
                return iconButton;
            }

            var btn = new Button { Text = text };
            btn.ThemeTypeVariation  = variation ?? DoloTheme.ButtonSecondary;
            btn.CustomMinimumSize   = new Vector2(minWidth, 52);
            btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            return btn;
        }

        private static Control Spacer(float height)
        {
            var c = new Control();
            c.CustomMinimumSize = new Vector2(0, height);
            return c;
        }
    }
}
