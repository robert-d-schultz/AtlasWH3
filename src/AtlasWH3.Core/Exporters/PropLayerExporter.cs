using AtlasWH3.Formats.Dds;
using AtlasWH3.Formats.Props;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Exporters;

/// <summary>
/// Regenerates the assembly kit region .layer files from the vanilla compiled global_props.bin, keeping each
/// object's meta tags (building_level_x, settlement_level_x, campaign_map_object_x, day/night) as Terry tag layers.
/// Each region is written to the file the .terry already references for that layer name, so the .terry is not touched.
/// Layers that don't come from global_props (rivers, Default, anything hand-made) are left alone.
/// </summary>
public sealed class PropLayerExporter
{
    public sealed record Result(string TargetDir, string? BackupDir, IReadOnlyList<string> Written,
                                IReadOnlyList<string> Notes, IReadOnlyDictionary<string, int> TaggedByTags);

    private readonly ProjectPaths _paths;

    public PropLayerExporter(ProjectPaths paths) => _paths = paths;

    /// <param name="targetDir">Folder to write into. Defaults to the AK terrain folder (backed up first).</param>
    /// <param name="shift">World offset to add to every entity. Needed when the AK map has already been expanded:
    /// global_props.bin is in vanilla coordinates, so pass the expansion's ShiftX/ShiftZ.</param>
    public Result Export(string? targetDir = null, (double X, double Z)? shift = null, Action<string>? log = null)
    {
        var akDir = _paths.AkTerrainDir;
        var terryPath = Path.Combine(akDir, _paths.MapName + ".terry");
        if (!File.Exists(terryPath))
            throw new FileNotFoundException("Assembly kit .terry project not found.", terryPath);
        var project = TerryProject.Load(terryPath);
        var notes = new List<string>();

        // global_props.bin is vanilla-sized; refuse to drop unshifted layers into an expanded map.
        var height = project.Find("LowFrequencyHeight");
        var header = new byte[148];
        using (var fs = File.OpenRead(_paths.HeightMapDds)) fs.ReadExactly(header);
        var vanilla = DdsHeader.Read(header);
        if (height != null && height.Size != (vanilla.Width, vanilla.Height) && shift == null)
            throw new InvalidOperationException(
                $"The assembly kit map is {height.Size.Width}x{height.Size.Height} but global_props.bin is for the vanilla " +
                $"{vanilla.Width}x{vanilla.Height} map. Pass the expansion shift so the regenerated layers line up.");

        targetDir ??= akDir;
        Directory.CreateDirectory(targetDir);
        var inPlace = string.Equals(Path.GetFullPath(targetDir).TrimEnd('\\'), Path.GetFullPath(akDir).TrimEnd('\\'),
                                    StringComparison.OrdinalIgnoreCase);

        log?.Invoke("reading global_props.bin...");
        var regions = GlobalProps.Load(_paths.GlobalPropsBin).ReadRegions(_paths.MapName);
        var layerFiles = project.LayerFiles();

        var outputs = new List<(string File, RegionObjects Objects)>();
        foreach (var region in regions)
        {
            if (layerFiles.TryGetValue(region.Region, out var id))
                outputs.Add((Path.Combine(targetDir, Path.GetFileName(project.LayerFilePath(id))), region));
            else
                notes.Add($"{region.Region}: no layer of that name in the .terry ({region.Count} objects not written).");
        }

        string? backupDir = null;
        if (inPlace)
            backupDir = ExportBackup.Create(_paths, outputs.Select(o => o.File).Where(File.Exists).ToList(), log);

        log?.Invoke($"writing {outputs.Count} region layers...");
        var written = new List<string>();
        var tagged = new SortedDictionary<string, int>(Comparer<string>.Create(MetaTags.NaturalCompare));
        foreach (var (file, objects) in outputs)
        {
            var text = LayerWriter.Write(objects);
            if (shift is { } s && (s.X != 0 || s.Z != 0))
                text = LayerShifter.Shift(text, s.X, s.Z).Text;
            File.WriteAllText(file, text, new System.Text.UTF8Encoding(false));
            written.Add(file);

            foreach (var tags in objects.Props.Select(p => p.Tags).Concat(objects.Vfx.Select(v => v.Tags))
                         .Concat(objects.PointLights.Select(l => l.Tags)).Concat(objects.CompositeScenes.Select(c => c.Tags))
                         .Where(t => t.Length > 0))
                tagged[tags] = tagged.GetValueOrDefault(tags) + 1;
        }

        var unused = layerFiles.Keys.Except(regions.Select(r => r.Region)).Order().ToList();
        if (unused.Count > 0)
            notes.Add($"Left untouched (not in global_props.bin): {string.Join(", ", unused)}.");
        notes.Add($"{tagged.Values.Sum():N0} objects carry tags, in {tagged.Count} distinct tag layers.");
        return new Result(targetDir, backupDir, written, notes, tagged);
    }
}
