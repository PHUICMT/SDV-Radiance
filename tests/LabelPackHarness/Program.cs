// Runs the real LabelPacks and LabelStore code against a fabricated Mods tree, because compiling is
// not running and this mod has no test project. Throwaway: it lives in the scratchpad, not the repo.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using SDVRadiance;
using StardewModdingAPI;

int failures = 0;

void Check(string what, bool ok, string detail = "")
{
    Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + (detail.Length == 0 ? "" : "   " + detail));
    if (!ok) failures++;
}

string root = Path.Combine(Path.GetTempPath(), "radiance-pack-harness-" + Guid.NewGuid().ToString("N")[..8]);
string mods = Path.Combine(root, "Mods");
string ourMod = Path.Combine(mods, "SDV-Radiance");
Directory.CreateDirectory(Path.Combine(ourMod, "labels"));

// Our own bundled labels: two sheets, one of which a pack will later try to steal.
File.WriteAllText(Path.Combine(ourMod, "labels", "water-labels.json"), Pack(new[]
{
    ("spring_beach", 0, (byte)1),
    ("vanilla_town", 0, (byte)1),
}));

// A well-behaved mod: ships one tilesheet and labels exactly that sheet.
string goodMod = Path.Combine(mods, "00_Bundle", "[CP] Good Mod");
Directory.CreateDirectory(Path.Combine(goodMod, "assets"));
File.WriteAllText(Path.Combine(goodMod, "manifest.json"), "{\"UniqueID\":\"someone.GoodMod\"}");
WriteFakePng(Path.Combine(goodMod, "assets", "good_sheet.png"));
File.WriteAllText(Path.Combine(goodMod, LabelPacks.FileName), Pack(new[] { ("good_sheet", 0, (byte)11) }, producedFor: "someone.GoodMod"));

// A greedy mod in the same bundle: ships nothing, paints somebody else's sheets, including ours.
string greedyMod = Path.Combine(mods, "00_Bundle", "[CP] Greedy Mod");
Directory.CreateDirectory(greedyMod);
File.WriteAllText(Path.Combine(greedyMod, "manifest.json"), "{\"UniqueID\":\"someone.GreedyMod\"}");
File.WriteAllText(Path.Combine(greedyMod, LabelPacks.FileName), Pack(new[]
{
    ("spring_beach", 0, (byte)9),
    ("good_sheet", 0, (byte)9),
}));

// A mod that ships art and a pack that will not parse at all.
string brokenMod = Path.Combine(mods, "Broken Mod");
Directory.CreateDirectory(brokenMod);
File.WriteAllText(Path.Combine(brokenMod, "manifest.json"), "{\"UniqueID\":\"someone.BrokenMod\"}");
WriteFakePng(Path.Combine(brokenMod, "broken_sheet.png"));
File.WriteAllText(Path.Combine(brokenMod, LabelPacks.FileName), "{ this is not json");

var (monitor, warningCount) = Monitors.Create();
var packs = LabelPacks.Discover(ourMod, monitor);

Console.WriteLine("discovery");
Check("found every pack, and not our own folder", packs.Count == 3, $"count={packs.Count}");
var byFolder = new Dictionary<string, LabelPack>(StringComparer.OrdinalIgnoreCase);
foreach (var pack in packs)
    byFolder[pack.OwningModFolder] = pack;
Check("owner is the pack's own mod folder inside a bundle, not the bundle",
    byFolder.ContainsKey("00_Bundle/[CP] Good Mod"), string.Join(" | ", byFolder.Keys));
Check("a square bracket in a folder name is not treated as a pattern",
    byFolder.ContainsKey("00_Bundle/[CP] Greedy Mod"));
Check("owned art is the mod's own only",
    byFolder["00_Bundle/[CP] Good Mod"].OwnedSheets.Contains("good_sheet")
    && !byFolder["00_Bundle/[CP] Good Mod"].OwnedSheets.Contains("spring_beach"));
Check("a mod shipping no art owns nothing", byFolder["00_Bundle/[CP] Greedy Mod"].OwnedSheets.Count == 0);
Check("producedFor is read when present", byFolder["00_Bundle/[CP] Good Mod"].ProducedFor == "someone.GoodMod");
Check("producedFor is absent, not an error, when the pack omits it", byFolder["00_Bundle/[CP] Greedy Mod"].ProducedFor == null);

Console.WriteLine("loading");
var store = new LabelStore(Path.Combine(ourMod, "labels"), packs, monitor);
Check("the bundled labels loaded", store.Get("Maps/vanilla_town", 0) != null);
Check("a pack painted the sheet it owns", store.Get("assets/good_sheet.png", 0)?[0] == 11);
Check("the greedy pack did not repaint our vanilla sheet", store.Get("spring_beach", 0)?[0] == 1);
Check("the greedy pack did not repaint another mod's sheet", store.Get("good_sheet", 0)?[0] == 11);
Check("an unreadable pack left everything else alone", store.SheetCount == 3, $"sheets={store.SheetCount}");
Check("only the loadable packs are counted", store.PackCount == 2, $"packs={store.PackCount}");
Check("the refusals were logged as warnings", warningCount() >= 2, $"warnings={warningCount()}");
Check("the report names the files", store.DescribeSources().Contains("[CP] Greedy Mod"));
Console.WriteLine("--- report ---");
Console.WriteLine(store.DescribeSources());

Console.WriteLine("no packs at all, which is every existing player");
string bare = Path.Combine(root, "Bare", "Mods", "SDV-Radiance");
Directory.CreateDirectory(Path.Combine(bare, "labels"));
File.WriteAllText(Path.Combine(bare, "labels", "water-labels.json"), Pack(new[] { ("spring_beach", 0, (byte)1) }));
var (bareMonitor, bareWarningCount) = Monitors.Create();
var barePacks = LabelPacks.Discover(bare, bareMonitor);
var bareStore = new LabelStore(Path.Combine(bare, "labels"), barePacks, bareMonitor);
Check("nothing is discovered", barePacks.Count == 0);
Check("the bundled labels are unaffected", bareStore.SheetCount == 1 && bareStore.Get("spring_beach", 0)?[0] == 1);
Check("nothing is warned about", bareWarningCount() == 0, $"warnings={bareWarningCount()}");
Check("the report says so plainly", bareStore.DescribeSources() == "bundled only", bareStore.DescribeSources());

// The old constructor still has to behave exactly as it did, since other code may call it.
var oldWay = new LabelStore(Path.Combine(bare, "labels"), bareMonitor);
Check("the one-argument constructor is unchanged", oldWay.SheetCount == 1 && oldWay.PackCount == 0);

Directory.Delete(root, recursive: true);
Console.WriteLine(failures == 0 ? "\nall checks passed" : $"\n{failures} check(s) FAILED");
return failures == 0 ? 0 : 1;

static string Pack(IEnumerable<(string sheet, int tile, byte value)> tiles, string? producedFor = null)
{
    var bySheet = new Dictionary<string, List<(int tile, byte value)>>();
    foreach (var (sheet, tile, value) in tiles)
    {
        if (!bySheet.TryGetValue(sheet, out var list))
            bySheet[sheet] = list = new List<(int, byte)>();
        list.Add((tile, value));
    }
    var text = new StringBuilder("{");
    if (producedFor != null)
        text.Append($"\"producedFor\":\"{producedFor}\",");
    text.Append("\"sheets\":{");
    bool firstSheet = true;
    foreach (var (sheet, list) in bySheet)
    {
        if (!firstSheet) text.Append(',');
        firstSheet = false;
        text.Append($"\"{sheet}\":{{\"tiles\":{{");
        bool firstTile = true;
        foreach (var (tile, value) in list)
        {
            if (!firstTile) text.Append(',');
            firstTile = false;
            byte[] pixels = new byte[256];
            Array.Fill(pixels, value);
            text.Append($"\"{tile}\":\"{Convert.ToBase64String(pixels)}\"");
        }
        text.Append("}}");
    }
    text.Append("}}");
    return text.ToString();
}

static void WriteFakePng(string path) => File.WriteAllBytes(path, new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' });

/// <summary>
/// IMonitor, generated at runtime. Written by hand it would have to name the parameter type of
/// VerboseLog, which is an interpolated string handler in an internal namespace: a proxy satisfies
/// every member without the harness needing to know any of their signatures.
/// </summary>
public class MonitorProxy : DispatchProxy
{
    public int Warnings;

    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
    {
        if (method?.Name is "Log" or "LogOnce" && args is { Length: 2 })
        {
            var level = (LogLevel)(args[1] ?? LogLevel.Trace);
            if (level >= LogLevel.Warn) Warnings++;
            Console.WriteLine($"       [{level}] {args[0]}");
        }
        if (method?.Name == "get_IsVerbose") return false;
        return null;
    }
}

internal static class Monitors
{
    public static (IMonitor monitor, Func<int> warnings) Create()
    {
        IMonitor monitor = DispatchProxy.Create<IMonitor, MonitorProxy>();
        var proxy = (MonitorProxy)monitor;
        return (monitor, () => proxy.Warnings);
    }
}
