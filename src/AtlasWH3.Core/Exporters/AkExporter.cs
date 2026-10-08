using AtlasWH3.Core.Operations;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Exporters;

/// <summary>
/// Writes the edited terrain back to assembly-kit sources so BOB can rebuild the compiled map:
///  - LowFrequencyHeight    → &lt;map&gt;.height.&lt;id&gt;.tif and lf_heights.tif (16-bit)
///  - LowFrequencyHeightSea → &lt;map&gt;.sea_height.&lt;id&gt;.tif and lf_sea_heights.tif (16-bit)
///  - BlendCampaign         → &lt;map&gt;.blend.&lt;id&gt;.tif (8-bit palettised LZW, palette kept from the old file)
/// When a canvas expansion is pending it also pads the AK-only maps (tree paint, climate maps, tile map),
/// shifts every .layer entity, and updates all TerrainMap sizes in the .terry.
/// Every file that will be overwritten is first copied into a timestamped backup folder.
/// </summary>
/// <summary>Tree data to fold into the AK tree paint on export.</summary>
public sealed record TreeExportInput(AtlasWH3.Formats.Trees.CampaignTreeList Trees,
                                     AtlasWH3.Formats.Trees.TreeDatabase? Db,
                                     AtlasWH3.Core.Editing.TreeEditLog Log);

public sealed class AkExporter
{
    public sealed record Result(string TargetDir, string? BackupDir, IReadOnlyList<string> Written, IReadOnlyList<string> Notes);

    /// <summary>Palette index meaning "no tree" in the AK tree paint TIF.</summary>
    public const byte NoTreeIndex = 19;

    private readonly ProjectPaths _paths;

    public AkExporter(ProjectPaths paths) => _paths = paths;

    /// <param name="targetDir">Folder to write into. Defaults to the AK terrain folder. Any other folder gets a
    /// complete copy of the modified sources (read from the AK folder), which is useful for a dry run.</param>
    public Result Export(TerrainData terrain, PendingExpansion? pending = null, string? targetDir = null,
                         Action<string>? log = null, TreeExportInput? trees = null)
    {
        var akDir = _paths.AkTerrainDir;
        var sourceTerry = Path.Combine(akDir, _paths.MapName + ".terry");
        if (!File.Exists(sourceTerry))
            throw new FileNotFoundException("Assembly kit .terry project not found.", sourceTerry);

        targetDir ??= akDir;
        Directory.CreateDirectory(targetDir);
        var inPlace = string.Equals(Path.GetFullPath(targetDir).TrimEnd('\\'), Path.GetFullPath(akDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        var terryPath = Path.Combine(targetDir, _paths.MapName + ".terry");
        var project = TerryProject.Load(File.Exists(terryPath) ? terryPath : sourceTerry);
        var notes = new List<string>();

        var height = Require(project, "LowFrequencyHeight");
        var sea = Require(project, "LowFrequencyHeightSea");
        var blend = Require(project, "BlendCampaign");
        var tree = project.Find("CampaignTree");

        string Target(string fileName) => Path.Combine(targetDir, fileName);
        string Source(string fileName) => Path.Combine(akDir, fileName);

        var heightTif = Path.GetFileName(project.LayerTifPath(height));
        var seaTif = Path.GetFileName(project.LayerTifPath(sea));
        var blendTif = Path.GetFileName(project.LayerTifPath(blend));
        var treeTif = tree != null ? Path.GetFileName(project.LayerTifPath(tree)) : null;

        // Decide whether the pending expansion still has to be applied to the AK-only files.
        var applyExpansion = false;
        if (pending is { IsEmpty: false })
        {
            var (pl, pt, pr, pb) = pending.Padding.Pixels(HexPadding.LfPxPerHex);
            var before = (terrain.Width - pl - pr, terrain.HeightPx - pt - pb);
            if (height.Size == before) applyExpansion = true;
            else if (height.Size == (terrain.Width, terrain.HeightPx))
                notes.Add("Expansion already present in the assembly kit sizes; layers were not shifted again.");
            else
                throw new InvalidOperationException(
                    $"Assembly kit height map is {height.Size.Width}x{height.Size.Height}, which matches neither the " +
                    $"pre-expansion ({before.Item1}x{before.Item2}) nor the expanded size ({terrain.Width}x{terrain.HeightPx}).");
        }

        var layerFiles = Directory.GetFiles(akDir, "*.layer");
        var extraMaps = new[] { "climate_map.png", "climate_map_g.png", "tile_map.png" };

        // Back up everything that will be overwritten.
        var toBackup = new List<string> { heightTif, "lf_heights.tif", seaTif, "lf_sea_heights.tif", blendTif, Path.GetFileName(terryPath) };
        var updateTreePaint = trees is { Log.IsEmpty: false } && treeTif != null && File.Exists(Source(treeTif));
        if (updateTreePaint && !applyExpansion) toBackup.Add(treeTif!);
        if (applyExpansion)
        {
            if (treeTif != null) toBackup.Add(treeTif);
            toBackup.AddRange(extraMaps);
            toBackup.AddRange(layerFiles.Select(Path.GetFileName)!);
        }
        var backupDir = ExportBackup.Create(_paths, toBackup.Select(Target).Where(File.Exists).ToList(), log);

        // Blend palette must match the texture group order; take it from the existing AK blend TIF.
        var (_, blendPalette) = TiffMap.ReadPalette8(Source(blendTif));
        var written = new List<string>();

        log?.Invoke("writing land height...");
        TiffMap.WriteGray16(Target(heightTif), terrain.Height); written.Add(Target(heightTif));
        TiffMap.WriteGray16(Target("lf_heights.tif"), terrain.Height); written.Add(Target("lf_heights.tif"));
        log?.Invoke("writing sea height...");
        TiffMap.WriteGray16(Target(seaTif), terrain.SeaHeight); written.Add(Target(seaTif));
        TiffMap.WriteGray16(Target("lf_sea_heights.tif"), terrain.SeaHeight); written.Add(Target("lf_sea_heights.tif"));
        log?.Invoke("writing ground textures...");
        TiffMap.WritePalette8(Target(blendTif), terrain.BlendGroup, blendPalette, lzw: true); written.Add(Target(blendTif));

        if (applyExpansion)
        {
            var pad = pending!.Padding;

            if (treeTif != null && File.Exists(Source(treeTif)))
            {
                log?.Invoke("padding tree paint...");
                var (paint, palette) = TiffMap.ReadPalette8(Source(treeTif));
                var (l, t, r, b) = pad.Pixels(HexPadding.LfPxPerHex / 4);
                TiffMap.WritePalette8(Target(treeTif), paint.Pad(l, t, r, b, NoTreeIndex), palette, lzw: false);
                written.Add(Target(treeTif));
                tree!.Size = (paint.Width + l + r, paint.Height + t + b);
            }

            foreach (var name in extraMaps)
            {
                if (!File.Exists(Source(name))) continue;
                log?.Invoke($"padding {name}...");
                var map = PngMap.Read(Source(name));
                var pxPerHex = map.Width * HexPadding.LfPxPerHex / (terrain.Width - pad.Pixels(HexPadding.LfPxPerHex).L - pad.Pixels(HexPadding.LfPxPerHex).R);
                var (l, t, r, b) = pad.Pixels(pxPerHex);
                PngMap.Write(Target(name), ExpandCanvas.PadReplicate(map, l, t, r, b));
                written.Add(Target(name));
            }
            notes.Add("tile_map.png was padded by repeating its edges; re-export it from CAIME (Tools > Export) after resizing there.");

            log?.Invoke($"shifting {layerFiles.Length} layers...");
            int shifted = 0, skipped = 0;
            foreach (var layer in layerFiles)
            {
                var stats = LayerShifter.ShiftFile(layer, pending.ShiftX, pending.ShiftZ, Target(Path.GetFileName(layer)));
                shifted += stats.Shifted;
                skipped += stats.SkippedGenerated;
                written.Add(Target(Path.GetFileName(layer)));
            }
            notes.Add($"Shifted {shifted:N0} entities by x +{pending.ShiftX:F3}, z +{pending.ShiftZ:F3} ({skipped} BOB-generated river meshes left for BOB to rebuild).");
        }
        else if (!inPlace)
        {
            // Dry-run folder: make it a complete project by copying the untouched sources.
            foreach (var name in layerFiles.Select(Path.GetFileName).Concat(extraMaps).Append(treeTif))
                if (name != null && File.Exists(Source(name)) && !File.Exists(Target(name)))
                    File.Copy(Source(name), Target(name));
        }

        if (updateTreePaint)
        {
            log?.Invoke("updating tree paint...");
            // Start from the file just padded by the expansion (if any), otherwise from the AK source.
            var paintSource = written.Contains(Target(treeTif!)) ? Target(treeTif!) : Source(treeTif!);
            var (paint, palette) = TiffMap.ReadPalette8(paintSource);
            var changed = TreeExporter.UpdatePaint(paint, palette, trees!.Trees, trees.Db, trees.Log);
            TiffMap.WritePalette8(Target(treeTif!), paint, palette, lzw: false);
            if (!written.Contains(Target(treeTif!))) written.Add(Target(treeTif!));
            notes.Add($"Tree paint: {changed:N0} cells updated from your tree edits.");
        }

        height.Size = (terrain.Width, terrain.HeightPx);
        blend.Size = (terrain.BlendGroup.Width, terrain.BlendGroup.Height);
        sea.Size = (terrain.SeaHeight.Width, terrain.SeaHeight.Height);
        project.Save(terryPath);
        written.Add(terryPath);

        return new Result(targetDir, backupDir, written, notes);
    }

    private static TerryProject.TerrainMap Require(TerryProject project, string type) =>
        project.Find(type) ?? throw new InvalidDataException($"No {type} map in {project.Path}.");
}

/// <summary>Copies files about to be overwritten into output\backups\&lt;map&gt;_&lt;timestamp&gt;.</summary>
internal static class ExportBackup
{
    public static string? Create(ProjectPaths paths, IReadOnlyList<string> files, Action<string>? log)
    {
        if (files.Count == 0) return null;
        var dir = Path.Combine(paths.OutputRoot, "backups", $"{paths.MapName}_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(dir);
        foreach (var file in files)
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
        log?.Invoke($"backed up {files.Count} files to {dir}");
        return dir;
    }
}
