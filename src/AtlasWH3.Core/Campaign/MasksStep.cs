using System.Diagnostics;
using AtlasWH3.Formats.Dds;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign;

/// <summary>
/// The terrain-map actions of BOB's default group (Color Overlay, Color Overlay (Sea), Snow Mask, Corruption Mask,
/// Event Area Mask), from the .terry's composited maps (<see cref="TerrainComposite"/>), stored as the TIFs are (row 0
/// first, not flipped) and written through BOB's DirectXTex path (<see cref="DirectXTex"/>). Byte-identical to BOB's on
/// IEE and Old World:
///  - colour_overlay.dds, lf_sea_colour.dds: ColorOverlay / ColorOverlaySea as BC1_UNORM with the full mip chain (RGBA8
///    levels). A map with no visible layer comes out flat grey 127 (BOB's 0.5, truncated), as Old World's ColorOverlay;
///  - snow_mask.dds: SnowMask stretched one pixel bigger each way, rounded up to whole blocks (3200 × 1941 → 3204 × 1944)
///    by the linear resize, then BC4_UNORM with the full mip chain (R8 levels);
///  - corruption_mask.dds: CorruptionMask as R8_UNORM with the full mip chain;
///  - event_area_mask.dds: EventAreaMask's indices as R8_UINT, one level.
/// patch_mask.dds comes from the Tilemap action (<see cref="TileListStep"/>); BOB's Patch Visibility Mask writes nothing
/// while the PatchVisibilityMask map has no layers (both fixtures), and with layers it is not reproduced (noted).
/// lf_normal.dds is the Campaign Heightmap action's (NVTT), not one of these.
/// </summary>
public sealed class MasksStep : ICampaignBuildStep
{
    public string Name => "masks";
    public string ReplacesBobAction => "Color Overlay, Color Overlay (Sea), Snow Mask, Corruption Mask, Event Area Mask";
    public IReadOnlyList<string> DependsOn => [];

    private static readonly (string Type, string File)[] Maps =
    [
        ("ColorOverlay", "colour_overlay.dds"), ("ColorOverlaySea", "lf_sea_colour.dds"), ("SnowMask", "snow_mask.dds"),
        ("CorruptionMask", "corruption_mask.dds"), ("EventAreaMask", "event_area_mask.dds"),
    ];

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        if (!File.Exists(ctx.TerryFile)) return [$"missing {ctx.TerryFile}"];
        var project = TerryProject.Load(ctx.TerryFile);
        var missing = new List<string>();
        foreach (var (type, _) in Maps)
        {
            if (project.Find(type) is not { } map) continue;
            try { TerrainComposite.Inputs(project, map); }
            catch (FileNotFoundException e) { missing.Add(e.Message); }
        }
        return missing;
    }

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var project = TerryProject.Load(ctx.TerryFile);
        Directory.CreateDirectory(ctx.TerrainOutDir);
        var written = new List<string>();
        var notes = new List<string>();
        foreach (var (type, file) in Maps)
        {
            if (project.Find(type) is not { } map) { notes.Add($"no {type} map in the .terry: {file} not written"); continue; }
            ctx.Log($"{file}...");
            var path = ctx.OutFile(file);
            switch (type)
            {
                case "ColorOverlay" or "ColorOverlaySea": WriteOverlay(project, map, path); break;
                case "SnowMask": WriteSnow(project, path); break;
                case "CorruptionMask": WriteCorruption(project, path); break;
                default: WriteEventArea(project, path); break;
            }
            written.Add(path);
            if (TerrainComposite.Inputs(project, map).Count == 0) notes.Add($"{type} has no visible layer");
            ctx.Cancel.ThrowIfCancellationRequested();
        }
        if (project.Find("PatchVisibilityMask") is { } patch && patch.Layers.Any(l => l.Composited))
            notes.Add("PatchVisibilityMask has visible layers: BOB's Patch Visibility Mask would write patch_mask.dds from them; " +
                      "not reproduced (no fixture has any), patch_mask.dds stays the Tilemap one");
        return new StepResult(Name, written, notes, sw.Elapsed);
    }

    /// <summary>A colour overlay as BC1 with every mip.</summary>
    public static void WriteOverlay(TerryProject project, TerryProject.TerrainMap map, string path)
    {
        var (w, h) = map.Size;
        byte[] rgba;
        if (TerrainComposite.Inputs(project, map).Count == 0)
        {
            rgba = new byte[w * h * 4];
            for (var i = 0; i < rgba.Length; i += 4) { rgba[i] = rgba[i + 1] = rgba[i + 2] = 127; rgba[i + 3] = 255; }
        }
        else
        {
            var overlay = TerrainComposite.Overlay(project, map.Type);   // 0xAABBGGRR: R, G, B, A in memory
            rgba = new byte[w * h * 4];
            Buffer.BlockCopy(overlay.Data, 0, rgba, 0, rgba.Length);
        }
        WriteMipped(path, w, h, DdsHeader.DxgiBc1Unorm,
            DirectXTex.Mips(rgba, w, h, 4, round: true).Select(l => DirectXTex.CompressBc1(l.Data, l.Width, l.Height)));
    }

    /// <summary>The snow_mask.dds size of a SnowMask map: one more pixel each way, rounded up to whole 4 × 4 blocks.</summary>
    public static (int Width, int Height) SnowSize(int width, int height) => ((width + 1 + 3) / 4 * 4, (height + 1 + 3) / 4 * 4);

    public static void WriteSnow(TerryProject project, string path)
    {
        var mask = TerrainComposite.Mask(project, "SnowMask");
        var (w, h) = SnowSize(mask.Width, mask.Height);
        var top = DirectXTex.Resize(mask.Data, mask.Width, mask.Height, 1, w, h, round: false);
        WriteMipped(path, w, h, DdsHeader.DxgiBc4Unorm,
            DirectXTex.Mips(top, w, h, 1, round: false).Select(l => DirectXTex.CompressBc4(l.Data, l.Width, l.Height)));
    }

    public static void WriteCorruption(TerryProject project, string path)
    {
        var mask = TerrainComposite.Mask(project, "CorruptionMask");
        WriteMipped(path, mask.Width, mask.Height, DdsHeader.DxgiR8Unorm,
            DirectXTex.Mips(mask.Data, mask.Width, mask.Height, 1, round: false).Select(l => l.Data));
    }

    public static void WriteEventArea(TerryProject project, string path)
    {
        var (indices, _) = TerrainComposite.Indexed(project, "EventAreaMask");
        using var f = File.Create(path);
        f.Write(DdsHeader.BuildDx10(indices.Width, indices.Height, DdsHeader.DxgiR8Uint, false, 1));
        f.Write(indices.Data);
    }

    /// <summary>A DX10 DDS with the full mip chain; <paramref name="levels"/> are the payloads, top level first.</summary>
    private static void WriteMipped(string path, int w, int h, uint format, IEnumerable<byte[]> levels)
    {
        var blockCompressed = format is DdsHeader.DxgiBc1Unorm or DdsHeader.DxgiBc4Unorm;
        using var f = File.Create(path);
        f.Write(DdsHeader.BuildDx10(w, h, format, blockCompressed, blockCompressed ? 8 : 1, DirectXTex.MipCount(w, h)));
        foreach (var level in levels) f.Write(level);
    }
}
