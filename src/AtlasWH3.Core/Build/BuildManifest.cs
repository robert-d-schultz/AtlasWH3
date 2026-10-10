using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AtlasWH3.Core.Build;

/// <summary>
/// The files the last Compile of each step wrote into a build output, so Pack can take exactly the build's own files
/// (<see cref="PackContent.CompiledSource"/>). The default output is the kit's working_data, which also holds CAIME's
/// inputs and BOB's by-products (the devastated folder's full rasters and global_props_devastation_*), none of which
/// ship. Kept in the app cache, one file per map and output folder; a step that fails keeps its previous entry.
/// </summary>
public sealed class BuildManifest
{
    public string Output { get; set; } = "";
    /// <summary>Step name → the files it wrote, relative to <see cref="Output"/> (forward slashes).</summary>
    public SortedDictionary<string, List<string>> Steps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Step name → the path prefixes it rewrites whole (<see cref="Campaign.StepResult.Owns"/>), relative.</summary>
    public SortedDictionary<string, List<string>> Owns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string FileFor(ProjectPaths paths, string map, string output)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normal(output).ToLowerInvariant())))[..8].ToLowerInvariant();
        return Path.Combine(paths.CacheRoot, "builds", $"{map}_{key}.json");
    }

    /// <summary>The manifest of <paramref name="output"/>, or an empty one.</summary>
    public static BuildManifest Load(string file, string output)
    {
        if (File.Exists(file))
        {
            var m = JsonSerializer.Deserialize<BuildManifest>(File.ReadAllText(file), BuildProject.Json);
            if (m is not null && Normal(m.Output).Equals(Normal(output), StringComparison.OrdinalIgnoreCase))
                return new BuildManifest
                {
                    Output = m.Output, Steps = new(m.Steps, StringComparer.OrdinalIgnoreCase), Owns = new(m.Owns, StringComparer.OrdinalIgnoreCase),
                };
        }
        return new BuildManifest { Output = Normal(output) };
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, BuildProject.Json));
    }

    /// <summary>Records what <paramref name="step"/> wrote (a folder stands for every file in it, as the pieces step
    /// reports its pieces); files outside the output folder (the cache) are left out.</summary>
    public void Set(string step, IEnumerable<string> written, IEnumerable<string>? owns = null)
    {
        var root = Normal(Output) + Path.DirectorySeparatorChar;
        Owns[step] = (owns ?? []).Select(Path.GetFullPath)
            .Where(f => f.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .Select(f => f[root.Length..].Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Steps[step] = written.Select(Path.GetFullPath)
            .SelectMany(f => Directory.Exists(f) ? Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories) : [f])
            .Where(f => f.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .Select(f => f[root.Length..].Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every recorded file (relative, forward slashes), each once.</summary>
    public IEnumerable<string> Files => Steps.Values.SelectMany(f => f).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every owned prefix (relative, forward slashes), each once.</summary>
    public IEnumerable<string> Owned => Owns.Values.SelectMany(f => f).Distinct(StringComparer.OrdinalIgnoreCase);

    private static string Normal(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
}
