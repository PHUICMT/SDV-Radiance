using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Two diagnostics that answer questions no other command in this mod can.
    ///
    /// <para>
    /// The first is about the mod's oldest measurement mistake. Every timer here is a Stopwatch
    /// around our own calls, which measures the time spent TELLING the card what to draw, not the
    /// time the card spends drawing it. That is how object shadows read as a rounding error while
    /// costing 1.80 ms. The standard fix is a GPU timestamp query, and the standard write-ups all
    /// assume Direct3D - but this game is MonoGame DesktopGL, so the process speaks OpenGL and
    /// there is no D3D device to ask. OpenGL's equivalent is GL_TIME_ELAPSED, and it is reachable
    /// without any MonoGame support at all, because SDL2 is already loaded in this process and
    /// SDL_GL_GetProcAddress hands back real function pointers for the game's current context.
    /// <c>radiance_gldiag</c> proves that route end to end before anyone builds on it.
    /// </para>
    ///
    /// <para>
    /// The second answers whether a cache that refreshes every N ticks can keep up with the map's
    /// own animated tiles. The map dump records which tiles animate and how many frames they have,
    /// but not how long a frame lasts, so the aliasing question could not be answered offline.
    /// <c>radiance_anim</c> reads the interval off the live tiles.
    /// </para>
    /// </summary>
    internal static partial class ConsoleCommands
    {
        // ---- the GL entry points we need, loaded through SDL rather than through MonoGame ----

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GL_GetProcAddress")]
        private static extern IntPtr SdlGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string proc);

        private delegate IntPtr GlGetStringDel(uint name);
        private delegate IntPtr GlGetStringiDel(uint name, uint index);
        private delegate void GlGetIntegervDel(uint pname, out int data);
        private delegate void GlGenQueriesDel(int n, out uint ids);
        private delegate void GlDeleteQueriesDel(int n, ref uint ids);
        private delegate void GlBeginQueryDel(uint target, uint id);
        private delegate void GlEndQueryDel(uint target);
        private delegate void GlGetQueryObjectivDel(uint id, uint pname, out int p);
        private delegate void GlGetQueryObjectui64vDel(uint id, uint pname, out ulong p);

        private const uint GL_VENDOR = 0x1F00, GL_RENDERER = 0x1F01, GL_VERSION = 0x1F02, GL_EXTENSIONS = 0x1F03;
        private const uint GL_NUM_EXTENSIONS = 0x821D;
        private const uint GL_TIME_ELAPSED = 0x88BF;
        private const uint GL_QUERY_RESULT = 0x8866, GL_QUERY_RESULT_AVAILABLE = 0x8867;

        private static T? Load<T>(string name) where T : Delegate
        {
            IntPtr p = SdlGetProcAddress(name);
            return p == IntPtr.Zero ? null : (T)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        /// <summary>
        /// Report the real GL context this process is running on, and prove (or disprove) that a
        /// GL_TIME_ELAPSED query can be created, run and read back from inside this mod.
        ///
        /// <para>Run it and paste the output. A PASS line means the per-stage GPU timing work is
        /// unblocked; a FAIL line names which step broke, which is the difference between "we need
        /// a different technique" and "we mistyped an entry point".</para>
        /// </summary>
        private static void GlDiag(IMonitor monitor)
        {
            var report = new StringBuilder();
            report.AppendLine("[gldiag] --- graphics backend ---");
            try
            {
                var getString = Load<GlGetStringDel>("glGetString");
                if (getString == null)
                {
                    monitor.Log("[gldiag] FAIL: SDL_GL_GetProcAddress returned nothing for glGetString. "
                        + "Either SDL2 is not the loaded backend or there is no current GL context on this thread.",
                        LogLevel.Warn);
                    return;
                }
                string Str(uint n)
                {
                    IntPtr p = getString(n);
                    return p == IntPtr.Zero ? "(null)" : (Marshal.PtrToStringAnsi(p) ?? "(null)");
                }
                report.AppendLine($"[gldiag] vendor   : {Str(GL_VENDOR)}");
                report.AppendLine($"[gldiag] renderer : {Str(GL_RENDERER)}");
                report.AppendLine($"[gldiag] version  : {Str(GL_VERSION)}");

                // Compatibility contexts still answer GL_EXTENSIONS as one string; core contexts
                // return null there and require the indexed form. Try both rather than assuming
                // which kind of context MonoGame asked SDL for.
                bool timerExt = false;
                int extCount = 0;
                string flat = Str(GL_EXTENSIONS);
                if (flat != "(null)")
                {
                    extCount = flat.Split(' ').Length;
                    timerExt = flat.Contains("GL_ARB_timer_query") || flat.Contains("GL_EXT_timer_query");
                }
                else
                {
                    var getStringi = Load<GlGetStringiDel>("glGetStringi");
                    var getIntegerv = Load<GlGetIntegervDel>("glGetIntegerv");
                    if (getStringi != null && getIntegerv != null)
                    {
                        getIntegerv(GL_NUM_EXTENSIONS, out extCount);
                        for (uint i = 0; i < (uint)extCount; i++)
                        {
                            IntPtr p = getStringi(GL_EXTENSIONS, i);
                            string e = p == IntPtr.Zero ? "" : (Marshal.PtrToStringAnsi(p) ?? "");
                            if (e == "GL_ARB_timer_query" || e == "GL_EXT_timer_query") { timerExt = true; break; }
                        }
                    }
                }
                report.AppendLine($"[gldiag] extensions advertised: {extCount}"
                    + $"   GL_ARB_timer_query / GL_EXT_timer_query: {(timerExt ? "PRESENT" : "absent")}");

                report.AppendLine("[gldiag] --- can this mod actually run a timer query? ---");
                var gen = Load<GlGenQueriesDel>("glGenQueries");
                var begin = Load<GlBeginQueryDel>("glBeginQuery");
                var end = Load<GlEndQueryDel>("glEndQuery");
                var getiv = Load<GlGetQueryObjectivDel>("glGetQueryObjectiv");
                var getu64 = Load<GlGetQueryObjectui64vDel>("glGetQueryObjectui64v");
                var del = Load<GlDeleteQueriesDel>("glDeleteQueries");
                var missing = new List<string>();
                if (gen == null) missing.Add("glGenQueries");
                if (begin == null) missing.Add("glBeginQuery");
                if (end == null) missing.Add("glEndQuery");
                if (getiv == null) missing.Add("glGetQueryObjectiv");
                if (getu64 == null) missing.Add("glGetQueryObjectui64v");
                if (missing.Count > 0)
                {
                    report.AppendLine($"[gldiag] FAIL: entry points not resolvable: {string.Join(", ", missing)}");
                    monitor.Log(report.ToString().TrimEnd(), LogLevel.Info);
                    return;
                }

                uint id = 0;
                gen!(1, out id);
                if (id == 0)
                {
                    report.AppendLine("[gldiag] FAIL: glGenQueries produced no id.");
                    monitor.Log(report.ToString().TrimEnd(), LogLevel.Info);
                    return;
                }
                begin!(GL_TIME_ELAPSED, id);
                end!(GL_TIME_ELAPSED);
                // The result is not ready in the same call. Spin briefly: this is a one-off proof,
                // not the shipping read path, which must ring-buffer and read a frame or three late
                // so it never stalls the pipeline.
                int available = 0;
                for (int i = 0; i < 2000 && available == 0; i++)
                    getiv!(id, GL_QUERY_RESULT_AVAILABLE, out available);
                if (available == 0)
                {
                    report.AppendLine("[gldiag] FAIL: the query never reported a result as available.");
                }
                else
                {
                    getu64!(id, GL_QUERY_RESULT, out ulong ns);
                    report.AppendLine($"[gldiag] PASS: query id {id} returned {ns} ns "
                        + "(a near-zero span is expected here - nothing was drawn between begin and end; "
                        + "what this proves is that the whole route works from inside this mod).");
                }
                if (del != null) del(1, ref id);
            }
            catch (DllNotFoundException)
            {
                monitor.Log("[gldiag] FAIL: SDL2 could not be loaded. This build of the game may not be DesktopGL.",
                    LogLevel.Warn);
                return;
            }
            catch (Exception ex)
            {
                report.AppendLine($"[gldiag] FAIL: {ex.GetType().Name}: {ex.Message}");
            }
            monitor.Log(report.ToString().TrimEnd(), LogLevel.Info);
        }

        /// <summary>
        /// Count the current location's animated tiles and report how fast they actually run.
        ///
        /// <para>The map dump records which tiles animate and how many frames each has, but not the
        /// frame interval, so "does a cache that refreshes every N ticks keep up" could not be
        /// answered from the dump. This reads the interval off the live tiles and converts it to
        /// ticks, because the caches in this mod are all clocked in ticks.</para>
        /// </summary>
        private static void AnimReport(IMonitor monitor)
        {
            GameLocation? location = Game1.currentLocation;
            if (location?.map == null)
            {
                monitor.Log("[anim] no location loaded.", LogLevel.Info);
                return;
            }
            const float MsPerTick = 1000f / 60f;
            var perFamily = new Dictionary<string, int>();
            var intervals = new List<long>();
            var frameCounts = new List<int>();
            int total = 0, mirrorDrawable = 0;

            foreach (var layer in MapLayers.RenderedLayers(location.map, topToBottom: false))
            {
                bool inMirror = MapLayers.TryGetFamily(layer.Id, out string fam) && fam != "AlwaysFront";
                for (int y = 0; y < layer.LayerHeight; y++)
                for (int x = 0; x < layer.LayerWidth; x++)
                {
                    if (layer.Tiles[x, y] is not xTile.Tiles.AnimatedTile at)
                        continue;
                    total++;
                    if (inMirror)
                    {
                        mirrorDrawable++;
                        string key = fam ?? layer.Id;
                        perFamily[key] = perFamily.TryGetValue(key, out int c) ? c + 1 : 1;
                    }
                    intervals.Add(at.FrameInterval);
                    frameCounts.Add(at.TileFrames?.Length ?? 0);
                }
            }

            monitor.Log($"[anim] {location.Name}: {total} animated tiles on rendered layers, "
                + $"{mirrorDrawable} of them on layers the mirror draws (Back/Buildings/Front).", LogLevel.Info);
            if (perFamily.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in perFamily) parts.Add($"{kv.Key} {kv.Value}");
                monitor.Log("[anim] by family: " + string.Join(", ", parts), LogLevel.Info);
            }
            if (intervals.Count == 0)
            {
                monitor.Log("[anim] nothing animated here, so this map cannot answer the cadence question.",
                    LogLevel.Info);
                return;
            }
            intervals.Sort();
            frameCounts.Sort();
            long Pct(List<long> v, double p) => v[Math.Min(v.Count - 1, (int)(v.Count * p))];
            long lo = intervals[0], mid = Pct(intervals, 0.5), hi = intervals[intervals.Count - 1];
            monitor.Log($"[anim] frame interval ms: min {lo}, median {mid}, max {hi}"
                + $"   =>  ticks: min {lo / MsPerTick:0.0}, median {mid / MsPerTick:0.0}, max {hi / MsPerTick:0.0}",
                LogLevel.Info);
            monitor.Log($"[anim] frames per tile: min {frameCounts[0]}, median {frameCounts[frameCounts.Count / 2]}, "
                + $"max {frameCounts[frameCounts.Count - 1]}", LogLevel.Info);

            // The point of the whole command: a cache refreshed every N ticks can only be as fresh
            // as the fastest thing it is trying to follow. Say so in the units the caches use, and
            // name the constant rather than leaving the reader to find it.
            double fastestTicks = lo / MsPerTick;
            monitor.Log($"[anim] the fastest tile here advances every {fastestTicks:0.0} ticks. "
                + $"The mirror scene cache refreshes every {6} ticks (SceneCacheTtlTicks), so it "
                + (fastestTicks >= 6 ? "keeps up with this map." : "cannot keep up with this map and will judder."),
                LogLevel.Info);
        }
    }
}
