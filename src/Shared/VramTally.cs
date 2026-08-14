using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;

namespace SDVRadiance
{
    /// <summary>
    /// How much graphics memory this mod is holding, and what for.
    ///
    /// <para>
    /// Every other probe here measures TIME. This one measures a cost that takes no time at all
    /// and can still ruin a machine: memory held. A render target pool that is never released
    /// shows up as 0.000 ms in every timer we own, right up until the card runs out and the
    /// driver starts evicting textures, at which point the game stutters in a way no per-frame
    /// timer will ever attribute to us.
    /// </para>
    ///
    /// <para>
    /// This exists because of one report and one correction. A player said the game was smooth
    /// before installing this mod and is not smooth after, with every feature switched off - and
    /// the reply drafted here was that our timers read zero so it was probably not us. That is
    /// the same mistake made earlier the same day, when the timers read near zero while object
    /// shadows were eating half the frame: a probe that cannot see something is not evidence
    /// that the something is absent. The game was fine before the mod. Something we hold or do
    /// is the cause until proven otherwise, and the first candidate is the one no timer covers.
    /// </para>
    ///
    /// <para>
    /// Tracking is by weak reference so this can never be the thing that keeps a target alive:
    /// a bucket whose targets have all been collected reports zero without any bookkeeping at
    /// the disposal sites.
    /// </para>
    /// </summary>
    internal static class VramTally
    {
        private sealed class Entry
        {
            public readonly WeakReference<Texture2D> Target;
            public readonly long Bytes;
            public Entry(Texture2D rt, long bytes) { Target = new WeakReference<Texture2D>(rt); Bytes = bytes; }
        }

        private static readonly Dictionary<string, List<Entry>> _buckets = new();
        private static readonly object _lock = new();

        /// <summary>Bytes a target occupies. Surface formats used here are all 4 bytes per pixel;
        /// anything else is counted at 4 as well, which over-reports rather than under-reports,
        /// and a memory figure that errs low is worse than useless.</summary>
        private static long SizeOf(Texture2D rt) => (long)rt.Width * rt.Height * 4;

        /// <summary>Record a newly created texture under a human-readable bucket. Takes any
        /// Texture2D, not just render targets: the first version counted only the latter and so
        /// reported a total that left out every mask, gradient and noise texture the mod uploads.
        /// A memory figure that quietly omits categories is worse than no figure, because it gets
        /// quoted.</summary>
        internal static T Track<T>(T rt, string bucket) where T : Texture2D
        {
            if (rt == null) return rt!;
            lock (_lock)
            {
                if (!_buckets.TryGetValue(bucket, out var list))
                    _buckets[bucket] = list = new List<Entry>();
                list.Add(new Entry(rt, SizeOf(rt)));
            }
            return rt;
        }

        /// <summary>Total live bytes, and the per-bucket breakdown, dropping entries whose targets
        /// have been disposed or collected.</summary>
        internal static (long Total, List<(string Bucket, int Count, long Bytes)> Rows) Snapshot()
        {
            var rows = new List<(string, int, long)>();
            long total = 0;
            lock (_lock)
            {
                foreach (var kv in _buckets)
                {
                    int count = 0; long bytes = 0;
                    kv.Value.RemoveAll(e => !e.Target.TryGetTarget(out Texture2D? rt) || rt.IsDisposed);
                    foreach (var e in kv.Value)
                    {
                        count++; bytes += e.Bytes;
                    }
                    if (count > 0) { rows.Add((kv.Key, count, bytes)); total += bytes; }
                }
            }
            rows.Sort((a, b) => b.Item3.CompareTo(a.Item3));
            return (total, rows);
        }

        internal static string Describe()
        {
            var (total, rows) = Snapshot();
            if (rows.Count == 0)
                return "Graphics memory held by this mod: nothing allocated yet.";
            var text = new System.Text.StringBuilder();
            text.AppendLine($"Graphics memory held by this mod: {total / (1024.0 * 1024.0):0.0} MB");
            foreach (var (bucket, count, bytes) in rows)
                text.AppendLine($"  {bucket,-28} {count,4} x  {bytes / (1024.0 * 1024.0),7:0.0} MB");
            text.AppendLine();
            text.AppendLine("This is memory HELD, not time spent, and no other number in this report can see it.");
            text.AppendLine("It matters on cards with little to spare: once the card is full the driver starts");
            text.AppendLine("moving textures in and out, which stutters, and which no per-frame timer will blame");
            text.AppendLine("on this mod. If this figure is large next to a 4 GB card carrying a big texture pack,");
            text.AppendLine("say so in the bug report - it is the one cost that survives turning every effect off.");
            return text.ToString().TrimEnd();
        }
    }
}
