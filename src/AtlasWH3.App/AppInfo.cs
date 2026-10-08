using System.Reflection;

namespace AtlasWH3.App;

/// <summary>Product name and version (from Directory.Build.props) for titles and the About box.</summary>
public static class AppInfo
{
    public const string Product = "AtlasWH3";
    public const string Tagline = "Total War: THREE KINGDOMS campaign map editor";

    /// <summary>e.g. "0.1.0-alpha.1" (without the +commit suffix).</summary>
    public static string Version { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    /// <summary>The commit the build came from, when the version carries one.</summary>
    public static string? Commit { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } v && v.Contains('+')
            ? v[(v.IndexOf('+') + 1)..] : null;

    /// <summary>"AtlasWH3 — context".</summary>
    public static string Title(string context) => $"{Product} — {context}";
}
