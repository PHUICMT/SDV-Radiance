using Microsoft.Xna.Framework;

namespace SDVRadiance
{
    /// <summary>A saved-profile chip: a load button plus its red delete X.</summary>
    internal sealed class TunerChip
    {
        public TunerTextButton Load = null!;
        public Rectangle Delete;
        public NamedProfile Profile = null!;
    }
}
