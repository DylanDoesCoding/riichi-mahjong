// =============================================================================
// HandDisplay.cs
// Renders one player's hand as a row or column of TileNodes.
//
// Bottom / Top  → HBoxContainer (horizontal row)
// Left  / Right → VBoxContainer (vertical column, face-down, compact tiles)
//
// Extends Control so it owns an inner container that matches the orientation.
// =============================================================================

using Godot;
using System.Collections.Generic;
using RiichiMahjong.Core;

namespace RiichiMahjong.UI
{
    public enum HandOrientation { Bottom, Top, Left, Right }

    public partial class HandDisplay : Control
    {
        // ---- Configuration ---------------------------------------------------

        [Export] public HandOrientation Orientation   { get; set; } = HandOrientation.Bottom;
        [Export] public bool            IsInteractive  { get; set; } = false;
        [Export] public bool            FaceDown       { get; set; } = true;

        // ---- Tile size: CPU side tiles are compact (face-down only) ----------

        private int TileW => (Orientation is HandOrientation.Left or HandOrientation.Right)
            ? 42 : TileNode.TileWidth;
        private int TileH => (Orientation is HandOrientation.Left or HandOrientation.Right)
            ? 52 : TileNode.TileHeight;

        // ---- Internal --------------------------------------------------------

        private BoxContainer       _inner        = null!;
        private List<TileNode>     _tileNodes    = new();
        private List<Control>      _meldControls = new();
        private TileNode?          _selected     = null;
        private TileNode?          _lifted       = null;

        // ---- Signals ---------------------------------------------------------

        [Signal] public delegate void TileSelectedEventHandler(TileNode tile);
        [Signal] public delegate void TileDiscardedEventHandler(TileNode tile);

        // =====================================================================
        // Godot lifecycle
        // =====================================================================

        public override void _Ready()
        {
            bool vertical = Orientation is HandOrientation.Left or HandOrientation.Right;

            if (vertical)
            {
                var vbox = new VBoxContainer();
                vbox.AddThemeConstantOverride("separation", 3);
                vbox.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
                _inner = vbox;
            }
            else
            {
                var hbox = new HBoxContainer();
                hbox.AddThemeConstantOverride("separation", 4);
                hbox.Alignment = BoxContainer.AlignmentMode.Center;
                hbox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                _inner = hbox;
            }

            SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            SizeFlagsVertical   = SizeFlags.ShrinkCenter;
            AddChild(_inner);
        }

        // =====================================================================
        // Public API
        // =====================================================================

        public void Rebuild(IReadOnlyList<Tile> tiles, IReadOnlyList<Meld>? melds = null, Tile? drawnTile = null)
        {
            // Clear closed tiles
            foreach (var n in _tileNodes) n.QueueFree();
            _tileNodes.Clear();
            _selected = null;
            _lifted   = null;

            // Clear meld controls
            foreach (var c in _meldControls) c.QueueFree();
            _meldControls.Clear();

            // Rebuild closed tiles
            for (int i = 0; i < tiles.Count; i++)
            {
                var node = MakeNode(tiles[i]);
                bool isDrawn = drawnTile != null && tiles[i] == drawnTile && i == tiles.Count - 1;
                if (isDrawn) { _lifted = node; node.SetLifted(true); }
                _inner.AddChild(node);
                _tileNodes.Add(node);
            }

            // Rebuild open melds (shown to the right / bottom of closed tiles)
            if (melds != null && melds.Count > 0)
            {
                bool vertical = Orientation is HandOrientation.Left or HandOrientation.Right;

                // Gap between closed tiles and melds
                var gap = new Control();
                gap.CustomMinimumSize = vertical ? new Vector2(0, 8) : new Vector2(10, 0);
                _inner.AddChild(gap);
                _meldControls.Add(gap);

                foreach (var meld in melds)
                {
                    // Each meld is a small box of tiles
                    BoxContainer meldBox = vertical
                        ? new VBoxContainer()
                        : new HBoxContainer();
                    meldBox.AddThemeConstantOverride("separation", 2);

                    for (int ti = 0; ti < meld.Tiles.Count; ti++)
                    {
                        var tile  = meld.Tiles[ti];
                        // KanClosed: outer two tiles (index 0 and 3) are face-down
                        bool tileDown = meld.Type == MeldType.KanClosed
                                        && (ti == 0 || ti == 3);
                        var tNode = new TileNode();
                        tNode.CustomMinimumSize = new Vector2(TileW, TileH);
                        tNode.SetTile(tile, faceDown: tileDown);
                        tNode.SetInteractive(false);
                        meldBox.AddChild(tNode);
                    }

                    _inner.AddChild(meldBox);
                    _meldControls.Add(meldBox);

                    // Small gap between consecutive melds
                    var meldGap = new Control();
                    meldGap.CustomMinimumSize = vertical ? new Vector2(0, 4) : new Vector2(4, 0);
                    _inner.AddChild(meldGap);
                    _meldControls.Add(meldGap);
                }
            }
        }

        public void AddTile(Tile tile, bool isDrawnTile = false)
        {
            _lifted?.SetLifted(false);
            _lifted = null;

            var node = MakeNode(tile);
            if (isDrawnTile) { _lifted = node; node.SetLifted(true); }
            _inner.AddChild(node);
            _tileNodes.Add(node);
        }

        public void RemoveTile(Tile tile)
        {
            for (int i = _tileNodes.Count - 1; i >= 0; i--)
            {
                if (_tileNodes[i].TileData == tile)
                {
                    if (_tileNodes[i] == _selected) _selected = null;
                    if (_tileNodes[i] == _lifted)   _lifted   = null;
                    _tileNodes[i].QueueFree();
                    _tileNodes.RemoveAt(i);
                    return;
                }
            }
        }

        public void RevealAll()
        {
            FaceDown = false;
            foreach (var n in _tileNodes) n.SetFaceDown(false);
        }

        public void ClearSelection()
        {
            _selected?.SetSelected(false);
            _selected = null;
        }

        /// <summary>The TileNode that was most recently drawn (lifted), or null.</summary>
        public TileNode? DrawnTileNode => _lifted;

        /// <summary>
        /// Enter riichi candidate mode: highlight valid discard tiles and dim the rest.
        /// </summary>
        public void SetRiichiCandidates(IEnumerable<Tile> candidates)
        {
            var candidateIds = new HashSet<int>(candidates.Select(t => t.TileId));
            foreach (var node in _tileNodes)
            {
                if (node.TileData == null) continue;
                bool isCandidate = candidateIds.Contains(node.TileData.TileId);
                node.SetRiichiState(isCandidate, modeActive: true);
            }
        }

        /// <summary>Clear all riichi candidate highlighting from every tile node.</summary>
        public void ClearRiichiCandidates()
        {
            foreach (var node in _tileNodes)
                node.SetRiichiState(isCandidate: false, modeActive: false);
        }

        /// <summary>
        /// Highlight the specific tiles that would be used in a Pon / Chi claim.
        /// Handles duplicates correctly: the first N matching nodes are highlighted
        /// where N = how many times each tile appears in <paramref name="tiles"/>.
        /// </summary>
        public void HighlightClaimTiles(IEnumerable<Tile> tiles)
        {
            // Build a count map so we highlight exactly the right number of copies
            var counts = new Dictionary<int, int>();
            foreach (var t in tiles)
            {
                int id = t.TileId;
                counts[id] = counts.TryGetValue(id, out int c) ? c + 1 : 1;
            }

            var remaining = new Dictionary<int, int>(counts);

            foreach (var node in _tileNodes)
            {
                if (node.TileData == null) continue;
                int id = node.TileData.TileId;
                if (remaining.TryGetValue(id, out int left) && left > 0)
                {
                    node.SetClaimHighlight(true);
                    remaining[id] = left - 1;
                }
            }
        }

        /// <summary>Turn off the claim highlight on every tile node in this hand.</summary>
        public void ClearClaimTileHighlights()
        {
            foreach (var node in _tileNodes)
                node.SetClaimHighlight(false);
        }

        public Tile? GetSelectedTile() => _selected?.TileData;
        public int   TileCount         => _tileNodes.Count;

        // =====================================================================
        // Private helpers
        // =====================================================================

        private TileNode MakeNode(Tile tile)
        {
            var node = new TileNode();
            // Override tile size for compact CPU tiles
            node.CustomMinimumSize = new Vector2(TileW, TileH);
            node.SetTile(tile, FaceDown);
            node.SetInteractive(IsInteractive);
            if (IsInteractive)
                node.TileClicked += OnTileClicked;
            return node;
        }

        private void OnTileClicked(TileNode clicked)
        {
            if (!IsInteractive) return;

            if (_selected == clicked)
            {
                EmitSignal(SignalName.TileDiscarded, clicked);
            }
            else
            {
                _selected?.SetSelected(false);
                _selected = clicked;
                clicked.SetSelected(true);
                EmitSignal(SignalName.TileSelected, clicked);
            }
        }
    }
}
