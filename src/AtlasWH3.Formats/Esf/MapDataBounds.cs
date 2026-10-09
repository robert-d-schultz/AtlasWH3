namespace AtlasWH3.Formats.Esf;

/// <summary>
/// The campaign map's world bounds in map_data.esf: CAMPAIGN_THEATRE's two vec2 values (min x, min z) and
/// (max x, max z). CAIME writes them; BOB's campaign hex grid (trees, region lookup) is built on them. They can differ
/// from the campaign_map_playable_areas row: the user's Old World row says 1068.1111 × 748.1, its map_data.esf (and its
/// tree list header) 1367.396 × 1368.7428.
/// </summary>
public readonly record struct MapDataBounds(float MinX, float MinZ, float MaxX, float MaxZ)
{
    public float Width => MaxX - MinX;
    public float Height => MaxZ - MinZ;

    public static MapDataBounds Read(string path) => Read(EsfTree.Read(path));

    public static MapDataBounds Read(EsfTree tree)
    {
        var theatre = tree.Root.Descendants("CAMPAIGN_THEATRE").FirstOrDefault()
            ?? throw new InvalidDataException("map_data.esf has no CAMPAIGN_THEATRE record.");
        var vectors = theatre.Values.Where(v => v.Type == 0x0c).Take(2).Select(v => v.Vec2).ToList();
        if (vectors.Count < 2) throw new InvalidDataException("map_data.esf's CAMPAIGN_THEATRE has no bounds.");
        return new MapDataBounds(vectors[0].X, vectors[0].Y, vectors[1].X, vectors[1].Y);
    }
}
