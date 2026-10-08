using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Editing;

/// <summary>Which layers a <see cref="LayerTreeFilter"/> keeps by their own saved visibility.</summary>
public enum LayerVisibilityFilter { All, Visible, Hidden }

/// <summary>
/// The scene editor's layer-tree filter. It only decides what the tree shows; it never changes visibility.
/// <list type="bullet">
/// <item><b>Text</b>: layer or entity name, entity label or id; case-insensitive substring.</item>
/// <item><b>Region</b>: substring of a layer name (CA names region layers after the region key).</item>
/// <item><b>Type</b>: an exact entity type (<see cref="TerryEntityTypes"/> / entity configuration type).</item>
/// <item><b>Visibility</b>: a layer's own saved state (.terry.user).</item>
/// </list>
/// A layer is kept when it passes the region and visibility tests and, if text or type is set, either its own name
/// matches the text (and no type is set) or it holds a matching entity. Parents of matches are kept.
/// </summary>
public sealed record LayerTreeFilter(string Text = "", string Region = "", string? Type = null,
                                     LayerVisibilityFilter Visibility = LayerVisibilityFilter.All)
{
    public static readonly LayerTreeFilter None = new();

    public bool IsEmpty => Text.Length == 0 && Region.Length == 0 && Type is null && Visibility == LayerVisibilityFilter.All;

    /// <summary>True when entities are tested at all (text or type set).</summary>
    public bool FiltersEntities => Text.Length > 0 || Type is not null;

    private static bool Has(string? haystack, string needle) =>
        needle.Length == 0 || (haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase));

    /// <summary>A non-layer entity: matches the text (name, label or id) and the type.</summary>
    public bool EntityMatches(TerryEntityData e, string label) =>
        (Type is null || string.Equals(e.Type, Type, StringComparison.OrdinalIgnoreCase))
        && (Text.Length == 0 || Has(e.Name, Text) || Has(label, Text) || Has(e.Id, Text));

    /// <summary>The layer's own visibility state passes the visibility filter.</summary>
    public bool VisibilityMatches(bool hidden) => Visibility switch
    {
        LayerVisibilityFilter.Visible => !hidden,
        LayerVisibilityFilter.Hidden => hidden,
        _ => true,
    };

    /// <summary>The layer's name alone satisfies the text part (only when no type is set: a type asks for entities).</summary>
    public bool LayerNameMatchesText(string name) => Text.Length > 0 && Type is null && Has(name, Text);

    /// <summary>
    /// Whether a file layer stays in the tree. <paramref name="entities"/> are its entities with their labels;
    /// <paramref name="hidden"/> is the layer's own saved state.
    /// </summary>
    public bool KeepFileLayer(string name, bool hidden, IEnumerable<(TerryEntityData Entity, string Label)> entities)
    {
        if (!Has(name, Region) || !VisibilityMatches(hidden)) return false;
        if (!FiltersEntities) return true;
        if (LayerNameMatchesText(name)) return true;
        foreach (var (e, label) in entities)
            if (TerryEntityTypes.IsLayerType(e.Type) ? LayerNameMatchesText(e.Name ?? e.Id) : EntityMatches(e, label))
                return true;
        return false;
    }

    /// <summary>
    /// Whether a child node (entity, group or nested layer) of a kept layer stays: a non-layer entity when it matches;
    /// a nested layer or group when its name matches the text or any member below it matches. With no entity filter
    /// every child stays.
    /// </summary>
    public bool KeepChild(TerryEntityData child, string label, IReadOnlyList<TerryEntityData> layerEntities, Func<TerryEntityData, string> labelOf,
                          Func<string, bool>? hiddenOf = null)
    {
        var isLayer = TerryEntityTypes.IsLayerType(child.Type);
        if (isLayer && hiddenOf is not null && Visibility != LayerVisibilityFilter.All && !VisibilityMatches(hiddenOf(child.Id))) return false;
        if (!FiltersEntities) return true;
        if (isLayer || child.Type == TerryEntityTypes.Group)
        {
            if (isLayer && LayerNameMatchesText(child.Name ?? child.Id)) return true;
            // any descendant matches (members of a layer list it in Parents; members of a group point at it via Group)
            var seen = new HashSet<string> { child.Id };
            var frontier = new Queue<string>([child.Id]);
            while (frontier.Count > 0)
            {
                var id = frontier.Dequeue();
                foreach (var m in layerEntities)
                {
                    if (!(m.Parents.Contains(id) || m.Group == id) || !seen.Add(m.Id)) continue;
                    if (TerryEntityTypes.IsLayerType(m.Type) || m.Type == TerryEntityTypes.Group)
                    {
                        if (TerryEntityTypes.IsLayerType(m.Type) && LayerNameMatchesText(m.Name ?? m.Id)) return true;
                        frontier.Enqueue(m.Id);
                    }
                    else if (EntityMatches(m, labelOf(m))) return true;
                }
            }
            return false;
        }
        return EntityMatches(child, label);
    }
}
