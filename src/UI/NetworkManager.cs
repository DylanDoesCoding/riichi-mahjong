// =============================================================================
// NetworkManager.cs
// Godot-side WebSocket client for multiplayer.
//
// Connects to the RiichiServer, serialises outgoing actions as JSON,
// and fires C# events for each incoming server message so that the
// lobby UI and GameController can respond without knowing about sockets.
//
// Usage:
//   NetworkManager.Instance.Connect("ws://your-server/ws");
//   NetworkManager.Instance.CreateRoom("Dylan");
//   NetworkManager.Instance.OnRoomCreated += (code, seat) => ...;
// =============================================================================

using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using RiichiMahjong.Core;

namespace RiichiMahjong.UI
{
    // ---- Lightweight DTOs mirroring server/Messages/ (no server assembly dep) ---

    public class NetTileDto
    {
        public TileSuit Suit      { get; set; }
        public int      Value     { get; set; }
        public bool     IsRedDora { get; set; }

        public Tile ToTile() => new(Suit, Value, IsRedDora);
        public static NetTileDto From(Tile t) => new() { Suit = t.Suit, Value = t.Value, IsRedDora = t.IsRedDora };
    }

    public class NetMeldDto
    {
        public string           Type  { get; set; } = "";
        public List<NetTileDto> Tiles { get; set; } = new();
    }

    public class NetPlayerInfo
    {
        public int    Seat  { get; set; }
        public string Name  { get; set; } = "";
        public bool   IsCpu { get; set; }
    }

    public class NetScoreEntry
    {
        public int    Seat   { get; set; }
        public string Name   { get; set; } = "";
        public int    Points { get; set; }
    }

    /// <summary>Flat server message — all fields optional, "type" is the discriminator.</summary>
    public class NetServerMessage
    {
        public string                  Type           { get; set; } = "";
        public string?                 Code           { get; set; }
        public int                     YourSeat       { get; set; }
        public List<NetPlayerInfo>?    Players        { get; set; }
        public string?                 Error          { get; set; }
        public int                     DealerSeat     { get; set; }
        public string?                 RoundWind      { get; set; }
        public int                     Counters       { get; set; }
        public List<NetTileDto>?       YourTiles      { get; set; }
        public int[]?                  TileCounts     { get; set; }
        public int[]?                  Scores         { get; set; }
        public string[]?               Names          { get; set; }
        public int                     Seat           { get; set; }
        public NetTileDto?             Tile           { get; set; }
        public bool                    IsRiichiDiscard{ get; set; }
        public NetMeldDto?             Meld           { get; set; }
        public int                     DiscarderSeat  { get; set; }
        public bool                    CanRon         { get; set; }
        public bool                    CanPon         { get; set; }
        public bool                    CanChi         { get; set; }
        public bool                    CanKan         { get; set; }
        public string?                 Reason         { get; set; }
        public int[]?                  Winners        { get; set; }
        public List<NetScoreEntry>?    ScoreBoard     { get; set; }
        public int                     Han            { get; set; }
        public int                     Fu             { get; set; }
        public int                     BasePoints     { get; set; }
        public string[]?               YakuNames      { get; set; }
        public int                     DoraCount      { get; set; }
        public int                     UraDoraCount   { get; set; }
        public int                     WinnerSeat     { get; set; }
        public int                     PayerSeat      { get; set; }

        // ---- Reconnection snapshot ------------------------------------------
        public List<List<NetTileDto>>? Discards       { get; set; }  // [seat][tile]
        public List<List<NetMeldDto>>? Melds          { get; set; }  // [seat][meld]
        public int[]?                  RiichiSeats    { get; set; }
        public int                     CurrentTurn    { get; set; }

        // ---- Dora -----------------------------------------------------------
        public List<NetTileDto>?       DoraIndicators { get; set; }

        // ---- Furiten --------------------------------------------------------
        /// <summary>True when the server reports the human player entered temporary furiten.</summary>
        public bool                    IsTemporaryFuriten { get; set; }

        // ---- Ryuukyoku reveal -----------------------------------------------
        /// <summary>Closed tiles per seat at exhaustive draw. Empty inner list = noten.</summary>
        public List<List<NetTileDto>>? RevealedHands  { get; set; }
        /// <summary>Waiting tiles per seat at exhaustive draw. Empty inner list = noten or no waits.</summary>
        public List<List<NetTileDto>>? TenpaiWaits    { get; set; }
    }

    // =========================================================================

    public partial class NetworkManager : Node
    {
        // ---- Singleton -------------------------------------------------------
        public static NetworkManager? Instance { get; private set; }

        // ---- State -----------------------------------------------------------
        public bool  IsSocketConnected  { get; private set; }
        public int   LocalSeat    { get; private set; } = -1;
        public string RoomCode   { get; private set; } = "";

        // ---- Lobby events ----------------------------------------------------
        public event Action<string, int, List<NetPlayerInfo>>? OnRoomCreated;  // code, seat, players
        public event Action<string, int, List<NetPlayerInfo>>? OnRoomJoined;   // code, seat, players
        public event Action<List<NetPlayerInfo>>?               OnPlayerJoined; // updated list
        public event Action<int, List<NetPlayerInfo>>?          OnPlayerLeft;   // seat, updated list
        public event Action<string>?                            OnError;
        public event Action?                                    OnConnected;    // WebSocket handshake complete
        public event Action?                                    OnDisconnected;
        public event Action?                                    OnRejoinSuccess; // server accepted rejoin
        public event Action<List<Tile>>?                        OnDoraUpdated;  // indicators changed (hand start or kan)

        // ---- Game events (mirror GameState events so GameController is unaware) --
        public event Action<int, string[]>?          OnGameStarted;     // yourSeat, names
        public event Action<List<Tile>, int[], int[], int, string, int, string[]>? OnHandDealt;
        //                  yourTiles, tileCounts, scores, dealerSeat, roundWind, counters, names
        public event Action<int, Tile?>?             OnTileDrawn;       // seat, tile (null = hidden)
        public event Action<int, Tile, bool>?        OnTileDiscarded;   // seat, tile, isRiichi
        public event Action<int, NetMeldDto>?        OnMeldDeclared;    // seat, meld
        public event Action<int>?                    OnRiichiDeclared;  // seat
        public event Action<int, Tile, bool, bool, bool, bool>? OnClaimWindowOpened;
        public event Action<bool>?                   OnFuritenChanged;  // isTemporaryFuriten
        public event Action?                         OnQueueJoined;     // entered matchmaking queue
        //                  discarderSeat, tile, canRon, canPon, canChi, canKan
        public event Action<string, int[], List<NetScoreEntry>, int, int, int, string[], int, int, int, int>? OnHandEnded;
        //                  reason, winners, scoreBoard, han, fu, basePoints, yakuNames, doraCount, uraDoraCount, winnerSeat, payerSeat
        public event Action<List<NetScoreEntry>>?    OnGameOver;        // scoreBoard

        // ---- Ryuukyoku reveal data (populated before OnHandEnded fires) ------
        /// <summary>
        /// Closed tiles for all 4 seats at exhaustive draw (empty list = noten).
        /// Set before <see cref="OnHandEnded"/> fires; null when last hand was not a draw.
        /// </summary>
        public List<List<Tile>>? LastRevealedHands { get; private set; }
        /// <summary>
        /// Waiting tile types per seat at exhaustive draw (empty list = noten).
        /// Computed server-side so open-hand waits are always correct.
        /// </summary>
        public List<List<Tile>>? LastTenpaiWaits   { get; private set; }

        // Fired when a mid-game rejoin succeeds — carries same payload as OnHandDealt
        // plus per-seat discards and melds for full board resync.
        public event Action<List<Tile>, int[], int[], int, string, int, string[],
                            List<List<NetTileDto>>, List<List<NetMeldDto>>, int[], int>?
            OnGameStateSnapshot;
        // yourTiles, tileCounts, scores, dealerSeat, roundWind, counters, names,
        // discards[seat][tile], melds[seat][meld], riichiSeats, currentTurn

        // ---- Internal --------------------------------------------------------
        private WebSocketPeer _ws         = new();
        private bool          _socketOpen = false;  // true only while WS is actually Open
        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
            Converters                  = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        // =====================================================================
        // Godot lifecycle
        // =====================================================================

        public override void _Ready()
        {
            Instance = this;
            GameSettings.Load();   // restore saved name, server URL, volumes, etc.
        }

        public override void _Process(double delta)
        {
            var state = _ws.GetReadyState();
            if (state == WebSocketPeer.State.Closed && !_socketOpen) return;

            _ws.Poll();
            state = _ws.GetReadyState();

            // Detect socket becoming Open (handshake complete)
            if (!_socketOpen && state == WebSocketPeer.State.Open)
            {
                _socketOpen       = true;
                IsSocketConnected = true;
                OnConnected?.Invoke();
            }

            while (_ws.GetAvailablePacketCount() > 0)
            {
                var raw = _ws.GetPacket().GetStringFromUtf8();
                try
                {
                    var msg = JsonSerializer.Deserialize<NetServerMessage>(raw, _jsonOpts);
                    if (msg != null) DispatchMessage(msg);
                }
                catch (Exception e)
                {
                    GD.PrintErr($"[NetworkManager] JSON parse error: {e.Message}");
                }
            }

            // Detect disconnection
            if (_socketOpen && _ws.GetReadyState() == WebSocketPeer.State.Closed)
            {
                _socketOpen       = false;
                IsSocketConnected = false;
                OnDisconnected?.Invoke();
            }
        }

        // =====================================================================
        // Connection
        // =====================================================================

        public Error Connect(string url)
        {
            _ws         = new WebSocketPeer();
            _socketOpen = false;
            var err     = _ws.ConnectToUrl(url);
            if (err == Error.Ok) IsSocketConnected = true;
            return err;
        }

        public void Disconnect()
        {
            _ws.Close();
            _socketOpen       = false;
            IsSocketConnected = false;
        }

        /// <summary>
        /// Clear session state (seat, room code) without closing the socket.
        /// Call this when navigating away from multiplayer so local mode starts clean.
        /// </summary>
        public void ResetSession()
        {
            LocalSeat = -1;
            RoomCode  = "";
            Disconnect();
        }

        // =====================================================================
        // Outbound actions — called by LobbyUI / GameController
        // =====================================================================

        public void CreateRoom(string displayName)
            => Send(new { type = "createRoom", displayName, uuid = GameSettings.PlayerUuid });

        public void JoinRoom(string code, string displayName)
            => Send(new { type = "joinRoom", code, displayName, uuid = GameSettings.PlayerUuid });

        public void SendRejoinRoom(string code)
            => Send(new { type = "rejoinRoom", code, uuid = GameSettings.PlayerUuid });

        public void StartGame()
            => Send(new { type = "startGame" });

        public void SendDiscard(Tile tile)
            => Send(new { type = "discard", tile = NetTileDto.From(tile) });

        public void SendRiichi(Tile discardTile)
            => Send(new { type = "riichi", tile = NetTileDto.From(discardTile) });

        public void SendTsumo()
            => Send(new { type = "tsumo" });

        public void SendPon()
            => Send(new { type = "pon" });

        public void SendChi(Tile t1, Tile t2)
            => Send(new { type = "chi", t1 = NetTileDto.From(t1), t2 = NetTileDto.From(t2) });

        public void SendRon()
            => Send(new { type = "ron" });

        public void SendKan(Tile? tile = null)
            => Send(tile != null
                ? new { type = "kan", tile = NetTileDto.From(tile) }
                : (object)new { type = "kan" });

        public void SendKyuushu()
            => Send(new { type = "kyuushu" });

        public void SendPass()
            => Send(new { type = "pass" });

        public void SendNextHand()
            => Send(new { type = "nextHand" });

        public void SendJoinQueue(string displayName)
            => Send(new { type = "joinQueue", displayName, uuid = GameSettings.PlayerUuid });

        public void SendLeaveQueue()
            => Send(new { type = "leaveQueue" });

        // =====================================================================
        // Dispatch
        // =====================================================================

        private void DispatchMessage(NetServerMessage msg)
        {
            switch (msg.Type)
            {
                case "roomCreated":
                    LocalSeat = msg.YourSeat;
                    RoomCode  = msg.Code ?? "";
                    OnRoomCreated?.Invoke(RoomCode, LocalSeat, msg.Players ?? new());
                    break;

                case "roomJoined":
                    LocalSeat = msg.YourSeat;
                    RoomCode  = msg.Code ?? "";
                    OnRoomJoined?.Invoke(RoomCode, LocalSeat, msg.Players ?? new());
                    break;

                case "playerJoined":
                    OnPlayerJoined?.Invoke(msg.Players ?? new());
                    break;

                case "playerLeft":
                    OnPlayerLeft?.Invoke(msg.Seat, msg.Players ?? new());
                    break;

                case "error":
                    OnError?.Invoke(msg.Error ?? "Unknown error");
                    break;

                case "gameStarted":
                    LocalSeat = msg.YourSeat;
                    // For matchmade games there is no prior roomJoined, so the server
                    // includes the room code here for reconnection support.
                    if (!string.IsNullOrEmpty(msg.Code)) RoomCode = msg.Code;
                    OnGameStarted?.Invoke(msg.YourSeat, msg.Names ?? Array.Empty<string>());
                    break;

                case "queueJoined":
                    OnQueueJoined?.Invoke();
                    break;

                case "handDealt":
                    var yourTiles = (msg.YourTiles ?? new()).Select(d => d.ToTile()).ToList();
                    OnHandDealt?.Invoke(
                        yourTiles,
                        msg.TileCounts  ?? Array.Empty<int>(),
                        msg.Scores      ?? Array.Empty<int>(),
                        msg.DealerSeat,
                        msg.RoundWind   ?? "East",
                        msg.Counters,
                        msg.Names       ?? Array.Empty<string>());
                    if (msg.DoraIndicators != null)
                        OnDoraUpdated?.Invoke(msg.DoraIndicators.Select(d => d.ToTile()).ToList());
                    break;

                case "tileDrawn":
                    OnTileDrawn?.Invoke(msg.Seat, msg.Tile?.ToTile());
                    if (msg.DoraIndicators != null)
                        OnDoraUpdated?.Invoke(msg.DoraIndicators.Select(d => d.ToTile()).ToList());
                    break;

                case "tileDiscarded":
                    if (msg.Tile != null)
                        OnTileDiscarded?.Invoke(msg.Seat, msg.Tile.ToTile(), msg.IsRiichiDiscard);
                    break;

                case "meldDeclared":
                    if (msg.Meld != null)
                        OnMeldDeclared?.Invoke(msg.Seat, msg.Meld);
                    if (msg.DoraIndicators != null)
                        OnDoraUpdated?.Invoke(msg.DoraIndicators.Select(d => d.ToTile()).ToList());
                    break;

                case "riichiDeclared":
                    OnRiichiDeclared?.Invoke(msg.Seat);
                    break;

                case "claimWindowOpened":
                    if (msg.Tile != null)
                        OnClaimWindowOpened?.Invoke(
                            msg.DiscarderSeat, msg.Tile.ToTile(),
                            msg.CanRon, msg.CanPon, msg.CanChi, msg.CanKan);
                    break;

                case "furitenChanged":
                    OnFuritenChanged?.Invoke(msg.IsTemporaryFuriten);
                    break;

                case "handEnded":
                    // Parse ryuukyoku reveal data before firing the event
                    LastRevealedHands = msg.RevealedHands?
                        .Select(row => row.Select(d => d.ToTile()).ToList())
                        .ToList();
                    LastTenpaiWaits = msg.TenpaiWaits?
                        .Select(row => row.Select(d => d.ToTile()).ToList())
                        .ToList();
                    OnHandEnded?.Invoke(
                        msg.Reason     ?? "",
                        msg.Winners    ?? Array.Empty<int>(),
                        msg.ScoreBoard ?? new(),
                        msg.Han, msg.Fu, msg.BasePoints,
                        msg.YakuNames  ?? Array.Empty<string>(),
                        msg.DoraCount,
                        msg.UraDoraCount,
                        msg.WinnerSeat,
                        msg.PayerSeat);
                    break;

                case "gameOver":
                    OnGameOver?.Invoke(msg.ScoreBoard ?? new());
                    break;

                case "gameStateSnapshot":
                    var snapTiles = (msg.YourTiles ?? new()).Select(d => d.ToTile()).ToList();
                    OnGameStateSnapshot?.Invoke(
                        snapTiles,
                        msg.TileCounts  ?? Array.Empty<int>(),
                        msg.Scores      ?? Array.Empty<int>(),
                        msg.DealerSeat,
                        msg.RoundWind   ?? "East",
                        msg.Counters,
                        msg.Names       ?? Array.Empty<string>(),
                        msg.Discards    ?? new(),
                        msg.Melds       ?? new(),
                        msg.RiichiSeats ?? Array.Empty<int>(),
                        msg.CurrentTurn);
                    if (msg.DoraIndicators != null)
                        OnDoraUpdated?.Invoke(msg.DoraIndicators.Select(d => d.ToTile()).ToList());
                    OnRejoinSuccess?.Invoke();
                    break;
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private void Send(object payload)
        {
            if (_ws.GetReadyState() != WebSocketPeer.State.Open) return;
            try
            {
                var json = JsonSerializer.Serialize(payload, _jsonOpts);
                _ws.SendText(json);
            }
            catch (Exception e)
            {
                GD.PrintErr($"[NetworkManager] Send error: {e.Message}");
            }
        }
    }
}
