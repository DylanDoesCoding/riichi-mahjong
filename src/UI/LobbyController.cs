// =============================================================================
// LobbyController.cs
// Multiplayer lobby — name entry, create/join room, waiting room.
//
// Two panels built entirely in code:
//   _connectPanel  — name + server URL + Create / Join buttons
//   _waitingPanel  — room code display, 4 player slots, Start / Leave
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
        private Control _connectPanel = null!;
        private Control _waitingPanel = null!;

        // ---- Connect panel widgets -------------------------------------------
        private LineEdit _nameInput   = null!;
        private LineEdit _serverInput = null!;
        private LineEdit _joinCodeInput = null!;
        private Label   _connectStatus = null!;

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

        // ---- Style colours ---------------------------------------------------
        private static readonly Color BgColor      = new(0.08f, 0.10f, 0.15f, 1f);
        private static readonly Color CardColor    = new(0.10f, 0.12f, 0.18f, 1f);
        private static readonly Color BorderColor  = new(0.30f, 0.45f, 0.70f, 1f);
        private static readonly Color AccentBlue   = new(0.22f, 0.45f, 0.80f);
        private static readonly Color AccentGreen  = new(0.15f, 0.55f, 0.25f);
        private static readonly Color AccentRed    = new(0.60f, 0.15f, 0.15f);
        private static readonly Color TextColor    = new(0.85f, 0.90f, 1f);
        private static readonly Color DimText      = new(0.55f, 0.62f, 0.75f);

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
            BuildWaitingPanel();

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
        }

        // =====================================================================
        // Panel builders
        // =====================================================================

        private void BuildConnectPanel()
        {
            _connectPanel = MakeCard(offsetV: -260, offsetH: -220, height: 520, width: 440);

            var vbox = MakeCardVBox(_connectPanel);

            // Title
            var title = new Label { Text = "🀄  Multiplayer" };
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.AddThemeFontSizeOverride("font_size", 28);
            title.AddThemeColorOverride("font_color", TextColor);
            vbox.AddChild(title);
            vbox.AddChild(new HSeparator());
            vbox.AddChild(Spacer(6));

            // Display name
            vbox.AddChild(MakeLabel("Display Name"));
            _nameInput = MakeLineEdit("Your name", GameSettings.PlayerName);
            vbox.AddChild(_nameInput);
            vbox.AddChild(Spacer(4));

            // Server URL
            vbox.AddChild(MakeLabel("Server URL"));
            _serverInput = MakeLineEdit("ws://localhost:5000/ws", GameSettings.ServerUrl);
            vbox.AddChild(_serverInput);
            vbox.AddChild(Spacer(14));

            // Create Room
            var createBtn = MakeButton("＋  Create Room", AccentBlue);
            createBtn.Pressed += OnCreateRoom;
            vbox.AddChild(createBtn);
            vbox.AddChild(Spacer(10));

            // Join Room row
            vbox.AddChild(MakeLabel("Join with a Room Code"));
            var joinRow = new HBoxContainer();
            joinRow.AddThemeConstantOverride("separation", 8);
            _joinCodeInput = MakeLineEdit("XXXXXX");
            _joinCodeInput.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _joinCodeInput.MaxLength = 6;
            var joinBtn = MakeButton("Join", AccentGreen, minWidth: 90);
            joinBtn.Pressed += OnJoinRoom;
            joinRow.AddChild(_joinCodeInput);
            joinRow.AddChild(joinBtn);
            vbox.AddChild(joinRow);

            vbox.AddChild(Spacer(12));
            vbox.AddChild(new HSeparator());
            vbox.AddChild(Spacer(6));

            // Status label
            _connectStatus = new Label { Text = "" };
            _connectStatus.HorizontalAlignment = HorizontalAlignment.Center;
            _connectStatus.AddThemeFontSizeOverride("font_size", 14);
            _connectStatus.AddThemeColorOverride("font_color", DimText);
            _connectStatus.AutowrapMode = TextServer.AutowrapMode.Word;
            vbox.AddChild(_connectStatus);

            vbox.AddChild(Spacer(4));

            // Back to menu
            var backBtn = MakeButton("← Back to Menu", new Color(0.18f, 0.22f, 0.35f));
            backBtn.Pressed += () =>
            {
                NetworkManager.Instance?.ResetSession();
                GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
            };
            vbox.AddChild(backBtn);
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

            _copyBtn = MakeButton("Copy", new Color(0.22f, 0.35f, 0.55f), minWidth: 90);
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
            _startBtn = MakeButton("▶  Start Game", AccentGreen);
            _startBtn.Pressed += OnStartGame;
            _startBtn.Visible = false;
            vbox.AddChild(_startBtn);
            vbox.AddChild(Spacer(4));

            // Leave button
            var leaveBtn = MakeButton("✕  Leave Room", AccentRed);
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

            var url = _serverInput.Text.Trim();
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
            NetworkManager.Instance?.ResetSession();
            ShowConnect();
            SetConnectStatus("");
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
            // If we're reconnecting to a lobby, send the rejoin immediately
            if (_reconnectCode.Length > 0)
                NetworkManager.Instance?.SendRejoinRoom(_reconnectCode);
        }

        private void HandleDisconnected()
        {
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
            _connectPanel.Visible = true;
            _waitingPanel.Visible = false;
        }

        private void ShowWaiting()
        {
            _connectPanel.Visible = false;
            _waitingPanel.Visible = true;
        }

        private void SetConnectStatus(string msg) => _connectStatus.Text = msg;
        private void SetWaitingStatus(string msg)  => _waitingStatus.Text  = msg;

        // =====================================================================
        // Widget factory helpers (matches MainMenu style)
        // =====================================================================

        private Control MakeCard(float offsetV, float offsetH, float height, float width)
        {
            var card = new PanelContainer();
            card.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
            card.OffsetLeft   = offsetH;
            card.OffsetTop    = offsetV;
            card.OffsetRight  = -offsetH;
            card.OffsetBottom = -offsetV;

            var style = new StyleBoxFlat();
            style.BgColor           = CardColor;
            style.BorderColor       = BorderColor;
            style.BorderWidthTop    = style.BorderWidthBottom =
            style.BorderWidthLeft   = style.BorderWidthRight  = 2;
            style.CornerRadiusTopLeft = style.CornerRadiusTopRight =
            style.CornerRadiusBottomLeft = style.CornerRadiusBottomRight = 10;
            card.AddThemeStyleboxOverride("panel", style);

            AddChild(card);
            return card;
        }

        private static VBoxContainer MakeCardVBox(Control card)
        {
            var vbox = new VBoxContainer();
            vbox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            vbox.OffsetLeft   = 28;
            vbox.OffsetTop    = 22;
            vbox.OffsetRight  = -28;
            vbox.OffsetBottom = -22;
            vbox.AddThemeConstantOverride("separation", 8);
            card.AddChild(vbox);
            return vbox;
        }

        private static Label MakeLabel(string text)
        {
            var lbl = new Label { Text = text };
            lbl.AddThemeFontSizeOverride("font_size", 14);
            lbl.AddThemeColorOverride("font_color", DimText);
            return lbl;
        }

        private static LineEdit MakeLineEdit(string placeholder, string initial = "")
        {
            var le = new LineEdit();
            le.PlaceholderText    = placeholder;
            le.Text               = initial;
            le.CustomMinimumSize  = new Vector2(0, 40);
            le.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            le.AddThemeFontSizeOverride("font_size", 16);
            return le;
        }

        private static Button MakeButton(string text, Color color, float minWidth = 0)
        {
            var btn = new Button { Text = text };
            btn.CustomMinimumSize   = new Vector2(minWidth, 46);
            btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            btn.AddThemeFontSizeOverride("font_size", 16);

            var style = new StyleBoxFlat();
            style.BgColor              = color;
            style.CornerRadiusTopLeft  = style.CornerRadiusTopRight =
            style.CornerRadiusBottomLeft = style.CornerRadiusBottomRight = 6;
            btn.AddThemeStyleboxOverride("normal", style);

            var hover = (StyleBoxFlat)style.Duplicate();
            hover.BgColor = color.Lightened(0.18f);
            btn.AddThemeStyleboxOverride("hover", hover);

            var pressed = (StyleBoxFlat)style.Duplicate();
            pressed.BgColor = color.Darkened(0.12f);
            btn.AddThemeStyleboxOverride("pressed", pressed);

            btn.AddThemeColorOverride("font_color", Colors.White);
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
