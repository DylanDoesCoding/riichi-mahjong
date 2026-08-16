// =============================================================================
// DoloTokens.cs
// The design tokens from the Dolo UI redesign handoff (passes 02-10).
//
// Every colour, radius, elevation and type size in the redesign resolves to a
// value here.  Nothing in the UI should hard-code a hex colour or a font size:
// if a value is worth using twice it belongs in this file.
//
// Source: design_handoff_dolo_ui/README.md, "Design tokens".
// =============================================================================

using Godot;

namespace RiichiMahjong.UI
{
    /// <summary>Immutable palette, geometry and type scale for the Dolo redesign.</summary>
    public static class DoloTokens
    {
        // =====================================================================
        // Colour - surfaces
        // =====================================================================

        /// <summary>Table felt. Wedge surface cosmetic option, and the pass-10 default.</summary>
        public static readonly Color Felt    = new("#2c4633");
        public static readonly Color Slate   = new("#2b3a44");
        public static readonly Color Tatami  = new("#514a2b");
        public static readonly Color Oxblood = new("#452a2e");
        public static readonly Color Rail    = new("#3d2b1d");

        /// <summary>
        /// Pass 10: the four per-seat felt tints collapse to this single tone so the
        /// felt goes quiet and the tiles carry the state. Seat identity moves to the
        /// wind glyph on the seat plate instead.
        /// </summary>
        public static readonly Color FeltQuiet = new("#2b3a2a");

        // Backgrounds - never more than two of these on one screen.
        public static readonly Color Card      = new("#1d1712");
        public static readonly Color InsetPane = new("#171209");
        public static readonly Color DeepField = new("#120e0a");
        public static readonly Color Page      = new("#14100c");

        // =====================================================================
        // Colour - ink
        // =====================================================================

        public static readonly Color Brass    = new("#c49b5c");
        public static readonly Color Ivory    = new("#efe5d3");
        public static readonly Color DoraGold = new("#f2c14e");

        public static readonly Color BodyText  = new("#b2a28c");
        public static readonly Color DimText   = new("#8c7a63");
        public static readonly Color MonoLabel = new("#96856d");
        public static readonly Color MonoDim   = new("#6f6252");

        public static readonly Color Positive = new("#93bf93");
        public static readonly Color Negative = new("#c98d86");

        /// <summary>Gains on the results screen - brighter than plain ivory.</summary>
        public static readonly Color GainText = new("#f0e7d5");

        // =====================================================================
        // Colour - state
        // =====================================================================

        public static readonly Color RiichiCyan = new("#6fd2e0");
        public static readonly Color FuritenRed = new("#8d3230");

        /// <summary>The riichi stick laid on the felt (pass 10): 52 x 5 with a 1px dark outline.</summary>
        public static readonly Color RiichiStick = new("#f3efe2");

        /// <summary>135-degree hatch drawn across furiten wait tiles: 3px on 6px.</summary>
        public static readonly Color FuritenHatch = new(0.078f, 0.063f, 0.047f, 0.62f);

        // =====================================================================
        // Colour - hairlines
        // =====================================================================
        // Brass at four prominences. Pick by how much the edge should assert itself.

        public static Color Hairline(float alpha) => new(Brass.R, Brass.G, Brass.B, alpha);

        public static readonly Color HairlineFaint  = Hairline(0.14f);
        public static readonly Color HairlineSoft   = Hairline(0.18f);
        public static readonly Color HairlineMedium = Hairline(0.28f);
        public static readonly Color HairlineStrong = Hairline(0.50f);

        /// <summary>Outer glow behind the 2px brass focus ring.</summary>
        public static readonly Color FocusGlow = Hairline(0.16f);

        // =====================================================================
        // Geometry - corner radii
        // =====================================================================

        public const int RadiusCard  = 8;
        public const int RadiusBoard = 6;   // screen boards and buttons
        public const int RadiusInset = 5;   // inset panels and text fields
        public const int RadiusTile  = 4;

        // =====================================================================
        // Geometry - sizing rules
        // =====================================================================

        /// <summary>Text fields are 52px tall with the label above, never a placeholder doing double duty.</summary>
        public const int FieldHeight = 52;

        /// <summary>Every interactive target is at least this tall. Non-negotiable on touch.</summary>
        public const int MinTouchTarget = 44;

        public const int FocusRingWidth = 2;

        // =====================================================================
        // Elevation
        // =====================================================================

        public const int ShadowCard   = 50;
        public const int ShadowButton = 18;
        public const int ShadowTile   = 6;

        public static readonly Color ShadowCardColor   = new(0f, 0f, 0f, 0.55f);
        public static readonly Color ShadowButtonColor = new(0f, 0f, 0f, 0.50f);
        public static readonly Color ShadowTileColor   = new(0f, 0f, 0f, 0.45f);

        public static readonly Vector2 ShadowCardOffset   = new(0, 20);
        public static readonly Vector2 ShadowButtonOffset = new(0, 8);
        public static readonly Vector2 ShadowTileOffset   = new(0, 2);

        // =====================================================================
        // Type scale - desktop (1920 x 1080)
        // =====================================================================
        // Never below 12px on desktop.

        public const int SizeScreenTitle = 52;
        public const int SizeSectionHead = 40;
        public const int SizeNameLarge   = 36;
        public const int SizeNameSmall   = 28;
        public const int SizeButton      = 22;
        public const int SizeRow         = 17;
        public const int SizeBody        = 15;
        public const int SizeBodySmall   = 14;
        public const int SizeMonoLabel   = 13;
        public const int SizeMonoSmall   = 12;

        // Results screen - the one place numbers get large.
        public const int SizeRankFirst   = 76;
        public const int SizeRankOther   = 52;
        public const int SizeScoreFirst  = 40;
        public const int SizeScoreOther  = 32;
        public const int SizePointsFirst = 44;
        public const int SizePointsOther = 34;

        // =====================================================================
        // Type scale - phone landscape (896 x 414)
        // =====================================================================

        public const int PhoneSizeTitle = 22;
        public const int PhoneSizeName  = 15;
        public const int PhoneSizeBody  = 13;
        public const int PhoneSizeMono  = 12;

        // =====================================================================
        // Font weights (variable-axis values for Source Sans 3)
        // =====================================================================

        public const int WeightRegular  = 400;   // labels
        public const int WeightSemiBold = 600;   // names, calls, headings
    }
}
