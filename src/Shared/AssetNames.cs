namespace SDVRadiance
{
    /// <summary>One spelling for an asset name, shared by the surface map and the label store (and
    /// the label pack test harness, which compiles the label store without the surface map).</summary>
    internal static class AssetNames
    {
        /// <summary>Forward slashes, no locale suffix: anything after the LAST dot that is short
        /// and has no slash in it is a language tag, not part of a path.</summary>
        internal static string Normalise(string name)
        {
            name = name.Replace('\\', '/');
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot < name.Length - 6 || name.IndexOf('/', dot) >= 0)
                return name;
            return name[..dot];
        }
    }
}
