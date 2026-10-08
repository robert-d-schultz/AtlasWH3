using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using AtlasWH3.Formats.Battle;

namespace AtlasWH3.Core.Battle;

/// <summary>
/// Builds a battle project with the Assembly Kit's BOB, headless (Terrain processor, one action at a time, the way
/// Terry's "Process with BOB" starts it), and compares the output with a reference build.
/// </summary>
public static class BobBattleBuild
{
    public const string ConfigName = "_terryclone_battle";
    public static readonly string[] Actions = ["Tilemap", "Low frequency data"];

    /// <summary>The map's rules.bob must turn on save_meta_data_map, or BOB never writes tile_map.tiles.</summary>
    public const string RulesText = "[Terrain]\r\n\tPrefabRoot = art/prefabs/battle\r\n\tsave_meta_data_map = true\r\n\tsave_final_tile_map = false\r\n";

    public static bool RulesOk(string projectDir)
    {
        var path = Path.Combine(projectDir, "rules.bob");
        if (!File.Exists(path)) return false;
        var text = File.ReadAllText(path).Replace(" ", "").Replace("\t", "").ToLowerInvariant();
        return text.Contains("save_meta_data_map=true");
    }

    public static void WriteRules(string projectDir) => File.WriteAllText(Path.Combine(projectDir, "rules.bob"), RulesText);

    /// <summary>A running BOB process and whether it belongs to the given kit.</summary>
    public sealed record RunningProcess(int Id, string Name, string? Path, bool SameKit);

    /// <summary>Running BOB processes. BOB must run one action at a time per kit; ones from other kits are reported too.</summary>
    public static IReadOnlyList<RunningProcess> RunningBob(string kitRoot)
    {
        var bin = System.IO.Path.GetFullPath(System.IO.Path.Combine(kitRoot, "binaries")).TrimEnd('\\') + "\\";
        var result = new List<RunningProcess>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!p.ProcessName.StartsWith("bob.", StringComparison.OrdinalIgnoreCase)) continue;
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* access denied: treat as unknown */ }
                result.Add(new RunningProcess(p.Id, p.ProcessName, path,
                    path == null || path.StartsWith(bin, StringComparison.OrdinalIgnoreCase)));
            }
            catch { /* process exited */ }
        }
        return result;
    }

    public sealed record RunResult(string Action, int? ExitCode, bool TimedOut, TimeSpan Duration, string Log, string ErrorLog)
    {
        public bool Ok => !TimedOut && ExitCode == 0 && ErrorLog.Trim().Length == 0 && !Log.Contains("failed", StringComparison.OrdinalIgnoreCase)
                          && Log.Contains("1 action(s) were selected");
    }

    /// <summary>Runs one Terrain action on raw_data\terrain\battles\&lt;map&gt; of <paramref name="kitRoot"/>.</summary>
    public static async Task<RunResult> RunAsync(string kitRoot, string map, string action, TimeSpan timeout, CancellationToken cancel = default)
    {
        var bin = Path.Combine(kitRoot, "binaries");
        var exe = new[] { "bob.retail.x64.exe", "bob.modder.x64.exe" }.Select(e => Path.Combine(bin, e)).FirstOrDefault(File.Exists)
                  ?? throw new FileNotFoundException($"BOB not found in {bin}");
        string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;");
        var scope = $"<raw>/terrain/battles/{map}/...";
        var config = new StringBuilder()
            .AppendLine("<bob_configuration>")
            .AppendLine("    <processors>\n        <processor>Terrain</processor>\n    </processors>")
            .AppendLine("    <directories>")
            .AppendLine($"        <directory>{Esc($"<raw>/terrain/battles/{map}")}</directory>")
            .AppendLine($"        <directory>{Esc($"<working>/terrain/battles/{map}")}</directory>")
            .AppendLine($"        <directory>{Esc("<working>/")}</directory>")
            .AppendLine("    </directories>")
            .AppendLine("    <global_rules/>\n    <retail>1</retail>\n    <silent>1</silent>\n    <show_errors>0</show_errors>\n    <no_progress>1</no_progress>")
            .AppendLine("    <fail_on_assert>0</fail_on_assert>\n    <scan_perforce>0</scan_perforce>\n    <merge_for_checkin_mode>3</merge_for_checkin_mode>")
            .AppendLine("    <keep_output>1</keep_output>\n    <load_asset_graph>0</load_asset_graph>\n    <clean_asset_graph>0</clean_asset_graph>\n    <get_latest>0</get_latest>")
            .AppendLine($"    <selected_providers>\n        <entry>{Esc(scope)}</entry>\n    </selected_providers>")
            .AppendLine($"    <selected_consumers>\n        <entry>{Esc(scope)}</entry>\n    </selected_consumers>")
            .AppendLine($"    <selected_actions>\n        <action>{Esc(action)}</action>\n    </selected_actions>")
            .AppendLine("</bob_configuration>");
        Directory.CreateDirectory(Path.Combine(bin, "BOB"));
        await File.WriteAllTextAsync(Path.Combine(bin, "BOB", $"{ConfigName}_configuration.xml"), config.ToString().Replace("\r\n", "\n"), cancel);

        var log = Path.Combine(bin, "bob.log");
        var started = DateTime.Now;
        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(new ProcessStartInfo(exe, $"/configuration:{ConfigName} /nosplashscreen /dont_stop_on_error")
        {
            WorkingDirectory = bin,
            UseShellExecute = false,
        })!;
        var timedOut = false;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            limit.CancelAfter(timeout);
            try { await proc.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { timedOut = true; }
        }
        string Fresh(string name)
        {
            var p = Path.Combine(bin, name);
            return File.Exists(p) && File.GetLastWriteTime(p) >= started.AddSeconds(-2) ? File.ReadAllText(p) : "";
        }
        return new RunResult(action, proc.HasExited ? proc.ExitCode : null, timedOut, sw.Elapsed, Fresh("bob.log"), Fresh("bob_error.log"));
    }

    /// <summary>Compares a BOB build (working_data\terrain\battles\&lt;map&gt;) with a reference compiled folder.</summary>
    public static List<string> Compare(string builtDir, string referenceDir, BattleTileDatabase db)
    {
        var report = new List<string>();
        string Hash(string dir, string file)
        {
            var p = Path.Combine(dir, file);
            return File.Exists(p) ? XxHash64.HashToUInt64(File.ReadAllBytes(p)).ToString("x16") : "";
        }
        foreach (var f in new[] { "battle_locations_map.bin", "climate_map.cm", "tile_map.index", "lf_height_map.dds",
                                  "lf_height_map.compressed_map", "lf_sea_height_map.dds", "lf_sea_height_map.compressed_map" })
        {
            var (a, b) = (Hash(builtDir, f), Hash(referenceDir, f));
            report.Add($"{f,-36} {(a == "" ? "missing in build" : b == "" ? "missing in reference" : a == b ? "identical" : "differs")}");
        }

        var builtBlm = Path.Combine(builtDir, "battle_locations_map.bin");
        var refBlm = Path.Combine(referenceDir, "battle_locations_map.bin");
        if (File.Exists(builtBlm) && File.Exists(refBlm))
        {
            var bb = BattleLocationsMapFile.Read(builtBlm);
            var rb = BattleLocationsMapFile.Read(refBlm);
            report.Add($"catchment land grid: {(bb.MetaGrid.SequenceEqual(rb.MetaGrid) ? "identical" : "differs")}");
            var keys = rb.Lists.Select(l => l.Key).Concat(bb.Lists.Select(l => l.Key)).Distinct();
            foreach (var key in keys)
            {
                var ra = rb.Lists.FirstOrDefault(l => l.Key == key).Areas ?? [];
                var ba = bb.Lists.FirstOrDefault(l => l.Key == key).Areas ?? [];
                static string Sig(BlmArea a) => $"{a.Box}|{a.Centre}|{a.Name}|{a.Redirection}|{a.RedirectionCatchment}";
                var same = ra.Select(Sig).GroupBy(s => s).Sum(g => Math.Min(g.Count(), ba.Count(a => Sig(a) == g.Key)));
                report.Add($"  {key,-24} reference {ra.Count,4}  build {ba.Count,4}  identical {same,4}");
            }
        }

        if (File.Exists(Path.Combine(builtDir, "tile_map.tiles")) && File.Exists(Path.Combine(referenceDir, "tile_map.tiles")))
        {
            var bt = BattleTileMapFile.Read(builtDir);
            var rt = BattleTileMapFile.Read(referenceDir);
            if (bt.Width == rt.Width && bt.Height == rt.Height)
            {
                string? Set(BattleTileMapFile t, int i) => t.OwnerLocation(i) is { } loc ? db.TileAt(loc)?.TileSet ?? loc : null;
                var same = 0;
                for (var i = 0; i < bt.W0.Length; i++)
                    if (Set(bt, i) == Set(rt, i)) same++;
                report.Add($"tile map: {same * 100.0 / bt.W0.Length:F2}% of cells have the same tile set");
                var special = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "settlement_cities", "settlement_ports", "resource", "start_pos", "gate_battles", "historical_battles" };
                HashSet<string> Specials(BattleTileMapFile t) => t.Instances()
                    .Where(i => db.TileAt(i.Location) is { } tile && special.Contains(tile.TileSet))
                    .Select(i => $"{BattleTileDatabase.NormaliseLocation(i.Location)}@{i.OriginX},{i.OriginY},{i.Rotation}").ToHashSet();
                var (bs, rs) = (Specials(bt), Specials(rt));
                report.Add($"settlement/resource tiles: reference {rs.Count}, build {bs.Count}, same place and rotation {bs.Intersect(rs).Count()}");
            }
            else report.Add($"tile map size differs: build {bt.Width}x{bt.Height}, reference {rt.Width}x{rt.Height}");
        }
        return report;
    }
}
