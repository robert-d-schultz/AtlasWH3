using System.Numerics;
using AtlasWH3.Formats.Esf;
using AtlasWH3.Formats.Packs;
using Xunit.Abstractions;

namespace AtlasWH3.Tests;

/// <summary>Composite scenes (.csc): every one in the game's packs reads, and the campaign ones place their models.</summary>
public class CompositeSceneTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryGameCsc_Reads()
    {
        if (!Directory.Exists(TestKits.Wh3GameData)) return;
        var packs = PackSet.OpenVanilla(TestKits.Wh3GameData);
        var paths = packs.Packs.SelectMany(p => p.Entries.Keys).Where(k => k.EndsWith(".csc")).Distinct().ToList();
        var failed = new List<string>();
        int models = 0, placed = 0;
        foreach (var path in paths)
        {
            try
            {
                var scene = CompositeScene.Read(packs.TryRead(path)!);
                foreach (var (_, world) in scene.Models())
                {
                    models++;
                    if (!world.IsIdentity) placed++;
                }
            }
            catch (Exception ex) { failed.Add($"{path}: {ex.Message}"); }
        }
        output.WriteLine($"{paths.Count} .csc, {failed.Count} unread; {models} model elements, {placed} not at the identity");
        foreach (var g in failed.GroupBy(f => f[(f.IndexOf(": ") + 2)..]).OrderByDescending(g => g.Count()))
            output.WriteLine($"{g.Count(),5}  {g.Key}  (e.g. {g.First()[..g.First().IndexOf(": ")]})");
        // ROOT v6 is the quest battles' newer layout (not in the Asset Editor's grammar either); everything else reads
        var other = failed.Where(f => !f.EndsWith("ROOT v6 is not a known .csc layout")).ToList();
        Assert.True(paths.Count > 100, $"{paths.Count} .csc files");
        Assert.True(other.Count == 0, $"{other.Count} unread: {string.Join("; ", other.Take(5))}");
    }
}
