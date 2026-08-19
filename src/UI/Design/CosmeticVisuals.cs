// =============================================================================
// CosmeticVisuals.cs
// Turns cosmetic ids into things Godot can draw.
//
// The ids in Core are shared with the server and must stay stable; how each one
// looks is a client concern and lives here. Keeping the two apart means the
// look can change without a protocol change.
// =============================================================================

using Godot;
using RiichiMahjong.Core;

namespace RiichiMahjong.UI
{
    public static class CosmeticVisuals
    {
        private const string PropPath = "res://Assets/Props";

        // How far a chosen surface pulls the felt away from its quiet tone. The mockup's
        // colourblind pass rules that the felt stays quiet so the tiles carry state - a
        // wedge tint must not compete with a dora border for attention - so a surface is
        // a whisper of hue over the felt, not a colour block. 0 = pure felt, 1 = raw swatch.
        private const float SurfaceTint = 0.35f;

        /// <summary>The wedge fill for a surface id, muted toward the quiet felt tone.</summary>
        public static Color Surface(string id)
        {
            Color raw = id switch
            {
                "tatami"  => DoloTokens.Tatami,
                "oxblood" => DoloTokens.Oxblood,
                "slate"   => DoloTokens.Slate,
                _         => DoloTokens.FeltQuiet,
            };
            return DoloTokens.FeltQuiet.Lerp(raw, SurfaceTint);
        }

        /// <summary>
        /// The prop sprite, or null where the art does not exist yet. A null leaves the
        /// dashed pocket showing, which is the honest state for the snack bowl - it is
        /// specified but unmade. Ashtray, beer, coffee and teapot are finished.
        /// </summary>
        public static Texture2D? Prop(string id) => id switch
        {
            "ashtray" => GD.Load<Texture2D>($"{PropPath}/ashtray.png"),
            "beer"    => GD.Load<Texture2D>($"{PropPath}/beer.png"),
            "coffee"  => GD.Load<Texture2D>($"{PropPath}/coffee.png"),
            "teapot"  => GD.Load<Texture2D>($"{PropPath}/teapot.png"),
            _         => null,
        };

        /// <summary>
        /// Whether this prop has finished art behind it. Delegates to Core's
        /// <see cref="CosmeticCatalogue.FinishedProps"/> so "which props are real" has one
        /// source of truth the tests can also read.
        /// </summary>
        public static bool PropIsDrawn(string id) => CosmeticCatalogue.PropIsFinished(id);

        /// <summary>The nameplate frame for a frame id.</summary>
        public static StyleBoxFlat Frame(string id)
        {
            var box = DoloStyles.Flat(DoloTokens.Card, DoloTokens.RadiusBoard,
                                      DoloTokens.HairlineMedium, borderWidth: 1);
            box.ContentMarginLeft   = 12;
            box.ContentMarginRight  = 12;
            box.ContentMarginTop    = 8;
            box.ContentMarginBottom = 8;

            switch (id)
            {
                case "brass":
                    box.BorderColor = DoloTokens.Brass;
                    DoloStyles.SetBorder(box, 2);
                    break;

                case "carved":
                    // Carved reads as a recessed edge: dark fill, light top hairline.
                    box.BgColor     = DoloTokens.InsetPane;
                    box.BorderColor = DoloTokens.Hairline(0.45f);
                    DoloStyles.SetBorder(box, 1);
                    box.BorderWidthTop = 3;
                    break;

                case "neon":
                    box.BorderColor  = DoloTokens.RiichiCyan;
                    DoloStyles.SetBorder(box, 2);
                    box.ShadowColor  = new Color(DoloTokens.RiichiCyan, 0.25f);
                    box.ShadowSize   = 8;
                    box.ShadowOffset = Vector2.Zero;
                    break;
            }

            box.ShadowColor  = box.ShadowSize > 0 ? box.ShadowColor : DoloTokens.ShadowButtonColor;
            if (box.ShadowSize == 0)
            {
                box.ShadowSize   = 10;
                box.ShadowOffset = new Vector2(0, 4);
            }

            return box;
        }

        /// <summary>The emblem badge, or null for "none".</summary>
        public static Control? Emblem(string id, int size = 16)
        {
            if (id == "none") return null;

            var icon = id switch
            {
                "circle"   => DoloIcon.Globe,
                "diamond"  => DoloIcon.Plus,
                "bars"     => DoloIcon.Tile,
                "crescent" => DoloIcon.Moon,
                _          => DoloIcon.Globe,
            };

            return new DoloIconRect(icon, size, DoloTokens.Brass);
        }

        /// <summary>
        /// Apply a whole set to one seat's wedge on the felt. Only the local player's own
        /// wedge takes a surface tint (<paramref name="tintSurface"/>); every other seat
        /// stays on the quiet felt so the table never becomes a four-colour pinwheel. The
        /// prop still applies to every seat, since that is where a remote player's identity
        /// belongs once the felt has gone quiet.
        /// </summary>
        public static void ApplyToFelt(TableFelt? felt, int visualSeat, CosmeticSet set,
                                       bool tintSurface)
        {
            if (felt == null) return;
            felt.SetSeatSurface(visualSeat,
                                tintSurface ? Surface(set.Surface) : DoloTokens.FeltQuiet);
            felt.SetSeatProp(visualSeat, Prop(set.Prop));
        }
    }
}
