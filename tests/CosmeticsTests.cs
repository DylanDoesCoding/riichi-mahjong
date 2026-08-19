// =============================================================================
// CosmeticsTests.cs
// Tests for the cosmetics catalogue and the CosmeticSet wire format.
//
// The wire form ("surface|frame|prop|emblem") crosses the network: the server
// stores it and relays it, and it originates on another client. So the two
// things worth proving are that every default and every CPU seat resolves to a
// real option, and that Deserialise turns any string — including malformed or
// hostile input from a peer — into a valid set rather than throwing or leaving a
// broken table. TEST_PLAN.md section A.
// =============================================================================

using System;
using RiichiMahjong.Core;

static class CosmeticsTests
{
    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n[ Cosmetics catalogue ]\n");
        int pass = 0, fail = 0;

        void Test(string name, bool result)
        {
            Console.WriteLine($"  {(result ? "✓" : "✗")}  {name}");
            if (result) pass++; else fail++;
        }

        var slots = new[]
        {
            CosmeticSlot.Surface, CosmeticSlot.Frame,
            CosmeticSlot.Prop,    CosmeticSlot.Emblem,
        };

        static bool Same(CosmeticSet a, CosmeticSet b)
            => a.Surface == b.Surface && a.Frame == b.Frame
            && a.Prop == b.Prop && a.Emblem == b.Emblem;

        static bool AllValid(CosmeticSet set)
            => CosmeticCatalogue.IsValid(CosmeticSlot.Surface, set.Surface)
            && CosmeticCatalogue.IsValid(CosmeticSlot.Frame,   set.Frame)
            && CosmeticCatalogue.IsValid(CosmeticSlot.Prop,    set.Prop)
            && CosmeticCatalogue.IsValid(CosmeticSlot.Emblem,  set.Emblem);

        // =====================================================================
        // 1. Every slot default is a real option
        // =====================================================================

        foreach (var slot in slots)
            Test($"Default for {slot} is a valid option",
                CosmeticCatalogue.IsValid(slot, CosmeticCatalogue.DefaultFor(slot)));

        // A fresh set is nothing but defaults, so it too must be entirely valid.
        Test("A fresh CosmeticSet is entirely valid", AllValid(new CosmeticSet()));

        // =====================================================================
        // 2. Every CPU seat set is valid — guards the hard-coded CPU tables
        // =====================================================================

        for (int seat = 0; seat < 4; seat++)
            Test($"CPU seat {seat} set is entirely valid",
                AllValid(CosmeticSet.ForCpuSeat(seat)));

        // No CPU ever shows the dashed placeholder: the prop must be a finished one,
        // never "none" or the still-unmade snack bowl.
        for (int seat = 0; seat < 4; seat++)
        {
            string prop = CosmeticSet.ForCpuSeat(seat).Prop;
            Test($"CPU seat {seat} prop is a finished prop (not none/bowl)",
                prop != "none" && prop != "bowl"
                && CosmeticCatalogue.IsValid(CosmeticSlot.Prop, prop));
        }

        // Deterministic, and stable under seat indices outside 0..3 (the ForCpuSeat
        // arithmetic wraps with Math.Abs(seat) % 4).
        Test("ForCpuSeat is deterministic",
            Same(CosmeticSet.ForCpuSeat(1), CosmeticSet.ForCpuSeat(1)));
        Test("ForCpuSeat wraps seat 4 onto seat 0",
            Same(CosmeticSet.ForCpuSeat(4), CosmeticSet.ForCpuSeat(0)));
        Test("ForCpuSeat handles a negative seat and stays valid",
            AllValid(CosmeticSet.ForCpuSeat(-1)));

        // =====================================================================
        // 3. Serialise / Deserialise round-trips
        // =====================================================================

        Test("Wire form has the surface|frame|prop|emblem shape",
            new CosmeticSet { Surface = "slate", Frame = "brass", Prop = "beer", Emblem = "bars" }
                .Serialise() == "slate|brass|beer|bars");

        var custom = new CosmeticSet
        {
            Surface = "oxblood", Frame = "neon", Prop = "teapot", Emblem = "crescent",
        };
        Test("Round-trip preserves a fully custom set",
            Same(CosmeticSet.Deserialise(custom.Serialise()), custom));
        Test("Round-trip preserves the default set",
            Same(CosmeticSet.Deserialise(new CosmeticSet().Serialise()), new CosmeticSet()));

        // -- Defensive cases: malformed input from a peer must not throw or break --

        Test("Empty string deserialises to a valid set",
            AllValid(CosmeticSet.Deserialise("")));
        Test("Null deserialises to a valid set",
            AllValid(CosmeticSet.Deserialise(null)));
        Test("Whitespace-only deserialises to a valid set",
            AllValid(CosmeticSet.Deserialise("   ")));

        // A short (two-field) string fills the missing slots from defaults.
        var partial = CosmeticSet.Deserialise("tatami|brass");
        Test("Two-field string keeps its fields and defaults the rest",
            partial.Surface == "tatami" && partial.Frame == "brass"
            && partial.Prop == CosmeticCatalogue.DefaultFor(CosmeticSlot.Prop)
            && partial.Emblem == CosmeticCatalogue.DefaultFor(CosmeticSlot.Emblem)
            && AllValid(partial));

        // An unknown id in every slot falls back to a valid default in every slot.
        Test("Unknown id per slot falls back to valid defaults",
            AllValid(CosmeticSet.Deserialise("bogus|bogus|bogus|bogus")));

        // A mix of good and bad ids keeps the good and defaults the bad.
        var mixed = CosmeticSet.Deserialise("slate|nope|beer|nope");
        Test("Mixed valid/invalid ids keep the valid and default the invalid",
            mixed.Surface == "slate" && mixed.Prop == "beer"
            && mixed.Frame == CosmeticCatalogue.DefaultFor(CosmeticSlot.Frame)
            && mixed.Emblem == CosmeticCatalogue.DefaultFor(CosmeticSlot.Emblem));

        // Extra fields beyond the four are ignored, not fatal.
        Test("Extra wire fields are ignored",
            Same(CosmeticSet.Deserialise("felt|plain|coffee|none|extra|junk"), new CosmeticSet()));

        // Surrounding whitespace on each field is trimmed before validation.
        Test("Field whitespace is trimmed before validation",
            Same(CosmeticSet.Deserialise(" oxblood | neon | teapot | crescent "), custom));

        return (pass, fail);
    }
}
