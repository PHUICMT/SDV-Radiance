namespace SDVRadiance
{
    /// <summary>Where a body meets the ground, for everything that draws under or beneath one.
    ///
    /// <para>A character's collision box ends a little below the drawn shoes, so the point both the
    /// shadow pass and the water mirror pivot on is the box's bottom lifted by
    /// <see cref="FeetLift"/> pixels. The number lived twice: as ShadowRenderer's own constant and
    /// as a bare 10f in the reflection stamp, under a comment promising the two were the same rule.
    /// House rule 5 says a villager and the player are anchored identically; a number written in two
    /// places is how that rule breaks quietly, so it is written here once and read from both.</para>
    ///
    /// <para>Not every stamp wants the feet: the water MASK cuts a sprite out of the ripple and so
    /// takes the whole box, and a grounding pool sits at the true bottom (ShadowRenderer.Objects
    /// adds the lift back for exactly that). Those are different questions, not copies of this
    /// one.</para></summary>
    internal static class BodyAnchor
    {
        /// <summary>Pixels up from the bottom of a character's collision box to its drawn feet.</summary>
        internal const float FeetLift = 10f;
    }
}
