// =============================================================================
// Cosmetics.cs
// What a player can make theirs about their quadrant of the table.
//
// The brief for this is "a real person's spot at a real table, with their stuff
// on it" - not a recoloured rectangle. So the slots are a surface, a frame, a
// prop and an emblem, and the prop is the point rather than the decoration.
//
// A cosmetic set travels with the *player*, not with a screen position: every
// client rotates the seats to put itself at the bottom, so the set is applied
// to whichever wedge that player is sitting at on this screen.
//
// Lives in Core so the client and the server agree on the same identifiers -
// the server has to store them and relay them, and a mismatch between the two
// would show as someone else's table quietly rendering wrong.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace RiichiMahjong.Core
{
    public enum CosmeticSlot
    {
        Surface,
        Frame,
        Prop,
        Emblem,
    }

    /// <summary>One option in one slot.</summary>
    public record CosmeticOption(string Id, string Name, bool IsFree);

    /// <summary>
    /// The catalogue of what exists. The unlock model is mixed - a free starter set
    /// plus unlockables - so each option carries whether it is free.
    /// </summary>
    public static class CosmeticCatalogue
    {
        public static readonly IReadOnlyList<CosmeticOption> Surfaces = new[]
        {
            new CosmeticOption("felt",    "Felt",    true),
            new CosmeticOption("tatami",  "Tatami",  true),
            new CosmeticOption("oxblood", "Oxblood", true),
            new CosmeticOption("slate",   "Slate",   true),
        };

        public static readonly IReadOnlyList<CosmeticOption> Frames = new[]
        {
            new CosmeticOption("plain",  "Plain",  true),
            new CosmeticOption("brass",  "Brass",  false),
            new CosmeticOption("carved", "Carved", false),
            new CosmeticOption("neon",   "Neon",   false),
        };

        // Ashtray, beer, coffee and teapot have finished sprites; only the snack bowl is
        // still designed-but-unmade, and its pocket draws as a dashed placeholder until
        // the art exists.
        public static readonly IReadOnlyList<CosmeticOption> Props = new[]
        {
            new CosmeticOption("none",    "None",       true),
            new CosmeticOption("ashtray", "Ashtray",    false),
            new CosmeticOption("beer",    "Beer",       false),
            new CosmeticOption("coffee",  "Coffee",     true),
            new CosmeticOption("teapot",  "Teapot",     false),
            new CosmeticOption("bowl",    "Snack bowl", false),
        };

        public static readonly IReadOnlyList<CosmeticOption> Emblems = new[]
        {
            new CosmeticOption("none",     "None",     true),
            new CosmeticOption("circle",   "Ring",     true),
            new CosmeticOption("diamond",  "Diamond",  true),
            new CosmeticOption("bars",     "Bars",     true),
            new CosmeticOption("crescent", "Crescent", true),
        };

        public static IReadOnlyList<CosmeticOption> For(CosmeticSlot slot) => slot switch
        {
            CosmeticSlot.Surface => Surfaces,
            CosmeticSlot.Frame   => Frames,
            CosmeticSlot.Prop    => Props,
            CosmeticSlot.Emblem  => Emblems,
            _                    => Array.Empty<CosmeticOption>(),
        };

        /// <summary>Whether an id is a real option in that slot. Used to sanitise input.</summary>
        public static bool IsValid(CosmeticSlot slot, string id)
            => For(slot).Any(o => o.Id == id);

        public static string DefaultFor(CosmeticSlot slot) => slot switch
        {
            CosmeticSlot.Surface => "felt",
            CosmeticSlot.Frame   => "plain",
            CosmeticSlot.Prop    => "coffee",
            CosmeticSlot.Emblem  => "none",
            _                    => "",
        };
    }

    /// <summary>One player's chosen set.</summary>
    public class CosmeticSet
    {
        public string Surface { get; set; } = CosmeticCatalogue.DefaultFor(CosmeticSlot.Surface);
        public string Frame   { get; set; } = CosmeticCatalogue.DefaultFor(CosmeticSlot.Frame);
        public string Prop    { get; set; } = CosmeticCatalogue.DefaultFor(CosmeticSlot.Prop);
        public string Emblem  { get; set; } = CosmeticCatalogue.DefaultFor(CosmeticSlot.Emblem);

        public string Get(CosmeticSlot slot) => slot switch
        {
            CosmeticSlot.Surface => Surface,
            CosmeticSlot.Frame   => Frame,
            CosmeticSlot.Prop    => Prop,
            CosmeticSlot.Emblem  => Emblem,
            _                    => "",
        };

        public void Set(CosmeticSlot slot, string id)
        {
            if (!CosmeticCatalogue.IsValid(slot, id)) return;

            switch (slot)
            {
                case CosmeticSlot.Surface: Surface = id; break;
                case CosmeticSlot.Frame:   Frame   = id; break;
                case CosmeticSlot.Prop:    Prop    = id; break;
                case CosmeticSlot.Emblem:  Emblem  = id; break;
            }
        }

        public CosmeticSet Clone() => new()
        {
            Surface = Surface, Frame = Frame, Prop = Prop, Emblem = Emblem,
        };

        /// <summary>
        /// Compact wire form: "surface|frame|prop|emblem". Four ids in a fixed order is
        /// enough, and it keeps the protocol change to a single string per seat.
        /// </summary>
        public string Serialise() => $"{Surface}|{Frame}|{Prop}|{Emblem}";

        /// <summary>
        /// Parse a wire form, falling back to defaults for anything unrecognised.
        /// This is a trust boundary - the string arrives from the server, which got it
        /// from another client - so every field is validated against the catalogue and
        /// a bad value becomes a default rather than an exception or a broken table.
        /// </summary>
        public static CosmeticSet Deserialise(string? wire)
        {
            var set = new CosmeticSet();
            if (string.IsNullOrWhiteSpace(wire)) return set;

            var parts = wire.Split('|');
            var slots = new[]
            {
                CosmeticSlot.Surface, CosmeticSlot.Frame,
                CosmeticSlot.Prop,    CosmeticSlot.Emblem,
            };

            for (int i = 0; i < slots.Length && i < parts.Length; i++)
                set.Set(slots[i], parts[i].Trim());

            return set;
        }

        /// <summary>
        /// A stable set for a CPU seat, derived from the seat index so a solo game
        /// still looks like four people sat down rather than one player at a bare table.
        /// Deterministic, so the same seat looks the same every game.
        /// </summary>
        public static CosmeticSet ForCpuSeat(int seat)
        {
            var surfaces = CosmeticCatalogue.Surfaces;
            var emblems  = CosmeticCatalogue.Emblems;

            // Only the finished props are used, so a CPU never shows a dashed pocket.
            string[] cpuProps = { "coffee", "ashtray", "beer", "coffee" };

            int index = Math.Abs(seat) % 4;
            return new CosmeticSet
            {
                Surface = surfaces[index % surfaces.Count].Id,
                Frame   = "plain",
                Prop    = cpuProps[index % cpuProps.Length],
                Emblem  = emblems[(index + 1) % emblems.Count].Id,
            };
        }
    }
}
