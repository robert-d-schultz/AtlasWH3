using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.App.Scene;

/// <summary>
/// Scripted check of the terrain tools, in-process: land raise + undo/redo, sea set-to-value, tree paint / fill / erase,
/// trees following the ground, then Save, verifying the TIFs hold the edited rasters and only their pixel bytes changed.
/// Start with: AtlasWH3.App --scene &lt;copy of a campaign project&gt;.terry --terrain-selftest &lt;out dir&gt;. It saves into
/// the project, so never point it at the real kit.
/// </summary>
public sealed partial class SceneWindow
{
    public async Task TerrainSelfTestAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var steps = new JsonArray();
        var report = new JsonObject { ["steps"] = steps };
        void Step(string name, bool ok, string? detail = null) =>
            steps.Add(new JsonObject { ["step"] = name, ["ok"] = ok, ["detail"] = detail, ["status"] = _status.Text });
        static string Hash<T>(T[] data) where T : unmanaged =>
            Convert.ToHexString(SHA1.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan())))[..12];
        try
        {
            while (!_loadedOnce) await Task.Delay(200);
            var tools = TerrainTools;
            var s = tools.Session;
            Step("session", s is not null, s is null ? null : string.Join(", ", new[] { KitTarget.Land, KitTarget.Sea, KitTarget.Trees }.Select(t => $"{t}={Path.GetFileName(s.PathOf(t))}")));
            if (s is null || _model.Terrain is not var (land, worldW, worldH)) return;
            _rightTabs!.SelectedIndex = 1;
            var landPath = s.PathOf(KitTarget.Land)!;
            var landFileBefore = File.ReadAllBytes(landPath);

            // A land point: the highest pixel of a band through the middle of the map.
            var (bx, by) = (0, 0);
            for (var y = land.Height * 2 / 5; y < land.Height * 3 / 5; y += 7)
                for (var x = land.Width / 4; x < land.Width * 3 / 4; x += 7)
                    if (land[x, y] > land[bx, by]) (bx, by) = (x, y);
            var (wx, wz) = s.Frame(KitTarget.Land).ToWorld(bx, by);
            wx -= 6; // off the peak, on a slope with trees more likely
            _view.FitTo(wx - 25, wz - 18, wx + 25, wz + 18);
            var landHash = Hash(land.Data);
            var (px, py) = ((int)Math.Round(s.Frame(KitTarget.Land).ToPixel(wx, wz).Px), (int)Math.Round(s.Frame(KitTarget.Land).ToPixel(wx, wz).Py));
            var before = land[px, py];
            var nearTree = _model.Trees.OrderBy(t => (t.X - wx) * (t.X - wx) + (t.Z - wz) * (t.Z - wz)).FirstOrDefault();
            await Shot(outDir, "t00_loaded");

            async Task Shot3D(string name, int settleMs)
            {
                _centre.SelectedIndex = 1;
                for (var wait = 0; wait < 900 && (_view3d.Snapshot(8, 8) is null || !_view3d.TerrainReady); wait++) await Task.Delay(100);
                await Task.Delay(settleMs); // an edit's re-mesh
                _view3d.Camera.Target = new System.Numerics.Vector3((float)wx + 4, (float)_model.GroundY(wx + 4, wz), (float)wz);
                _view3d.Camera.Distance = 22;
                _view3d.Camera.Pitch = 0.55f;
                _view3d.Camera.Yaw = 0.4f;
                _view3d.Invalidate();
                for (var wait = 0; wait < 60 && !_view3d.ModelsSettled; wait++) await Task.Delay(100);
                await Task.Delay(500);
                Save3D(outDir, name);
                _centre.SelectedIndex = 0;
            }
            await Shot3D("t00_3d_before", 0);

            tools.TargetIndex = 1;
            tools.HeightModeIndex = (int)KitHeightMode.Raise;
            tools.Radius = 3;
            tools.Strength = 1;
            tools.Softness = 0.5;
            tools.Begin(wx, wz);
            tools.Drag(wx + 1.5, wz);
            tools.End();
            Step("land raise", land[px, py] > before && s.IsDirty(KitTarget.Land), $"pixel ({px}, {py}) {before} -> {land[px, py]}");
            var movedTree = _model.Trees.FirstOrDefault(t => t.X == nearTree.X && t.Z == nearTree.Z);
            Step("trees follow the ground", nearTree.TreeId is null || movedTree.Y > nearTree.Y,
                 nearTree.TreeId is null ? "no tree nearby" : $"{nearTree.TreeId} y {nearTree.Y:0.###} -> {movedTree.Y:0.###}");
            await Shot(outDir, "t01_land_raised");
            tools.Undo();
            Step("land undo", Hash(land.Data) == landHash && !s.IsDirty(KitTarget.Land));
            tools.Redo();
            Step("land redo", land[px, py] > before && s.IsDirty(KitTarget.Land));

            tools.TargetIndex = 2;
            tools.HeightModeIndex = (int)KitHeightMode.SetValue;
            tools.ValueText = "16000";
            tools.Radius = 4;
            tools.Softness = 0;
            tools.Begin(wx, wz);
            tools.End();
            Step("sea set to value", s.RawAt(KitTarget.Sea, wx, wz) == 16000, $"sea raw {s.RawAt(KitTarget.Sea, wx, wz)}");
            await Shot(outDir, "t02_sea");

            tools.TargetIndex = 3;
            var counts = s.TreeCounts();
            var species = Enumerable.Range(0, 256).Where(i => i != KitTerrainEditSession.NoTreeIndex && counts[i] > 0)
                .OrderByDescending(i => counts[i]).Select(i => (byte)i).ToList();
            if (species.Count >= 2)
            {
                tools.TreeModeIndex = 0;
                tools.SelectTreeIndex(species[1]);
                tools.Radius = 1.5;
                var (tx, tz) = (wx + 8, wz);
                tools.Begin(tx, tz);
                tools.Drag(tx + 3, tz);
                tools.End();
                var (hc, hr) = s.NearestHex(tx, tz);
                var (cx, cz) = s.HexCentre(hc, hr);
                var tree = _model.Trees.FirstOrDefault(t => Math.Abs(t.X - cx) < 0.25 && Math.Abs(t.Z - cz) < 0.25);
                var ids = _model.TreeDb?.ColourGroups().GetValueOrDefault((uint)s.PaletteRgb(species[1])) ?? [];
                Step("tree paint", s.HexIndex(hc, hr) == species[1] && ids.Contains(tree.TreeId), $"hex ({hc}, {hr}) index {s.HexIndex(hc, hr)}, tree {tree.TreeId}");

                tools.TreeModeIndex = 2;
                tools.SelectTreeIndex(species[0]);
                var fillBefore = s.TreeCounts()[species[0]];
                tools.Begin(tx, tz);
                tools.End();
                Step("tree fill connected", s.HexIndex(hc, hr) == species[0] && s.TreeCounts()[species[0]] > fillBefore,
                     $"{s.TreeCounts()[species[0]] - fillBefore} hexes");

                tools.TreeModeIndex = 1;
                var treesBefore = _model.Trees.Count;
                tools.Begin(tx, tz);
                tools.End();
                Step("tree erase", s.HexIndex(hc, hr) == KitTerrainEditSession.NoTreeIndex && _model.Trees.Count < treesBefore,
                     $"{treesBefore} -> {_model.Trees.Count} trees");
                await Shot(outDir, "t03_trees");
            }
            else Step("tree paint", false, "fewer than two species on the map");

            await Shot3D("t04_3d_after", 3000);

            var dirty = new[] { KitTarget.Land, KitTarget.Sea, KitTarget.Trees }.Where(s.IsDirty).ToList();
            var saved = tools.SaveNow();
            var landFileAfter = File.ReadAllBytes(landPath);
            var changedBytes = landFileAfter.Length == landFileBefore.Length
                ? Enumerable.Range(0, landFileAfter.Length).Count(i => landFileAfter[i] != landFileBefore[i]) : -1;
            var layout = TiffMap.ReadLayout(landPath);
            var pixelStart = layout.StripOffsets.Min();
            var headerSame = layout.Compression != 1 || landFileAfter.AsSpan(0, (int)pixelStart).SequenceEqual(landFileBefore.AsSpan(0, (int)pixelStart));
            Step("save", saved && !s.IsDirty() && Hash(TiffMap.ReadGray16(landPath).Data) == Hash(land.Data)
                         && (s.Sea is null || Hash(TiffMap.ReadGray16(s.PathOf(KitTarget.Sea)!).Data) == Hash(s.Sea.Data))
                         && (s.TreeMap is null || Hash(TiffMap.ReadPalette8(s.PathOf(KitTarget.Trees)!).Indices.Data) == Hash(s.TreeMap.Data)),
                 $"saved {string.Join(", ", dirty)}");
            Step("land TIF: only pixel bytes changed", changedBytes > 0 && headerSame, $"{changedBytes} bytes differ, same length {landFileAfter.Length == landFileBefore.Length}");
            Step("journal backup", s.Journal.History().Count > 0, s.Journal.Dir);
            report["state"] = tools.StateText;
            await Shot(outDir, "t05_saved");
        }
        catch (Exception ex)
        {
            Step("exception", false, ex.ToString());
        }
        finally
        {
            report["ok"] = steps.All(n => n!["ok"]!.GetValue<bool>());
            await File.WriteAllTextAsync(Path.Combine(outDir, "terrain_selftest.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            System.Windows.Application.Current.Shutdown();
        }
    }
}
