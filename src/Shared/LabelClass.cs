namespace SDVRadiance
{
    /// <summary>
    /// The label classes a painted tile can carry, by the byte the label files store.
    ///
    /// <para>THE CONTRACT WITH EVERY LABEL PACK. These numbers are written into
    /// <c>labels/water-labels.json</c> and into the packs other people ship, so they are fixed:
    /// a class may be added at the end, never renumbered. They were spelled out as bare numbers
    /// in nine places across five files, one of which kept its own private copy of three of them
    /// and another a hand-typed table of names that had already lost three entries. Adding a
    /// class meant finding every literal.</para>
    ///
    /// <para>The names are the ones the label tool shows, and <see cref="Name"/> is the same list
    /// the dump and the console print, so a class added here appears everywhere at once.</para>
    /// </summary>
    internal static class LabelClass
    {
        public const byte Ground = 0;
        public const byte Water = 1;
        public const byte Wall = 2;
        public const byte Roof = 3;
        /// <summary>A raised walkable surface: a pier, a bridge, a deck over water.</summary>
        public const byte Deck = 4;
        public const byte Void = 5;
        /// <summary>Art that gives off light of its own: a forge, a lamp's bulb, lava's glow.</summary>
        public const byte Emissive = 6;
        /// <summary>A floor polished enough to return a soft image of what stands on it.</summary>
        public const byte ReflectFloor = 7;
        /// <summary>A backed mirror: it returns everything and blocks the light, unlike glass.</summary>
        public const byte Mirror = 8;
        public const byte Ice = 9;
        /// <summary>Water with a direction: a river's pull, a waterfall's drop.</summary>
        public const byte Flowing = 10;
        public const byte Lava = 11;
        /// <summary>A window: a hole in a wall with glass in it, so light passes through.</summary>
        public const byte Window = 12;
        /// <summary>Plain glass: a shop front, a display case, a greenhouse pane.</summary>
        public const byte Glass = 13;
        /// <summary>Hot enough to shimmer above: a forge's mouth, a hot spring.</summary>
        public const byte Hot = 14;

        /// <summary>What a label file writes for a pixel nobody painted: not a class, a gap. The
        /// water gather treats it as liquid where the tile it sits in is liquid art.</summary>
        public const byte Unlabelled = 255;

        /// <summary>Every class in order, so a listing never has to be typed out again.</summary>
        public static readonly string[] Names =
        {
            "ground", "water", "wall", "roof", "deck", "void", "emissive", "reflect_floor",
            "mirror", "ice", "flowing", "lava", "window", "glass", "hot",
        };

        /// <summary>The name of a class, or the number itself if a label pack carries one this
        /// build has never heard of.</summary>
        public static string Name(byte labelClass)
            => labelClass < Names.Length ? Names[labelClass] : labelClass.ToString();

        /// <summary>Whether a class is a liquid surface: something with a top that moves and
        /// returns an image. Ice is in it because the water machinery is what draws ice.</summary>
        public static bool IsLiquid(byte labelClass)
            => labelClass == Water || labelClass == Ice || labelClass == Flowing
               || labelClass == Lava || labelClass == Hot;

        /// <summary>Whether light passes through this class and an image comes back off it:
        /// a window, a plain pane, or a backed mirror.</summary>
        public static bool IsGlassy(byte labelClass)
            => labelClass == Window || labelClass == Glass || labelClass == Mirror;
    }
}
