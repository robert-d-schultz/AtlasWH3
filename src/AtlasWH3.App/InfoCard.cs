using System.Windows;
using System.Windows.Controls;

namespace AtlasWH3.App;

/// <summary>Hover info cards: a ToolTip with a bold title and one or two plain sentences, shown after a short delay.
/// Texts live in one table per window (see <see cref="InfoCards"/>); attach with <c>element.Card("key")</c> or, for
/// one-off text, <c>element.Card(title, text)</c>.</summary>
public static class InfoCard
{
    public const int ShowDelayMs = 350;
    public const double MaxWidth = 320;

    /// <summary>The card for a table key (<see cref="InfoCards.Get"/>).</summary>
    public static ToolTip For(string key)
    {
        var (title, text) = InfoCards.Get(key);
        return Make(title, text);
    }

    /// <summary>A card with a bold title (may be empty) and body text.</summary>
    public static ToolTip Make(string title, string text)
    {
        var body = new StackPanel { MaxWidth = MaxWidth };
        if (title.Length > 0)
            body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("Text") });
        return new ToolTip { Content = body, Padding = new Thickness(8, 6, 8, 6) };
    }

    /// <summary>Attach the card for a table key; returns the element for chaining.</summary>
    public static T Card<T>(this T element, string key) where T : FrameworkElement => Attach(element, For(key));

    /// <summary>Attach a one-off card; returns the element for chaining.</summary>
    public static T Card<T>(this T element, string title, string text) where T : FrameworkElement => Attach(element, Make(title, text));

    private static T Attach<T>(T element, ToolTip card) where T : FrameworkElement
    {
        element.ToolTip = card;
        ToolTipService.SetInitialShowDelay(element, ShowDelayMs);
        ToolTipService.SetShowDuration(element, 60_000);
        ToolTipService.SetShowOnDisabled(element, true);
        return element;
    }
}

/// <summary>All info-card texts, keyed "window.item". Each window keeps its own table in a partial file
/// (e.g. InfoCards.Build.cs declares <c>private static readonly Entry[] BuildCards = [...]</c>), so every string for a
/// window is in one place and windows don't edit each other's files. Every static <see cref="Entry"/>[] field of this
/// class is part of the table.</summary>
public static partial class InfoCards
{
    public readonly record struct Entry(string Key, string Title, string Text);

    private static readonly Lazy<Dictionary<string, Entry>> Table = new(() =>
    {
        var table = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in typeof(InfoCards).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
            if (field.FieldType == typeof(Entry[]))
                foreach (var e in (Entry[])field.GetValue(null)!) table[e.Key] = e;
        return table;
    });

    /// <summary>Title and text for a key; an unknown key shows the key itself so a typo is visible, not silent.</summary>
    public static (string Title, string Text) Get(string key) =>
        Table.Value.TryGetValue(key, out var e) ? (e.Title, e.Text) : (key, "(no info card text yet)");

    public static bool Has(string key) => Table.Value.ContainsKey(key);
}
