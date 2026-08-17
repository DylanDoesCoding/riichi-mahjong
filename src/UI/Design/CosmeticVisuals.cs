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

        /// <summary>The wedge fill for a surface id.</summary>
        public static Color Surface(string id) => id switch
        {
            "tatami"  => DoloTokens.Tatami,
            "oxblood" => DoloTokens.Oxblood,
            "slate"   => DoloTokens.Slate,
            _         => DoloTokens.FeltQuiet,
        };

        /// <summary>
        /// The prop sprite, or null where the art does not exist yet. A null leaves the
        /// dashed pocket showing, which is the honest state for coffee, teapot and the
        /// snack bowl - they are specified but unmade.
        /// </summary>
        public static Texture2D? Prop(string id) => id switch
        {
            "ashtray" => GD.Load<Texture2D>($"{PropPath}/ashtray.png"),
            "beer"    => GD.Load<Texture2D>($"{PropPath}/beer.png"),
            _         => null,
        };

        /// <summary>Whether this prop has finished art behind it.</summary>
        public static bool PropIsDrawn(string id) => id is "ashtray" or "beer";

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

        /// <summary>Apply a whole set to one seat's wedge on the felt.</summary>
        public static void ApplyToFelt(TableFelt? felt, int visualSeat, CosmeticSet set)
        {
            if (felt == null) return;
            felt.SetSeatSurface(visualSeat, Surface(set.Surface));
            felt.SetSeatProp(visualSeat, Prop(set.Prop));
        }
    }
}
