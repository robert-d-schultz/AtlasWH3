using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtlasWH3.Core;

/// <summary>Per-user settings, <c>%AppData%\AtlasWH3\settings.json</c>: tool paths, recent projects, window placement.</summary>
public sealed class AppSettings
{
    /// <summary>%AppData%\AtlasWH3, or the folder in the ATLASWH3_SETTINGS_DIR environment variable (a throw-away settings
    /// file for testing the first-run setup and walkthroughs without touching the real one).</summary>
    public static string Folder { get; } = Environment.GetEnvironmentVariable("ATLASWH3_SETTINGS_DIR") is { Length: > 0 } dir
        ? Path.GetFullPath(dir)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AtlasWH3");
    public static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>Game install (the folder holding data\ and assembly_kit\); empty = auto-detect.</summary>
    public string GameFolder { get; set; } = "";
    /// <summary>Default assembly kit; empty = &lt;game&gt;\assembly_kit.</summary>
    public string AssemblyKit { get; set; } = "";
    /// <summary>Where AtlasWH3 keeps its own data (output\, cache\); empty = %LocalAppData%\AtlasWH3.</summary>
    public string DataFolder { get; set; } = "";
    /// <summary>The project (.atlaswh3) the Start screen opens editors on; empty = none: the kit's <see cref="MapName"/>.</summary>
    public string CurrentProject { get; set; } = "";
    /// <summary>The map the Start screen opens editors on without a project; empty = <see cref="MapCatalog.DefaultMap"/>.</summary>
    public string MapName { get; set; } = "";
    public List<string> RecentProjects { get; set; } = [];
    public bool DeveloperMode { get; set; }
    /// <summary>Windows whose walkthrough was finished or skipped (keys such as "start", "scene"); it starts by itself
    /// only the first time a window opens.</summary>
    public List<string> ToursSeen { get; set; } = [];
    public Dictionary<string, WindowPlacement> Windows { get; set; } = [];
    /// <summary>Free-form remembered UI values (last tool, last texture, ...).</summary>
    public Dictionary<string, string> Values { get; set; } = [];

    public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static AppSettings? _current;
    /// <summary>Loaded once per process; a missing or unreadable file gives defaults.</summary>
    public static AppSettings Current => _current ??= Load();

    private static AppSettings Load() => Load(FilePath);

    /// <summary>Reads a settings file; a missing or unreadable file gives defaults (a broken one is kept as .bad).</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch (JsonException)
        {
            // keep the broken file for the user instead of overwriting it with defaults on the next save
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (IOException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save() => SaveTo(FilePath);

    /// <summary>Writes the settings to <paramref name="path"/> (through a temp file, so a crash never leaves half a file).</summary>
    public void SaveTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    public bool TourSeen(string key) => ToursSeen.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers that a window's walkthrough was finished or skipped; false when it already was.</summary>
    public bool MarkTourSeen(string key)
    {
        if (TourSeen(key)) return false;
        ToursSeen.Add(key);
        return true;
    }

    /// <summary>Every walkthrough starts again the next time its window opens.</summary>
    public void ResetTours() => ToursSeen.Clear();

    public void AddRecentProject(string path)
    {
        path = Path.GetFullPath(path);
        RecentProjects.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > 10) RecentProjects.RemoveRange(10, RecentProjects.Count - 10);
    }
}
