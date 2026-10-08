using System.Security.Cryptography;
using System.Text.Json;

namespace AtlasWH3.Core.Editing;

/// <summary>
/// Undo journal for editors that rewrite whole files (prop layers, tile_map.png). Each committed batch snapshots the
/// files it is about to overwrite into &lt;dir&gt;\&lt;seq&gt;\ and appends a line to journal.jsonl with the files' hashes
/// before and after, so batches can be undone, or rolled back to a named checkpoint (checkpoints.json).
/// </summary>
public sealed class FileJournal(string dir)
{
    public sealed record HistoryEntry(int Seq, DateTime Time, string Label, List<HistoryFile> Files);
    /// <summary>Before/After are content hashes; null means the file did not exist (created, or deleted by the batch).</summary>
    public sealed record HistoryFile(string Path, string? Before, string? After);

    public string Dir { get; } = dir;
    private string JournalPath => Path.Combine(Dir, "journal.jsonl");
    private string CheckpointsPath => Path.Combine(Dir, "checkpoints.json");

    /// <summary>output\&lt;kind&gt;\&lt;map&gt;, plus a hash of the kit root when it isn't the default one, so edits to a copy
    /// of the kit never share an undo journal with the real one.</summary>
    public static string EditDir(ProjectPaths paths, string kind)
    {
        var root = Path.GetFullPath(paths.AssemblyKitRoot).TrimEnd('\\', '/');
        var isDefault = root.Equals(Path.GetFullPath(new ProjectPaths().AssemblyKitRoot).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        var name = isDefault ? paths.MapName
            : $"{paths.MapName}_{Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..8].ToLowerInvariant()}";
        return Path.Combine(paths.OutputRoot, kind, name);
    }

    public List<HistoryEntry> History() =>
        File.Exists(JournalPath)
            ? File.ReadAllLines(JournalPath).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<HistoryEntry>(l)!).ToList()
            : [];

    public int Top => History() is { Count: > 0 } h ? h[^1].Seq : 0;

    /// <summary>Snapshots each file, then calls its save action (which writes the new content to that path, or deletes it), and
    /// journals the batch. Returns the batch's seq.</summary>
    public int Commit(IEnumerable<(string Path, Action<string> Save)> files, string label)
    {
        var history = History();
        var seq = history.Count == 0 ? 1 : history[^1].Seq + 1;
        var snap = Path.Combine(Dir, seq.ToString("D5"));
        Directory.CreateDirectory(snap);
        var entries = new List<HistoryFile>();
        foreach (var (path, save) in files)
        {
            var before = File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null;
            if (before is not null) File.Copy(path, Path.Combine(snap, Path.GetFileName(path)), overwrite: true);
            save(path);
            entries.Add(new HistoryFile(path, before, File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null));
        }
        File.AppendAllText(JournalPath, JsonSerializer.Serialize(new HistoryEntry(seq, DateTime.Now, label, entries)) + "\n");
        return seq;
    }

    /// <summary>Restores the files of the last <paramref name="steps"/> batches. Refuses when a file changed since
    /// (another tool or a hand edit) unless forced.</summary>
    public List<HistoryEntry> Undo(int steps = 1, bool force = false)
    {
        var history = History();
        var undone = new List<HistoryEntry>();
        for (var i = 0; i < steps && history.Count > 0; i++)
        {
            var last = history[^1];
            foreach (var f in last.Files)
            {
                var now = File.Exists(f.Path) ? Hash(File.ReadAllBytes(f.Path)) : null;
                if (now != f.After && !force)
                    throw new InvalidOperationException($"{Path.GetFileName(f.Path)} changed since edit {last.Seq} ({last.Label}); pass force to overwrite");
            }
            var snap = Path.Combine(Dir, last.Seq.ToString("D5"));
            foreach (var f in last.Files)
            {
                if (f.Before is null) File.Delete(f.Path);
                else File.Copy(Path.Combine(snap, Path.GetFileName(f.Path)), f.Path, overwrite: true);
            }
            Directory.Delete(snap, recursive: true);
            history.RemoveAt(history.Count - 1);
            undone.Add(last);
        }
        File.WriteAllLines(JournalPath, history.Select(h => JsonSerializer.Serialize(h)));
        var checkpoints = Checkpoints();
        var top = history.Count == 0 ? 0 : history[^1].Seq;
        foreach (var stale in checkpoints.Where(c => c.Value > top).Select(c => c.Key).ToList()) checkpoints.Remove(stale);
        SaveCheckpoints(checkpoints);
        return undone;
    }

    public Dictionary<string, int> Checkpoints() =>
        File.Exists(CheckpointsPath) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(CheckpointsPath))! : [];

    private void SaveCheckpoints(Dictionary<string, int> checkpoints)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(CheckpointsPath, JsonSerializer.Serialize(checkpoints, new JsonSerializerOptions { WriteIndented = true }));
    }

    public int Checkpoint(string label)
    {
        var seq = Top;
        var checkpoints = Checkpoints();
        checkpoints[label] = seq;
        SaveCheckpoints(checkpoints);
        return seq;
    }

    public List<HistoryEntry> Rollback(string label, bool force = false)
    {
        if (!Checkpoints().TryGetValue(label, out var seq)) throw new KeyNotFoundException($"no checkpoint '{label}'");
        var count = History().Count(h => h.Seq > seq);
        return Undo(count, force);
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes));
}
