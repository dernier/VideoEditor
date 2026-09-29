using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using VideoEditor.Core;
using VideoEditor.Core.Claude;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Ipc;
using VideoEditor.Core.Media;
using VideoEditor.Core.Model;
using VideoEditor.Core.Render;
using VideoEditor.Core.Transcription;

namespace VideoEditor.App;

/// <summary>
/// Collega la pagina web (interfaccia) al modello: applica le modifiche, salva con cronologia,
/// osserva il file del progetto (modifiche di Claude/CLI), prepara i media, esegue render,
/// trascrizioni e conversazioni con Claude Code, e risponde ai comandi <c>vedit app</c>.
/// </summary>
public sealed class EditorHost : IDisposable
{
    private readonly MainWindow _win;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _mediaLock = new(1, 1);

    private ProjectStore? _store;
    private Project? _project;
    private string? _lastJson;
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;

    private ClaudeCliSession? _claude;
    private CancellationTokenSource? _claudeCts;
    private CancellationTokenSource? _taskCts;

    // Stato dell'interfaccia riportato dalla pagina (per "vedit app status" e per il contesto di Claude).
    private double _time;
    private bool _playing;
    private List<string> _selection = [];
    private double? _in, _out;

    public EditorHost(MainWindow win)
    {
        _win = win;
        _ = AppPipe.ServeAsync(HandlePipeAsync, _life.Token);
    }

    // ================= Messaggi dalla pagina =================

    public async Task HandleAsync(JsonObject msg, List<string>? files)
    {
        var type = msg["type"]?.GetValue<string>() ?? "";
        try
        {
            switch (type)
            {
                case "ready":
                    SendAppSettings();
                    if (_project is not null) SendAll();
                    else Post("welcome", new JsonObject { ["recent"] = ToArray(_settings.RecentProjects.Where(Directory.Exists)) });
                    break;
                case "newProject": NewProject(); break;
                case "openProject": await OpenProjectAsync(Str(msg, "path") ?? PickProject()); break;
                case "import": await ImportAsync(PickMedia(), msg); break;
                case "importFiles": await ImportAsync(files ?? [], msg); break;
                case "op": ApplyOp(msg["op"]!.AsObject()); break;
                case "undo": Undo(true); break;
                case "redo": Undo(false); break;
                case "state":
                    _time = Num(msg, "time") ?? _time;
                    _playing = msg["playing"]?.GetValue<bool>() ?? _playing;
                    _selection = msg["selection"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? _selection;
                    _in = Num(msg, "in");
                    _out = Num(msg, "out");
                    break;
                case "render": await RenderAsync(msg); break;
                case "frame": await FrameAsync(Num(msg, "time") ?? _time); break;
                case "transcribe": await TranscribeAsync(msg["assets"]?.AsArray().Select(n => n!.GetValue<string>()).ToList(), msg["force"]?.GetValue<bool>() == true); break;
                case "silences": await SilencesAsync(msg); break;
                case "claude": await SendClaudeAsync(Str(msg, "text") ?? ""); break;
                case "claudeStop": _claude?.Cancel(); break;
                case "claudeReset":
                    _claude?.Cancel();
                    _claude = null;
                    Post("claude", new JsonObject { ["kind"] = "system", ["text"] = "Nuova conversazione." });
                    break;
                case "appSettings": UpdateAppSettings(msg["settings"]!.AsObject()); break;
                case "cancelTask": _taskCts?.Cancel(); break;
                case "openFolder":
                    if (_store is not null)
                        Process.Start(new ProcessStartInfo("explorer.exe", Str(msg, "sub") is { } sub ? Path.Combine(_store.Directory, sub) : _store.Directory) { UseShellExecute = true });
                    break;
                case "openTerminal": OpenTerminal(); break;
                case "exportSrt": ExportSrt(); break;
                case "devtools": _win.OpenDevTools(); break;
            }
        }
        catch (Exception ex) when (ex is EditException or InvalidOperationException or ArgumentException or IOException
                                       or FileNotFoundException or InvalidDataException or JsonException)
        {
            Toast(ex.Message, "error");
            if (type == "op") SendProject(); // ripristina la vista dopo un'operazione rifiutata
        }
    }

    // ================= Progetto =================

    private void NewProject()
    {
        var dlg = new OpenFolderDialog { Title = "Scegli (o crea) una cartella vuota per il nuovo progetto" };
        if (dlg.ShowDialog(_win) != true) return;
        var dir = dlg.FolderName;
        if (File.Exists(Path.Combine(dir, ProjectStore.FileName)))
        {
            _ = OpenProjectAsync(dir);
            return;
        }
        var store = ProjectStore.Create(dir, Project.CreateDefault(new DirectoryInfo(dir).Name));
        _ = OpenProjectAsync(store.Directory);
    }

    private string? PickProject()
    {
        var dlg = new OpenFileDialog { Title = "Apri progetto", Filter = "Progetto Video Editor|project.vedit.json" };
        return dlg.ShowDialog(_win) == true ? dlg.FileName : null;
    }

    public async Task OpenProjectAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var store = ProjectStore.Find(path) ?? throw new InvalidOperationException($"Nessun progetto in {path}.");
        _claude?.Cancel();
        _claude = null;
        _store = store;
        _project = store.Load();
        _lastJson = store.ReadText();
        _win.MapProjectFolder(store.Directory);
        _win.Title = $"{_project.Name} — Video Editor";
        _settings.AddRecent(store.Directory);
        Templates.UpdateProjectClaudeMd(store.Directory);
        StartWatching();
        SendAll();
        await EnsureMediaAsync(_project.Assets);
    }

    private void SendAll()
    {
        SendProject();
        SendWords();
        if (_store is null || _project is null) return;
        foreach (var a in _project.Assets)
        {
            if (File.Exists(_store.PeaksPath(a.Id))) _ = SendPeaksAsync(a);
        }
    }

    private void SendProject(bool external = false)
    {
        if (_store is null || _project is null) return;
        var media = new JsonObject();
        foreach (var a in _project.Assets) media[a.Id] = MediaInfo(a);
        Post("project", new JsonObject
        {
            ["project"] = JsonSerializer.SerializeToNode(_project, Json.Options),
            ["dir"] = _store.Directory,
            ["duration"] = _project.GetDuration(),
            ["canUndo"] = _store.CanUndo,
            ["canRedo"] = _store.CanRedo,
            ["media"] = media,
            ["external"] = external,
            ["fontAvailable"] = Renderer.ResolveFont(_project) is not null,
        });
    }

    private JsonObject MediaInfo(Asset a)
    {
        var proxy = _store!.ProxyPath(a.Id, a.Kind);
        var thumb = _store.ThumbPath(a.Id);
        return new JsonObject
        {
            ["proxy"] = File.Exists(proxy) ? Url(proxy) : null,
            ["thumb"] = File.Exists(thumb) ? Url(thumb) : null,
            ["transcribed"] = File.Exists(_store.TranscriptPath(a.Id)),
        };
    }

    private void SendWords()
    {
        if (_store is null || _project is null) return;
        var words = TranscriptTools.ToTimeline(_project, _store.LoadTranscript);
        var arr = new JsonArray();
        foreach (var w in words)
            arr.Add(new JsonObject { ["t"] = w.Text, ["s"] = w.Start, ["e"] = w.End, ["p"] = w.P, ["c"] = w.ClipId });
        Post("words", new JsonObject { ["words"] = arr });
    }

    private void Save(string label)
    {
        _store!.Save(_project!, label, "editor");
        _lastJson = _store.ReadText();
        SendProject();
        SendWords();
    }

    private void Undo(bool undo)
    {
        if (_store is null) return;
        var p = undo ? _store.Undo() : _store.Redo();
        if (p is null)
        {
            Toast(undo ? "Niente da annullare." : "Niente da ripetere.");
            return;
        }
        _project = p;
        _lastJson = _store.ReadText();
        SendProject();
        SendWords();
    }

    // ================= Operazioni di montaggio =================

    private void ApplyOp(JsonObject op)
    {
        if (_project is null) throw new InvalidOperationException("Nessun progetto aperto.");
        var ed = new TimelineEditor(_project);
        string label;
        switch (Str(op, "op"))
        {
            case "addClip":
            {
                var asset = _project.FindAsset(Str(op, "assetId")!) ?? throw new EditException("Asset non trovato.");
                var track = Str(op, "trackId") is { } tid ? ed.GetTrack(tid) : AssetImporter.DefaultTrackFor(_project, asset);
                var adopted = AssetImporter.AdoptFormatIfEmpty(_project, asset);
                var c = ed.AddClip(track.Id, asset.Id, Num(op, "start"), Num(op, "in") ?? 0, Num(op, "duration"));
                label = $"Aggiunta {asset.Name} su {track.Name}";
                if (adopted) Toast($"Formato del progetto: {_project.Settings.Width}x{_project.Settings.Height} @ {_project.Settings.Fps:0.##} fps");
                Post("select", new JsonObject { ["ids"] = new JsonArray(c.Id) });
                break;
            }
            case "moveClip":
                ed.MoveClip(Str(op, "clipId")!, Num(op, "start")!.Value, Str(op, "trackId"));
                label = "Spostata clip";
                break;
            case "moveClips":
                foreach (var m in op["moves"]!.AsArray())
                    ed.MoveClip(Str(m!.AsObject(), "clipId")!, Num(m.AsObject(), "start")!.Value, Str(m.AsObject(), "trackId"));
                label = "Spostate clip";
                break;
            case "trimClip":
                ed.TrimClip(Str(op, "clipId")!, Num(op, "start"), Num(op, "end"));
                label = "Rifilata clip";
                break;
            case "setClip":
                ed.SetClip(Str(op, "clipId")!, op["patch"]!.AsObject().DeepClone().AsObject());
                label = "Modificata clip";
                break;
            case "split":
            {
                var t = Num(op, "time") ?? _time;
                var ids = op["clipIds"]?.AsArray().Select(n => n!.GetValue<string>()).ToList();
                var created = ids is { Count: > 0 }
                    ? ids.Where(id => _project.FindClip(id) is { } f && t > f.Clip.Start && t < f.Clip.End).Select(id => ed.SplitClip(id, t)).ToList()
                    : ed.SplitAt(t);
                if (created.Count == 0) throw new EditException("Nessuna clip sotto la testina da dividere.");
                label = $"Divisione a {TranscriptTools.Time(t)}";
                break;
            }
            case "remove":
            {
                var ids = op["clipIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
                var ripple = op["ripple"]?.GetValue<bool>() == true;
                if (ripple && ids.Count > 0)
                {
                    // Eliminazione con ricompattamento: si toglie l'intervallo occupato dalle clip su tutte le tracce.
                    var ranges = ids.Select(id => ed.GetClip(id).Clip).Select(c => (c.Start, c.End)).ToList();
                    foreach (var id in ids) ed.RemoveClip(id);
                    ed.RippleDeleteRanges(ranges);
                }
                else
                {
                    foreach (var id in ids) ed.RemoveClip(id);
                }
                label = ripple ? "Eliminazione con ricompattamento" : "Eliminate clip";
                break;
            }
            case "deleteToPlayhead":
            {
                var left = op["left"]?.GetValue<bool>() == true;
                var t = Num(op, "time") ?? _time;
                var ids = op["clipIds"]?.AsArray().Select(n => n!.GetValue<string>()).ToList();
                var (from, to) = ed.DeleteToPlayhead(t, left, ids);
                label = left ? $"Eliminato a sinistra ({to - from:0.##} s)" : $"Eliminato a destra ({to - from:0.##} s)";
                // A sinistra il contenuto scorre indietro: la testina va dove ora riprende il video.
                if (left) Post("seek", new JsonObject { ["time"] = from });
                break;
            }
            case "rippleDelete":
                ed.RippleDelete(Num(op, "from")!.Value, Num(op, "to")!.Value, op["tracks"]?.AsArray().Select(n => n!.GetValue<string>()).ToList());
                label = $"Taglio {TranscriptTools.Time(Num(op, "from")!.Value)}–{TranscriptTools.Time(Num(op, "to")!.Value)}";
                break;
            case "cutRanges":
            {
                var ranges = op["ranges"]!.AsArray().Select(r => (r![0]!.GetValue<double>(), r[1]!.GetValue<double>())).ToList();
                ed.RippleDeleteRanges(ranges, op["tracks"]?.AsArray().Select(n => n!.GetValue<string>()).ToList());
                label = $"Tagliati {ranges.Count} intervalli";
                break;
            }
            case "insertGap":
                ed.InsertGap(Num(op, "time") ?? _time, Num(op, "length") ?? 1);
                label = "Inserito spazio";
                break;
            case "addText":
            {
                var c = ed.AddText(Str(op, "trackId"), Str(op, "text") ?? "Testo", Num(op, "start") ?? _time, Num(op, "duration") ?? 3);
                label = "Aggiunto testo";
                Post("select", new JsonObject { ["ids"] = new JsonArray(c.Id) });
                break;
            }
            case "addMask":
            {
                var spec = op["mask"] is JsonObject mo ? Json.Deserialize<MaskSpec>(mo.ToJsonString()) : null;
                var c = ed.AddMask(Str(op, "trackId"), Num(op, "start") ?? _time, Num(op, "duration") ?? 3, spec);
                label = "Aggiunta maschera";
                Post("select", new JsonObject { ["ids"] = new JsonArray(c.Id) });
                break;
            }
            case "maskRect":
                ed.SetMaskRect(Str(op, "clipId")!, Num(op, "time") ?? _time, Num(op, "x")!.Value, Num(op, "y")!.Value,
                    Num(op, "w")!.Value, Num(op, "h")!.Value, op["keyframe"]?.GetValue<bool>() == true);
                label = "Zona della maschera";
                break;
            case "maskUnkey":
                ed.RemoveMaskKey(Str(op, "clipId")!, Num(op, "time") ?? _time);
                label = "Rimosso keyframe";
                break;
            case "createSubtitles":
            {
                var words = TranscriptTools.ToWords(TranscriptTools.ToTimeline(_project, _store!.LoadTranscript));
                if (words.Count == 0) throw new EditException("Nessuna trascrizione sulla timeline: premi prima «Trascrivi».");
                var options = new SubtitleOptions
                {
                    MaxChars = (int)(Num(op, "maxChars") ?? 42),
                    MaxLines = (int)(Num(op, "lines") ?? 2),
                    SkipFillers = op["skipFillers"]?.GetValue<bool>() ?? true,
                };
                var track = ed.SetSubtitles(SubtitleBuilder.Build(words, options));
                label = $"Creati {track.Clips.Count} sottotitoli";
                Toast($"{track.Clips.Count} sottotitoli sulla traccia «{track.Name}». Selezionane uno per cambiare stile e usa «Applica a tutti».", "ok");
                break;
            }
            case "applyStyleToTrack":
            {
                var n = ed.ApplyStyleToTrack(Str(op, "clipId")!);
                label = $"Stile applicato a {n} testi";
                break;
            }
            case "addTrack":
                ed.AddTrack(Enum.Parse<TrackKind>(Str(op, "kind")!, true), Str(op, "name"));
                label = "Aggiunta traccia";
                break;
            case "removeTrack":
                ed.RemoveTrack(Str(op, "trackId")!);
                label = "Rimossa traccia";
                break;
            case "moveTrack":
                ed.MoveTrack(Str(op, "trackId")!, (int)Num(op, "index")!.Value);
                label = "Spostata traccia";
                break;
            case "setTrack":
                ed.SetTrack(Str(op, "trackId")!, op["patch"]!.AsObject().DeepClone().AsObject());
                label = "Modificata traccia";
                break;
            case "addMarker":
                ed.AddMarker(Num(op, "time") ?? _time, Str(op, "label") ?? "");
                label = "Aggiunto marcatore";
                break;
            case "removeMarker":
                ed.RemoveMarker(Str(op, "id")!);
                label = "Rimosso marcatore";
                break;
            case "removeAsset":
                ed.RemoveAsset(Str(op, "assetId")!);
                label = "Rimosso asset";
                break;
            case "settings":
            {
                var patch = op["patch"]!.AsObject().DeepClone().AsObject();
                if (patch.Remove("name", out var name) && name is not null) _project.Name = name.GetValue<string>();
                _project.Settings = Json.Merge(_project.Settings, patch);
                label = "Impostazioni del progetto";
                break;
            }
            default:
                throw new EditException($"Operazione sconosciuta: {Str(op, "op")}.");
        }
        Save(label);
    }

    // ================= Media =================

    private static readonly string MediaFilter =
        "File multimediali|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.mts;*.mxf;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|Tutti i file|*.*";

    private List<string> PickMedia()
    {
        var dlg = new OpenFileDialog { Title = "Importa media", Multiselect = true, Filter = MediaFilter };
        return dlg.ShowDialog(_win) == true ? dlg.FileNames.ToList() : [];
    }

    private async Task ImportAsync(List<string> files, JsonObject msg)
    {
        if (_store is null || _project is null) throw new InvalidOperationException("Apri o crea prima un progetto.");
        if (files.Count == 0) return;
        var add = msg["add"]?.GetValue<bool>() == true;
        var trackId = Str(msg, "trackId");
        var at = Num(msg, "at");
        var copy = msg["copy"]?.GetValue<bool>() == true;
        var store = _store;
        var project = _project;
        var imported = new List<Asset>();
        var errors = new List<string>();
        foreach (var f in files)
        {
            try { imported.Add(await Task.Run(() => AssetImporter.Import(store, project, f, copy))); }
            catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or InvalidOperationException or IOException)
            {
                errors.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }
        if (add)
        {
            var ed = new TimelineEditor(project);
            foreach (var a in imported)
            {
                AssetImporter.AdoptFormatIfEmpty(project, a);
                var track = trackId is not null && project.FindTrack(trackId) is { } t && Compatible(t, a) ? t : AssetImporter.DefaultTrackFor(project, a);
                var c = ed.AddClip(track.Id, a.Id, at);
                if (at is not null) at = c.End;
            }
        }
        if (imported.Count > 0) Save($"Importati {imported.Count} file");
        if (errors.Count > 0) Toast("Impossibile importare:\n" + string.Join("\n", errors), "error");
        await EnsureMediaAsync(imported);
    }

    private static bool Compatible(Track t, Asset a) => t.Kind switch
    {
        TrackKind.Video => a.Kind != AssetKind.Audio,
        TrackKind.Audio => a.HasAudio,
        _ => false,
    };

    /// <summary>Prepara in background proxy, miniature e forme d'onda che mancano.</summary>
    private async Task EnsureMediaAsync(IEnumerable<Asset> assets)
    {
        var store = _store;
        if (store is null) return;
        foreach (var a in assets.ToList())
        {
            var needProxy = !File.Exists(store.ProxyPath(a.Id, a.Kind));
            var needThumb = a.HasVideo && !File.Exists(store.ThumbPath(a.Id));
            var needPeaks = a.HasAudio && !File.Exists(store.PeaksPath(a.Id));
            if (!needProxy && !needThumb && !needPeaks) continue;
            await _mediaLock.WaitAsync();
            try
            {
                if (_store != store) return; // progetto cambiato nel frattempo
                var taskId = "media-" + a.Id;
                ReportTask(taskId, $"Preparo {a.Name}", 0);
                var progress = new Progress<double>(p => ReportTask(taskId, $"Proxy di {a.Name}", p));
                await Task.Run(async () =>
                {
                    if (needThumb) await MediaCache.EnsureThumbnailAsync(store, a, _life.Token);
                    if (needProxy) await MediaCache.EnsureProxyAsync(store, a, _settings.Encoder, progress, _life.Token);
                    if (needPeaks) await MediaCache.GetPeaksAsync(store, a, _life.Token);
                });
                ReportTask(taskId, "", 1, done: true);
                Post("media", new JsonObject { ["assetId"] = a.Id, ["info"] = MediaInfo(a) });
                if (a.HasAudio) await SendPeaksAsync(a);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                ReportTask("media-" + a.Id, "", 1, done: true);
                Toast($"{a.Name}: {ex.Message}", "error");
            }
            finally
            {
                _mediaLock.Release();
            }
        }
    }

    private async Task SendPeaksAsync(Asset a)
    {
        if (_store is null) return;
        var peaks = await MediaCache.GetPeaksAsync(_store, a);
        if (peaks is null) return;
        Post("waveform", new JsonObject { ["assetId"] = a.Id, ["rate"] = peaks.Rate, ["data"] = Convert.ToBase64String(peaks.Values) });
    }

    // ================= Render, fotogrammi, trascrizione =================

    private async Task RenderAsync(JsonObject msg)
    {
        if (_store is null || _project is null) return;
        if (_taskCts is not null) throw new InvalidOperationException("C'è già un'elaborazione in corso.");
        var preview = msg["preview"]?.GetValue<bool>() == true;
        string? output = null;
        if (!preview)
        {
            Directory.CreateDirectory(_store.OutputDir);
            var dlg = new SaveFileDialog
            {
                Title = "Esporta video",
                Filter = "Video MP4|*.mp4",
                InitialDirectory = _store.OutputDir,
                FileName = _project.Name + ".mp4",
            };
            if (dlg.ShowDialog(_win) != true) return;
            output = dlg.FileName;
        }
        var snapshot = Json.Deserialize<Project>(Json.Serialize(_project));
        var store = _store;
        var options = new RenderOptions
        {
            Preview = preview,
            Output = output,
            Encoder = Str(msg, "encoder") ?? _settings.Encoder,
            Quality = msg["quality"] is JsonValue q ? q.GetValue<int>() : null,
            From = Num(msg, "from") ?? 0,
            To = Num(msg, "to"),
        };
        _taskCts = new CancellationTokenSource();
        var label = preview ? "Anteprima" : "Esportazione";
        try
        {
            ReportTask("render", label, 0, cancellable: true);
            var progress = new Progress<double>(p => ReportTask("render", label, p, cancellable: true));
            var result = await Task.Run(() => Renderer.RenderAsync(store, snapshot, options, progress, _taskCts.Token));
            ReportTask("render", label, 1, done: true);
            if (preview)
                Post("previewReady", new JsonObject { ["url"] = Url(result.Output), ["duration"] = result.Duration, ["from"] = options.From });
            else
                Toast($"Esportato {Path.GetFileName(result.Output)} ({result.Encoder}, {result.Elapsed.TotalSeconds:0} s)", "ok");
        }
        catch (OperationCanceledException)
        {
            ReportTask("render", label, 1, done: true);
            Toast($"{label} annullata.");
        }
        catch
        {
            ReportTask("render", label, 1, done: true);
            throw;
        }
        finally
        {
            _taskCts = null;
        }
    }

    private async Task FrameAsync(double time)
    {
        if (_store is null || _project is null) return;
        var store = _store;
        var snapshot = Json.Deserialize<Project>(Json.Serialize(_project));
        var output = Path.Combine(store.CacheDir, "frame.png");
        Directory.CreateDirectory(store.CacheDir);
        await Task.Run(() => Renderer.RenderFrameAsync(store, snapshot, time, output, Math.Min(1080, snapshot.Settings.Height)));
        Post("frameReady", new JsonObject { ["url"] = Url(output), ["time"] = time });
    }

    private async Task TranscribeAsync(List<string>? assetIds, bool force)
    {
        if (_store is null || _project is null) return;
        if (_taskCts is not null) throw new InvalidOperationException("C'è già un'elaborazione in corso.");
        var store = _store;
        var assets = assetIds is { Count: > 0 }
            ? assetIds.Select(id => _project.FindAsset(id)).OfType<Asset>().ToList()
            : _project.Assets.Where(a => a.HasAudio && (a.Kind == AssetKind.Video ||
                _project.Tracks.Where(t => !t.Duck).SelectMany(t => t.Clips).Any(c => c.AssetId == a.Id))).ToList();
        assets = assets.Where(a => a.HasAudio && (force || !File.Exists(store.TranscriptPath(a.Id)))).ToList();
        if (assets.Count == 0)
        {
            Toast("Niente da trascrivere (già fatto?).");
            return;
        }
        _taskCts = new CancellationTokenSource();
        var language = _project.Settings.Language;
        try
        {
            foreach (var a in assets)
            {
                var label = $"Trascrizione {a.Name}";
                ReportTask("transcribe", label, 0, cancellable: true);
                var t = await Task.Run(() => WhisperTranscriber.TranscribeAsync(store, a, _settings.WhisperModel, language,
                    log: m => ReportTask("transcribe", $"{a.Name}: {m}", null, cancellable: true),
                    progress: new Progress<double>(p => ReportTask("transcribe", label, p, cancellable: true)),
                    ct: _taskCts.Token));
                store.SaveTranscript(t);
                Post("media", new JsonObject { ["assetId"] = a.Id, ["info"] = MediaInfo(a) });
                SendWords();
            }
            Toast("Trascrizione completata.", "ok");
        }
        catch (OperationCanceledException)
        {
            Toast("Trascrizione annullata.");
        }
        finally
        {
            ReportTask("transcribe", "", 1, done: true);
            _taskCts = null;
        }
    }

    private async Task SilencesAsync(JsonObject msg)
    {
        if (_store is null || _project is null) return;
        var store = _store;
        var project = _project;
        ReportTask("silences", "Cerco le pause", null);
        try
        {
            var (track, ranges) = await Task.Run(() => SilenceFinder.FindAsync(store, project, Str(msg, "trackId"),
                Num(msg, "db") ?? -35, Num(msg, "min") ?? 0.6, Num(msg, "pad") ?? 0.15));
            var arr = new JsonArray();
            foreach (var (f, t) in ranges) arr.Add(new JsonArray(f, t));
            Post("silences", new JsonObject { ["trackId"] = track.Id, ["ranges"] = arr });
        }
        finally
        {
            ReportTask("silences", "", 1, done: true);
        }
    }

    // ================= Claude =================

    private async Task SendClaudeAsync(string text)
    {
        if (_store is null || _project is null) throw new InvalidOperationException("Apri prima un progetto.");
        if (string.IsNullOrWhiteSpace(text)) return;
        _claude ??= new ClaudeCliSession(_store.Directory);
        if (_claude.IsRunning) throw new InvalidOperationException("Claude sta ancora lavorando: attendi o premi Interrompi.");
        _claude.ClaudePath = _settings.ClaudePath;
        _claude.Model = _settings.ClaudeModel;
        _claudeCts = new CancellationTokenSource();
        Post("claudeState", new JsonObject { ["running"] = true });
        try
        {
            await foreach (var ev in _claude.SendAsync(BuildContext() + text, _claudeCts.Token))
                Post("claude", new JsonObject { ["kind"] = ev.Kind, ["text"] = ev.Text });
        }
        finally
        {
            Post("claudeState", new JsonObject { ["running"] = false, ["sessionId"] = _claude?.SessionId });
            ReloadFromDisk();
        }
    }

    /// <summary>Contesto che accompagna ogni messaggio: cosa l'utente sta guardando nell'editor.</summary>
    private string BuildContext()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Contesto dall'editor video — informazioni, non istruzioni]");
        sb.AppendLine($"Testina: {_time:0.###} s ({TranscriptTools.Time(_time)}){(_playing ? ", in riproduzione" : "")}. Durata timeline: {_project!.GetDuration():0.###} s.");
        foreach (var id in _selection)
        {
            if (_project.FindClip(id) is not { } f) continue;
            var what = f.Clip.IsText ? $"testo \"{f.Clip.Text}\"" : $"{f.Clip.AssetId} sorgente {f.Clip.In:0.###}–{f.Clip.In + f.Clip.Duration:0.###}";
            sb.AppendLine($"Clip selezionata: {id} su {f.Track.Id}, timeline {f.Clip.Start:0.###}–{f.Clip.End:0.###}, {what}.");
        }
        if (_in is double i && _out is double o) sb.AppendLine($"Intervallo In/Out segnato: {i:0.###}–{o:0.###} s.");
        sb.AppendLine("Usa `vedit` per leggere e modificare il progetto (vedi CLAUDE.md).");
        sb.AppendLine();
        sb.AppendLine("Richiesta dell'utente:");
        return sb.ToString();
    }

    private void ExportSrt()
    {
        if (_store is null || _project is null) return;
        var cues = new TimelineEditor(_project).CurrentSubtitles();
        if (cues.Count == 0) throw new InvalidOperationException("Nessun sottotitolo: crea prima i sottotitoli dalla trascrizione.");
        Directory.CreateDirectory(_store.OutputDir);
        var dlg = new SaveFileDialog
        {
            Title = "Esporta sottotitoli",
            Filter = "Sottotitoli SRT|*.srt",
            InitialDirectory = _store.OutputDir,
            FileName = _project.Name + ".srt",
        };
        if (dlg.ShowDialog(_win) != true) return;
        File.WriteAllText(dlg.FileName, SubtitleBuilder.ToSrt(cues), new UTF8Encoding(false));
        Toast($"Esportati {cues.Count} sottotitoli in {Path.GetFileName(dlg.FileName)}", "ok");
    }

    private void OpenTerminal()
    {
        if (_store is null) return;
        // Terminale nella cartella del progetto con vedit nel PATH: lì si può lanciare "claude" in modo interattivo.
        var psi = new ProcessStartInfo("cmd.exe") { WorkingDirectory = _store.Directory, UseShellExecute = false };
        psi.Environment["PATH"] = AppContext.BaseDirectory + ";" + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["VEDIT_PROJECT"] = _store.Directory;
        psi.ArgumentList.Add("/k");
        psi.ArgumentList.Add("echo Progetto: " + _store.Directory + " ^& echo Scrivi 'claude' per parlare con Claude, 'vedit help' per i comandi.");
        Process.Start(psi);
    }

    // ================= Modifiche esterne (Claude, CLI) =================

    private void StartWatching()
    {
        _watcher?.Dispose();
        _reloadTimer?.Dispose();
        _reloadTimer = new Timer(_ => _win.Dispatcher.InvokeAsync(() => ReloadFromDisk()), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(_store!.Directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler onChange = (_, e) =>
        {
            var name = e.FullPath;
            if (name.EndsWith(ProjectStore.FileName, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(_store!.TranscriptDir, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                _reloadTimer?.Change(200, Timeout.Infinite);
        };
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Renamed += (s, e) => onChange(s, e);
    }

    private void ReloadFromDisk(bool force = false)
    {
        if (_store is null || !_store.Exists) return;
        string json;
        try { json = _store.ReadText(); }
        catch (IOException) { _reloadTimer?.Change(200, Timeout.Infinite); return; }
        var changed = force || json != _lastJson;
        if (changed)
        {
            try
            {
                var p = Json.Deserialize<Project>(json);
                var newAssets = p.Assets.Where(a => _project?.FindAsset(a.Id) is null).ToList();
                _project = p;
                _lastJson = json;
                SendProject(external: true);
                if (newAssets.Count > 0) _ = EnsureMediaAsync(newAssets);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                Toast("project.vedit.json non valido: " + ex.Message, "error");
                return;
            }
        }
        SendWords();
        if (_project is not null)
            foreach (var a in _project.Assets) Post("media", new JsonObject { ["assetId"] = a.Id, ["info"] = MediaInfo(a) });
    }

    // ================= Canale "vedit app" =================

    private Task<JsonNode?> HandlePipeAsync(string cmd, JsonObject args) =>
        _win.Dispatcher.InvokeAsync<JsonNode?>(() =>
        {
            switch (cmd)
            {
                case "status":
                    return new JsonObject
                    {
                        ["project"] = _store?.Directory,
                        ["name"] = _project?.Name,
                        ["time"] = Math.Round(_time, 3),
                        ["timeText"] = TranscriptTools.Time(_time),
                        ["playing"] = _playing,
                        ["selection"] = ToArray(_selection),
                        ["in"] = _in,
                        ["out"] = _out,
                        ["duration"] = _project?.GetDuration(),
                        ["claudeRunning"] = _claude?.IsRunning ?? false,
                    };
                case "seek":
                    Post("seek", new JsonObject { ["time"] = args["time"]?.GetValue<double>() ?? 0 });
                    return new JsonObject { ["ok"] = true };
                case "play" or "pause":
                    Post(cmd, new JsonObject());
                    return new JsonObject { ["ok"] = true };
                case "select":
                    Post("select", new JsonObject { ["ids"] = new JsonArray(args["clipId"]?.GetValue<string>()) });
                    return new JsonObject { ["ok"] = true };
                case "reload":
                    ReloadFromDisk(force: true);
                    return new JsonObject { ["ok"] = true };
                case "say":
                    var text = args["text"]?.GetValue<string>() ?? "";
                    Toast(text, "claude");
                    Post("claude", new JsonObject { ["kind"] = "text", ["text"] = text });
                    return new JsonObject { ["ok"] = true };
                default:
                    throw new InvalidOperationException($"Comando sconosciuto: {cmd}");
            }
        }).Task;

    // ================= Impostazioni =================

    private void SendAppSettings() => Post("appSettings", new JsonObject
    {
        ["settings"] = JsonSerializer.SerializeToNode(_settings, Json.Options),
        ["encoders"] = ToArray(Encoders.Known),
        ["whisperModels"] = ToArray(WhisperTranscriber.Models.Keys),
        ["claudeFound"] = (_settings.ClaudePath is { Length: > 0 } cp && File.Exists(cp)) || ClaudeCliSession.Locate() is not null,
        ["ffmpegFound"] = FfmpegAvailable(),
    });

    private static bool FfmpegAvailable()
    {
        try { _ = Ffmpeg.FfmpegPath; return true; }
        catch (FileNotFoundException) { return false; }
    }

    private void UpdateAppSettings(JsonObject patch)
    {
        var updated = Json.Merge(_settings, patch);
        _settings.ClaudePath = string.IsNullOrWhiteSpace(updated.ClaudePath) ? null : updated.ClaudePath;
        _settings.ClaudeModel = string.IsNullOrWhiteSpace(updated.ClaudeModel) ? null : updated.ClaudeModel;
        _settings.Encoder = updated.Encoder;
        _settings.WhisperModel = updated.WhisperModel;
        _settings.Save();
        SendAppSettings();
        Toast("Impostazioni salvate.", "ok");
    }

    public bool ConfirmClose()
    {
        if (_taskCts is null && _claude?.IsRunning != true) return true;
        var r = MessageBox.Show(_win, "C'è un'elaborazione in corso. Chiudere comunque?", "Video Editor",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        return r == MessageBoxResult.Yes;
    }

    public void Dispose()
    {
        _life.Cancel();
        _taskCts?.Cancel();
        _claude?.Cancel();
        _watcher?.Dispose();
        _reloadTimer?.Dispose();
    }

    // ================= Supporto =================

    private void Post(string type, JsonObject data)
    {
        data["type"] = type;
        _win.Post(data);
    }

    private void Toast(string text, string level = "info") =>
        Post("toast", new JsonObject { ["text"] = text, ["level"] = level });

    private void ReportTask(string id, string label, double? progress, bool done = false, bool cancellable = false) =>
        Post("task", new JsonObject { ["id"] = id, ["label"] = label, ["progress"] = progress, ["done"] = done, ["cancellable"] = cancellable });

    /// <summary>URL del file nell'host virtuale del progetto (con parametro anti-cache).</summary>
    private string Url(string fullPath)
    {
        var rel = Path.GetRelativePath(_store!.Directory, fullPath).Replace('\\', '/');
        var encoded = string.Join('/', rel.Split('/').Select(Uri.EscapeDataString));
        var v = File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath).Ticks : 0;
        return $"https://{MainWindow.ProjectHost}/{encoded}?v={v}";
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i);
        return arr;
    }
}
