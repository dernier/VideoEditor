using System.Text.Json;
using VideoEditor.Core.Model;

namespace VideoEditor.Core;

/// <summary>
/// Lettura e scrittura di un progetto su disco, con cronologia per annulla/ripeti condivisa
/// tra editor e riga di comando (le istantanee stanno in <c>.vedit/history</c>).
/// </summary>
public sealed class ProjectStore
{
    public const string FileName = "project.vedit.json";
    private const int MaxHistory = 300;

    public string Directory { get; }
    public string ProjectFile => Path.Combine(Directory, FileName);
    public string WorkDir => Path.Combine(Directory, ".vedit");
    public string HistoryDir => Path.Combine(WorkDir, "history");
    public string CacheDir => Path.Combine(WorkDir, "cache");
    public string ProxyDir => Path.Combine(WorkDir, "proxies");
    public string TempDir => Path.Combine(WorkDir, "tmp");
    public string TranscriptDir => Path.Combine(Directory, "transcripts");
    public string MediaDir => Path.Combine(Directory, "media");
    public string OutputDir => Path.Combine(Directory, "output");

    public ProjectStore(string directory)
    {
        Directory = Path.GetFullPath(directory);
    }

    public bool Exists => File.Exists(ProjectFile);

    /// <summary>
    /// Cerca il progetto partendo da un percorso (file o cartella) e risalendo le cartelle padre.
    /// </summary>
    public static ProjectStore? Find(string? start)
    {
        var path = Path.GetFullPath(string.IsNullOrEmpty(start) ? Environment.CurrentDirectory : start);
        if (File.Exists(path)) path = Path.GetDirectoryName(path)!;
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, FileName))) return new ProjectStore(dir.FullName);
        }
        return null;
    }

    public static ProjectStore Create(string directory, Project project)
    {
        var store = new ProjectStore(directory);
        if (store.Exists) throw new InvalidOperationException($"Esiste già un progetto in {store.Directory}.");
        System.IO.Directory.CreateDirectory(store.Directory);
        foreach (var d in new[] { store.MediaDir, store.TranscriptDir, store.OutputDir, store.WorkDir })
            System.IO.Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(store.WorkDir, ".gitignore"), "*\n");
        File.WriteAllText(Path.Combine(store.Directory, "CLAUDE.md"), Templates.ClaudeMd());
        store.Save(project, "Nuovo progetto");
        return store;
    }

    /// <summary>Testo attuale del file di progetto (lettura che non blocca i salvataggi degli altri processi).</summary>
    public string ReadText() => ReadShared(ProjectFile);

    public Project Load()
    {
        var json = ReadShared(ProjectFile);
        return Json.Deserialize<Project>(json);
    }

    /// <summary>Salva il progetto e registra un'istantanea nella cronologia.</summary>
    public void Save(Project project, string label, string source = "cli")
    {
        var json = Json.Serialize(project);
        using var _ = AcquireLock();
        WriteAtomic(ProjectFile, json);

        System.IO.Directory.CreateDirectory(HistoryDir);
        var state = ReadHistoryState();
        var next = state.Current + 1;
        // Dopo un annulla, un nuovo salvataggio elimina i passi "ripeti".
        for (var i = next; i <= state.Max; i++) TryDelete(SnapshotPath(i));
        File.WriteAllText(SnapshotPath(next), json);
        var first = state.First == 0 ? next : state.First;
        while (next - first >= MaxHistory) TryDelete(SnapshotPath(first++));
        WriteHistoryState(new HistoryState(next, next, first));
        File.AppendAllText(Path.Combine(HistoryDir, "log.jsonl"),
            JsonSerializer.Serialize(new { n = next, time = DateTimeOffset.Now, source, label }) + "\n");
    }

    public bool CanUndo => ReadHistoryState() is var s && s.Current > s.First && s.First > 0;
    public bool CanRedo => ReadHistoryState() is var s && s.Current < s.Max;

    public Project? Undo() => Step(-1);
    public Project? Redo() => Step(+1);

    private Project? Step(int delta)
    {
        using var _ = AcquireLock();
        var state = ReadHistoryState();
        var target = state.Current + delta;
        if (state.First == 0 || target < state.First || target > state.Max || !File.Exists(SnapshotPath(target)))
            return null;
        var json = File.ReadAllText(SnapshotPath(target));
        WriteAtomic(ProjectFile, json);
        WriteHistoryState(state with { Current = target });
        return Json.Deserialize<Project>(json);
    }

    /// <summary>Ultime voci della cronologia (dalla più recente).</summary>
    public IReadOnlyList<string> HistoryLog(int count = 20)
    {
        var log = Path.Combine(HistoryDir, "log.jsonl");
        if (!File.Exists(log)) return [];
        var current = ReadHistoryState().Current;
        return File.ReadLines(log).TakeLast(count).Reverse().Select(l =>
        {
            using var doc = JsonDocument.Parse(l);
            var n = doc.RootElement.GetProperty("n").GetInt32();
            var mark = n == current ? "→" : n > current ? " (annullato)" : "";
            return $"{n,4} {mark} [{doc.RootElement.GetProperty("source").GetString()}] {doc.RootElement.GetProperty("label").GetString()}";
        }).ToList();
    }

    /// <summary>Risolve il percorso di un asset (relativo al progetto o assoluto).</summary>
    public string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Directory, path));

    /// <summary>Percorso da salvare nel progetto: relativo se il file è dentro la cartella del progetto.</summary>
    public string ToProjectPath(string fullPath)
    {
        var rel = Path.GetRelativePath(Directory, fullPath);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)
            ? fullPath
            : rel.Replace('\\', '/');
    }

    public string TranscriptPath(string assetId) => Path.Combine(TranscriptDir, assetId + ".json");
    public string ProxyPath(string assetId, AssetKind kind) =>
        Path.Combine(ProxyDir, assetId + (kind == AssetKind.Audio ? ".m4a" : kind == AssetKind.Image ? ".jpg" : ".mp4"));
    public string PeaksPath(string assetId) => Path.Combine(CacheDir, assetId + ".peaks.json");
    public string ThumbPath(string assetId) => Path.Combine(CacheDir, assetId + ".thumb.jpg");

    public Transcript? LoadTranscript(string assetId)
    {
        var p = TranscriptPath(assetId);
        return File.Exists(p) ? Json.Deserialize<Transcript>(ReadShared(p)) : null;
    }

    public void SaveTranscript(Transcript t)
    {
        System.IO.Directory.CreateDirectory(TranscriptDir);
        WriteAtomic(TranscriptPath(t.AssetId), Json.Serialize(t));
    }

    private sealed record HistoryState(int Current, int Max, int First);

    private string SnapshotPath(int n) => Path.Combine(HistoryDir, $"{n:D6}.json");
    private string StatePath => Path.Combine(HistoryDir, "state.json");

    private HistoryState ReadHistoryState()
    {
        if (!File.Exists(StatePath)) return new HistoryState(0, 0, 0);
        try { return JsonSerializer.Deserialize<HistoryState>(File.ReadAllText(StatePath)) ?? new(0, 0, 0); }
        catch (JsonException) { return new HistoryState(0, 0, 0); }
    }

    private void WriteHistoryState(HistoryState s) => WriteAtomic(StatePath, JsonSerializer.Serialize(s));

    private IDisposable AcquireLock()
    {
        System.IO.Directory.CreateDirectory(WorkDir);
        var path = Path.Combine(WorkDir, "lock");
        for (var i = 0; ; i++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (i < 100)
            {
                Thread.Sleep(50);
            }
        }
    }

    internal static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        for (var i = 0; ; i++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (IOException) when (i < 20) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) when (i < 20) { Thread.Sleep(50); }
        }
    }

    internal static string ReadShared(string path)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException) when (i < 20 && File.Exists(path)) { Thread.Sleep(50); }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
