using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VideoEditor.Cli;
using VideoEditor.Core;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Ipc;
using VideoEditor.Core.Media;
using VideoEditor.Core.Model;
using VideoEditor.Core.Render;
using VideoEditor.Core.Transcription;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = new UTF8Encoding(false);

try
{
    return await Commands.RunAsync(args);
}
catch (Exception ex) when (ex is CliException or EditException or FileNotFoundException or InvalidOperationException
                               or InvalidDataException or ArgumentException)
{
    Console.Error.WriteLine("ERRORE: " + ex.Message);
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Interrotto.");
    return 130;
}

namespace VideoEditor.Cli
{
    internal static class Commands
    {
        private const string Usage = """
            vedit — editor video da riga di comando (il progetto è project.vedit.json)

            Uso: vedit <comando> [argomenti] [--project <cartella>] [--json]
            I tempi sono in secondi (12.5) oppure mm:ss.cc / hh:mm:ss.cc, sempre sulla timeline salvo dove indicato.

            PROGETTO
              init [cartella] [--name N] [--size 1920x1080] [--fps 30]   crea un progetto
              info [--json]                        riepilogo: asset, tracce, clip, durata, avvisi
              settings chiave=valore ...           width, height, fps, backgroundColor, language, fontFile, name
              validate                             controlla sovrapposizioni ed errori
              undo | redo | history [N]            annulla/ripeti (condiviso con l'editor)

            MEDIA
              import <file...> [--copy] [--add] [--track ID] [--at T]   importa (e con --add mette in timeline)
              probe <file>                         caratteristiche di un file
              proxy [assetId...]                   prepara proxy, miniature e forme d'onda per l'editor

            MONTAGGIO
              add <assetId> [--track ID] [--at T] [--in T] [--dur D]    aggiunge una clip (senza --at la accoda)
              text "<testo>" --at T --dur D [--track ID] [--x 0.5] [--y 0.85] [--size 64] [--color #FFFFFF] [--box #000000AA]
              move <clipId> <inizio> [--track ID]
              trim <clipId> [--start T] [--end T]   sposta i bordi (tempi di timeline)
              set <clipId> chiave=valore ...       volume, fadeIn, fadeOut, x, y, scale, opacity, text, style.fontSize, note...
              split <T> [--clip ID | --tracks V1,A1]
              remove <clipId...> [--ripple]
              cut <da> <a> [--tracks V1,T1]         elimina l'intervallo e ricompatta (ripple delete)
              cut --ranges "1.2-3.4,10-11.5" [--tracks ...]   più intervalli in un colpo (vanno bene in qualsiasi ordine)
              gap <T> <durata>                     inserisce uno spazio vuoto
              track add <video|audio|text|mask> [--name N] [--index I] | track remove ID | track move ID I | track set ID chiave=valore
              marker add <T> "<etichetta>" | marker remove ID

            OSCURAMENTO (volti, nomi, email… — coordinate 0-1: x,y = centro, w,h = dimensioni)
              mask add --at T --dur D [--rect 0.5,0.4,0.2,0.3] [--effect blur|pixelate|solid] [--shape ellipse|rect]
                       [--strength 30] [--color #000000] [--track M1]
              mask key <clipId> <T> x,y,w,h     posizione della zona all'istante T (la maschera si muove tra i keyframe)
              mask unkey <clipId> <T>           toglie il keyframe vicino a T
              mask clear <clipId>               toglie tutti i keyframe (resta fissa dove si trova all'inizio)

            TRASCRIZIONE E ANALISI
              transcribe [assetId...] [--model large-v3-turbo] [--lang it] [--prompt "nomi propri"] [--force]
              transcript [--words] [--srt] [--json]        trascrizione riportata sulla timeline corrente
              transcript <assetId> --source [--words]      trascrizione nel tempo del file sorgente
              find "<frase>"                               dove viene detta una frase (tempi di timeline)
              fillers                                      intercalari (ehm, uhm...) con gli intervalli da tagliare
              silences [--track V1] [--db -35] [--min 0.6] [--pad 0.15]   pause sulla timeline

            SOTTOTITOLI
              subtitles create [--max-chars 42] [--lines 2] [--max-dur 6] [--size N] [--y 0.88] [--keep-fillers]
                                           traccia "Sottotitoli" dalla trascrizione (la ricrea se esiste già)
              subtitles export <file.srt>  esporta in SRT i sottotitoli della traccia (con le correzioni fatte a mano)
              subtitles style <clipId>     applica stile e posizione di quel sottotitolo a tutti gli altri

            RENDERING (ffmpeg, NVENC se disponibile)
              render [--preview] [--out file.mp4] [--encoder auto|h264_nvenc|hevc_nvenc|av1_nvenc|libx264|libx265]
                     [--quality CQ] [--from T] [--to T] [--height H]
              frame <T> [--out frame.png] [--height H] [--grid]   fotogramma composto; --grid aggiunge una griglia al 10%

            EDITOR APERTO
              app status                   file aperto, posizione della testina, clip selezionata, stato
              app seek <T> | app play | app pause | app select <clipId> | app reload | app say "<messaggio>"
            """;

        public static async Task<int> RunAsync(string[] argv)
        {
            if (argv.Length == 0 || argv[0] is "help" or "--help" or "-h")
            {
                Console.WriteLine(Usage);
                return 0;
            }
            var command = argv[0].ToLowerInvariant();
            var a = Args.Parse(argv.Skip(1));
            if (a.Flag("help"))
            {
                Console.WriteLine(Usage);
                return 0;
            }
            var label = "vedit " + string.Join(' ', argv.Select(x => x.Contains(' ') ? $"\"{x}\"" : x));

            switch (command)
            {
                case "init": return Init(a);
                case "probe": return Probe(a);
                case "app": return await App(a);
            }

            var store = OpenStore(a);
            var project = store.Load();
            var editor = new TimelineEditor(project);

            switch (command)
            {
                case "info": return Info(store, project, a);
                case "validate": return Validate(editor);
                case "undo":
                case "redo":
                {
                    var p = command == "undo" ? store.Undo() : store.Redo();
                    Console.WriteLine(p is null ? $"Niente da {(command == "undo" ? "annullare" : "ripetere")}." : $"OK: {command} eseguito.");
                    return p is null ? 1 : 0;
                }
                case "history":
                    foreach (var line in store.HistoryLog(a.Positional.Count > 0 ? (int)a.PosTime(0, "numero") : 20)) Console.WriteLine(line);
                    return 0;
                case "settings": return Save(store, project, label, () => Settings(project, a));
                case "import": return await Import(store, project, a, label);
                case "proxy": return await Proxy(store, project, a);
                case "add":
                    return Save(store, project, label, () =>
                    {
                        var asset = project.FindAsset(a.Pos(0, "assetId")) ?? throw new CliException($"Asset {a.Positional[0]} non trovato.");
                        var track = a.Opt("track") is { } tid ? editor.GetTrack(tid) : AssetImporter.DefaultTrackFor(project, asset);
                        var clip = editor.AddClip(track.Id, asset.Id, a.Num("at"), a.Num("in") ?? 0, a.Num("dur"));
                        return $"clip {clip.Id} su {track.Id} da {F(clip.Start)} a {F(clip.End)}";
                    }, editor);
                case "text":
                    return Save(store, project, label, () =>
                    {
                        var style = new TextStyle();
                        if (a.Num("size") is double size) style.FontSize = size;
                        if (a.Opt("color") is { } color) style.Color = color;
                        if (a.Opt("box") is { } box) style.BoxColor = box;
                        var clip = editor.AddText(a.Opt("track"), a.Pos(0, "testo").Replace("\\n", "\n"),
                            a.Num("at") ?? throw new CliException("Manca --at."), a.Num("dur") ?? 3, style);
                        if (a.Num("x") is double x) clip.X = x;
                        if (a.Num("y") is double y) clip.Y = y;
                        if (a.Num("fade") is double fade) clip.FadeIn = clip.FadeOut = fade;
                        return $"testo {clip.Id} da {F(clip.Start)} a {F(clip.End)}";
                    }, editor);
                case "move":
                    return Save(store, project, label, () =>
                    {
                        var c = editor.MoveClip(a.Pos(0, "clipId"), a.PosTime(1, "inizio"), a.Opt("track"));
                        return $"clip {c.Id} ora da {F(c.Start)} a {F(c.End)}";
                    }, editor);
                case "trim":
                    return Save(store, project, label, () =>
                    {
                        var c = editor.TrimClip(a.Pos(0, "clipId"), a.Num("start"), a.Num("end"));
                        return $"clip {c.Id} ora da {F(c.Start)} a {F(c.End)} (ingresso {F(c.In)})";
                    }, editor);
                case "set":
                    return Save(store, project, label, () =>
                    {
                        var c = editor.SetClip(a.Pos(0, "clipId"), Args.ToPatch(a.Positional.Skip(1)));
                        return $"clip {c.Id} aggiornata";
                    }, editor);
                case "split":
                    return Save(store, project, label, () =>
                    {
                        var t = a.PosTime(0, "tempo");
                        if (a.Opt("clip") is { } clipId) return $"nuova clip {editor.SplitClip(clipId, t).Id}";
                        var created = editor.SplitAt(t, SplitList(a.Opt("tracks")));
                        if (created.Count == 0) throw new CliException($"Nessuna clip attraversa {F(t)}.");
                        return "nuove clip " + string.Join(", ", created.Select(c => c.Id));
                    }, editor);
                case "remove":
                    return Save(store, project, label, () =>
                    {
                        if (a.Positional.Count == 0) throw new CliException("Indica almeno una clip.");
                        foreach (var id in a.Positional) editor.RemoveClip(id, a.Flag("ripple"));
                        return $"rimosse {a.Positional.Count} clip";
                    }, editor);
                case "cut":
                    return Save(store, project, label, () =>
                    {
                        var ranges = a.Opt("ranges") is { } r ? ParseRanges(r) : [(a.PosTime(0, "da"), a.PosTime(1, "a"))];
                        var before = project.GetDuration();
                        editor.RippleDeleteRanges(ranges, SplitList(a.Opt("tracks")));
                        return $"tagliati {ranges.Count} intervalli, durata {Time(before)} → {Time(project.GetDuration())}";
                    }, editor);
                case "gap":
                    return Save(store, project, label, () =>
                    {
                        editor.InsertGap(a.PosTime(0, "tempo"), a.PosTime(1, "durata"));
                        return "spazio inserito";
                    }, editor);
                case "track": return Save(store, project, label, () => Track(editor, a), editor);
                case "mask": return Save(store, project, label, () => Mask(editor, a), editor);
                case "subtitles" or "subs" or "sottotitoli":
                    if (a.Positional.FirstOrDefault() == "export")
                    {
                        var cues = editor.CurrentSubtitles();
                        if (cues.Count == 0) throw new CliException("Nessun sottotitolo: usa prima 'vedit subtitles create'.");
                        var file = Path.GetFullPath(a.Pos(1, "file.srt"));
                        File.WriteAllText(file, SubtitleBuilder.ToSrt(cues), new UTF8Encoding(false));
                        Console.WriteLine($"OK: {cues.Count} sottotitoli in {file}");
                        return 0;
                    }
                    return Save(store, project, label, () => Subtitles(store, project, editor, a), editor);
                case "marker":
                    return Save(store, project, label, () =>
                    {
                        switch (a.Pos(0, "add|remove"))
                        {
                            case "add":
                                var m = editor.AddMarker(a.PosTime(1, "tempo"), a.Positional.Count > 2 ? a.Positional[2] : "");
                                return $"marcatore {m.Id} a {F(m.Time)}";
                            case "remove":
                                editor.RemoveMarker(a.Pos(1, "id"));
                                return "marcatore rimosso";
                            default: throw new CliException("Uso: marker add <T> \"etichetta\" | marker remove <id>");
                        }
                    }, editor);
                case "transcribe": return await Transcribe(store, project, a);
                case "transcript": return Transcript(store, project, a);
                case "find": return Find(store, project, a);
                case "fillers": return Fillers(store, project, a);
                case "silences": return await Silences(store, project, a);
                case "render": return await Render(store, project, a);
                case "frame":
                {
                    var t = a.PosTime(0, "tempo");
                    var output = a.Opt("out") ?? Path.Combine(store.OutputDir, $"frame-{F(t)}.png");
                    output = Path.GetFullPath(output);
                    await Renderer.RenderFrameAsync(store, project, t, output, a.Int("height"), grid: a.Flag("grid"));
                    Console.WriteLine(output);
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"Comando sconosciuto: {command}. Usa 'vedit help'.");
                    return 2;
            }
        }

        // ---------- Progetto ----------

        private static ProjectStore OpenStore(Args a)
        {
            var start = a.Opt("project") ?? Environment.GetEnvironmentVariable("VEDIT_PROJECT");
            return ProjectStore.Find(start ?? Environment.CurrentDirectory)
                   ?? throw new CliException($"Nessun progetto trovato (cerco {ProjectStore.FileName} qui e nelle cartelle superiori). Usa 'vedit init'.");
        }

        private static int Init(Args a)
        {
            var dir = a.Positional.FirstOrDefault() ?? Environment.CurrentDirectory;
            var project = Project.CreateDefault(a.Opt("name") ?? new DirectoryInfo(Path.GetFullPath(dir)).Name);
            if (a.Opt("size") is { } size)
            {
                var p = size.ToLowerInvariant().Split('x');
                if (p.Length != 2 || !int.TryParse(p[0], out var w) || !int.TryParse(p[1], out var h))
                    throw new CliException("--size nel formato 1920x1080.");
                project.Settings.Width = w;
                project.Settings.Height = h;
            }
            if (a.Num("fps") is double fps) project.Settings.Fps = fps;
            var store = ProjectStore.Create(dir, project);
            Console.WriteLine($"OK: progetto creato in {store.Directory}");
            return 0;
        }

        private static int Info(ProjectStore store, Project p, Args a)
        {
            if (a.Flag("json"))
            {
                Console.WriteLine(Json.Serialize(p));
                return 0;
            }
            var s = p.Settings;
            Console.WriteLine($"Progetto: {p.Name}  ({store.Directory})");
            Console.WriteLine($"Formato: {s.Width}x{s.Height} @ {F(s.Fps)} fps · durata {Time(p.GetDuration())} ({F(p.GetDuration())} s)");
            Console.WriteLine();
            Console.WriteLine("ASSET");
            foreach (var asset in p.Assets)
            {
                var tr = File.Exists(store.TranscriptPath(asset.Id)) ? " · trascritto" : "";
                var dims = asset.HasVideo ? $" {asset.Width}x{asset.Height}" + (asset.Fps > 0 ? $"@{F(asset.Fps)}" : "") : "";
                Console.WriteLine($"  {asset.Id,-5} {asset.Kind,-6} {Time(asset.Duration),10}{dims}{(asset.HasAudio ? " +audio" : "")}{tr}  {asset.Path}");
            }
            Console.WriteLine();
            Console.WriteLine("TRACCE (dall'alto; le tracce visive più in alto sono in primo piano)");
            foreach (var t in p.Tracks)
            {
                var flags = string.Concat(t.Muted ? " muta" : "", t.Hidden ? " nascosta" : "", t.Locked ? " bloccata" : "",
                    t.Duck ? " ducking" : "", Math.Abs(t.Volume - 1) > 1e-9 ? $" vol={F(t.Volume)}" : "");
                Console.WriteLine($"  {t.Id,-4} {t.Kind,-5} \"{t.Name}\"{flags} — {t.Clips.Count} clip");
                foreach (var c in t.Clips)
                {
                    var what = c.IsText ? $"testo \"{Short(c.Text!)}\""
                        : c.Mask is { } mk ? $"maschera {mk.Effect.ToString().ToLowerInvariant()}/{mk.Shape.ToString().ToLowerInvariant()} forza {F(mk.Strength)} " +
                            (mk.Keyframes.Count == 0 ? $"zona ({F(mk.X)},{F(mk.Y)},{F(mk.W)},{F(mk.H)})"
                                : $"{mk.Keyframes.Count} keyframe: " + string.Join(" ", mk.Keyframes.Select(k => $"{F(c.Start + k.T - c.In)}s→({F(k.X)},{F(k.Y)},{F(k.W)},{F(k.H)})")))
                        : $"{c.AssetId} [{F(c.In)}–{F(c.In + c.Duration)}]";
                    var extra = string.Concat(
                        Math.Abs(c.Volume - 1) > 1e-9 ? $" vol={F(c.Volume)}" : "",
                        c.FadeIn > 0 ? $" fadeIn={F(c.FadeIn)}" : "", c.FadeOut > 0 ? $" fadeOut={F(c.FadeOut)}" : "",
                        Math.Abs(c.Scale - 1) > 1e-9 || c.X != 0.5 || (!c.IsText && c.Y != 0.5) ? $" pos=({F(c.X)},{F(c.Y)}) scala={F(c.Scale)}" : "",
                        c.Opacity < 1 ? $" opacità={F(c.Opacity)}" : "",
                        c.Note is { Length: > 0 } ? $" // {c.Note}" : "");
                    Console.WriteLine($"      {c.Id,-6} {F(c.Start),9} → {F(c.End),-9} {what}{extra}");
                }
            }
            if (p.Markers.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("MARCATORI");
                foreach (var m in p.Markers) Console.WriteLine($"  {m.Id,-4} {F(m.Time),9}  {m.Label}");
            }
            var issues = new TimelineEditor(p).Validate();
            if (issues.Count > 0)
            {
                Console.WriteLine();
                foreach (var i in issues) Console.WriteLine(i);
            }
            return 0;
        }

        private static int Validate(TimelineEditor editor)
        {
            var issues = editor.Validate();
            if (issues.Count == 0) Console.WriteLine("OK: nessun problema.");
            foreach (var i in issues) Console.WriteLine(i);
            return issues.Any(i => i.StartsWith("ERRORE")) ? 1 : 0;
        }

        private static string Settings(Project project, Args a)
        {
            var patch = Args.ToPatch(a.Positional);
            if (patch.Remove("name", out var name)) project.Name = name?.ToString() ?? project.Name;
            project.Settings = Json.Merge(project.Settings, patch);
            return "impostazioni aggiornate";
        }

        /// <summary>Esegue una modifica, salva con etichetta nella cronologia e stampa il risultato e gli avvisi.</summary>
        private static int Save(ProjectStore store, Project project, string label, Func<string> action, TimelineEditor? editor = null)
        {
            var message = action();
            store.Save(project, label);
            Console.WriteLine("OK: " + message);
            foreach (var issue in (editor ?? new TimelineEditor(project)).Validate()) Console.WriteLine(issue);
            return 0;
        }

        // ---------- Media ----------

        private static int Probe(Args a)
        {
            var asset = MediaProbe.Probe(Path.GetFullPath(a.Pos(0, "file")));
            Console.WriteLine(Json.Serialize(asset));
            return 0;
        }

        private static async Task<int> Import(ProjectStore store, Project project, Args a, string label)
        {
            if (a.Positional.Count == 0) throw new CliException("Indica almeno un file.");
            var editor = new TimelineEditor(project);
            var at = a.Num("at");
            var lines = new List<string>();
            var imported = new List<string>();
            foreach (var file in a.Positional)
            {
                var asset = AssetImporter.Import(store, project, file, a.Flag("copy"));
                imported.Add(asset.Id);
                var line = $"{asset.Id}: {asset.Name} ({asset.Kind}, {Time(asset.Duration)})";
                if (a.Flag("add") && AssetImporter.AdoptFormatIfEmpty(project, asset))
                    line += $" · formato del progetto: {project.Settings.Width}x{project.Settings.Height} @ {F(project.Settings.Fps)} fps";
                if (a.Flag("add"))
                {
                    var track = a.Opt("track") is { } tid ? editor.GetTrack(tid) : AssetImporter.DefaultTrackFor(project, asset);
                    var clip = editor.AddClip(track.Id, asset.Id, at);
                    at = at is null ? null : clip.End;
                    line += $" → clip {clip.Id} su {track.Id} [{F(clip.Start)}–{F(clip.End)}]";
                }
                lines.Add(line);
            }
            store.Save(project, label);
            foreach (var l in lines) Console.WriteLine("OK: " + l);
            // Proxy e forme d'onda servono all'editor: si preparano subito (con NVENC è veloce).
            if (!a.Has("no-proxy")) await Proxy(store, project, Args.Parse(imported));
            return 0;
        }

        private static async Task<int> Proxy(ProjectStore store, Project project, Args a)
        {
            var assets = a.Positional.Count > 0
                ? a.Positional.Select(id => project.FindAsset(id) ?? throw new CliException($"Asset {id} non trovato.")).ToList()
                : project.Assets;
            foreach (var asset in assets)
            {
                Console.Write($"{asset.Id}: proxy");
                await MediaCache.EnsureProxyAsync(store, asset, a.Opt("encoder") ?? "auto");
                Console.Write(", miniatura");
                await MediaCache.EnsureThumbnailAsync(store, asset);
                if (asset.HasAudio)
                {
                    Console.Write(", forma d'onda");
                    await MediaCache.GetPeaksAsync(store, asset);
                }
                Console.WriteLine(" — pronto");
            }
            return 0;
        }

        private static string Track(TimelineEditor editor, Args a)
        {
            switch (a.Pos(0, "add|remove|move|set"))
            {
                case "add":
                    var kind = a.Pos(1, "video|audio|text").ToLowerInvariant() switch
                    {
                        "video" or "v" => TrackKind.Video,
                        "audio" or "a" => TrackKind.Audio,
                        "text" or "testo" or "t" => TrackKind.Text,
                        "mask" or "maschere" or "m" => TrackKind.Mask,
                        var k => throw new CliException($"Tipo di traccia non valido: {k}."),
                    };
                    var t = editor.AddTrack(kind, a.Opt("name"), a.Int("index"));
                    return $"traccia {t.Id} creata";
                case "remove":
                    editor.RemoveTrack(a.Pos(1, "id"));
                    return "traccia rimossa";
                case "move":
                    editor.MoveTrack(a.Pos(1, "id"), (int)a.PosTime(2, "indice"));
                    return "traccia spostata";
                case "set":
                    var tr = editor.SetTrack(a.Pos(1, "id"), Args.ToPatch(a.Positional.Skip(2)));
                    return $"traccia {tr.Id} aggiornata";
                default:
                    throw new CliException("Uso: track add|remove|move|set ...");
            }
        }

        private static string Mask(TimelineEditor editor, Args a)
        {
            switch (a.Pos(0, "add|key|unkey|clear"))
            {
                case "add":
                {
                    var spec = new MaskSpec();
                    if (a.Opt("rect") is { } rect) (spec.X, spec.Y, spec.W, spec.H) = ParseRect(rect);
                    if (a.Opt("effect") is { } effect) spec.Effect = ParseEnum<MaskEffect>(effect, "effect");
                    if (a.Opt("shape") is { } shape) spec.Shape = ParseEnum<MaskShape>(shape, "shape");
                    if (a.Num("strength") is double strength) spec.Strength = strength;
                    if (a.Opt("color") is { } color) spec.Color = color;
                    var clip = editor.AddMask(a.Opt("track"), a.Num("at") ?? throw new CliException("Manca --at."), a.Num("dur") ?? 3, spec);
                    return $"maschera {clip.Id} da {F(clip.Start)} a {F(clip.End)}";
                }
                case "key":
                {
                    var (x, y, w, h) = ParseRect(a.Pos(3, "x,y,w,h"));
                    var clip = editor.SetMaskRect(a.Pos(1, "clipId"), a.PosTime(2, "tempo"), x, y, w, h, asKeyframe: true);
                    return $"maschera {clip.Id}: {clip.Mask!.Keyframes.Count} keyframe";
                }
                case "unkey":
                {
                    var clip = editor.RemoveMaskKey(a.Pos(1, "clipId"), a.PosTime(2, "tempo"));
                    return $"maschera {clip.Id}: {clip.Mask!.Keyframes.Count} keyframe";
                }
                case "clear":
                {
                    var (_, clip) = editor.GetClip(a.Pos(1, "clipId"));
                    var m = clip.Mask ?? throw new CliException($"La clip {clip.Id} non è una maschera.");
                    var first = m.At(clip.In);
                    (m.X, m.Y, m.W, m.H) = (first.X, first.Y, first.W, first.H);
                    m.Keyframes.Clear();
                    return $"maschera {clip.Id} fissa";
                }
                default:
                    throw new CliException("Uso: mask add|key|unkey|clear ...");
            }
        }

        private static string Subtitles(ProjectStore store, Project project, TimelineEditor editor, Args a)
        {
            switch (a.Positional.FirstOrDefault() ?? "create")
            {
                case "create":
                {
                    var options = new SubtitleOptions();
                    if (a.Int("max-chars") is int mc) options.MaxChars = mc;
                    if (a.Int("lines") is int lines) options.MaxLines = lines;
                    if (a.Num("max-dur") is double md) options.MaxDuration = md;
                    if (a.Flag("keep-fillers")) options.SkipFillers = false;
                    var cues = SubtitleBuilder.Build(TimelineWords(store, project), options);
                    TextStyle? style = null;
                    if (a.Num("size") is double size) { style = editor.DefaultSubtitleStyle(); style.FontSize = size; }
                    var track = editor.SetSubtitles(cues, style, a.Num("y") ?? 0.88);
                    if (a.Num("y") is double y) foreach (var c in track.Clips) c.Y = y;
                    return $"{track.Clips.Count} sottotitoli sulla traccia {track.Id}";
                }
                case "style":
                {
                    var n = editor.ApplyStyleToTrack(a.Pos(1, "clipId"));
                    return $"stile applicato a {n} sottotitoli";
                }
                default:
                    throw new CliException("Uso: subtitles create|export|style ...");
            }
        }

        private static (double X, double Y, double W, double H) ParseRect(string text)
        {
            var p = text.Split(',', StringSplitOptions.TrimEntries);
            if (p.Length != 4 || !p.All(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                throw new CliException($"Zona non valida '{text}': usa x,y,w,h (valori 0-1, x e y sono il centro).");
            var n = p.Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            return (n[0], n[1], n[2], n[3]);
        }

        private static T ParseEnum<T>(string value, string name) where T : struct, Enum =>
            Enum.TryParse<T>(value, true, out var r) ? r
                : throw new CliException($"--{name}: valore non valido '{value}'. Validi: {string.Join(", ", Enum.GetNames<T>().Select(x => x.ToLowerInvariant()))}.");

        // ---------- Trascrizione ----------

        private static async Task<int> Transcribe(ProjectStore store, Project project, Args a)
        {
            List<Asset> assets;
            if (a.Positional.Count > 0)
            {
                assets = a.Positional.Select(id => project.FindAsset(id) ?? throw new CliException($"Asset {id} non trovato.")).ToList();
            }
            else
            {
                // Di default: i video con audio e i file audio usati su tracce senza ducking (non la musica).
                var onVoiceTracks = project.Tracks.Where(t => !t.Duck).SelectMany(t => t.Clips).Select(c => c.AssetId).ToHashSet();
                assets = project.Assets.Where(x => x.HasAudio && (x.Kind == AssetKind.Video || onVoiceTracks.Contains(x.Id))).ToList();
            }
            var model = a.Opt("model") ?? WhisperTranscriber.DefaultModel;
            var lang = a.Opt("lang") ?? project.Settings.Language;
            foreach (var asset in assets)
            {
                if (!a.Flag("force") && File.Exists(store.TranscriptPath(asset.Id)))
                {
                    Console.WriteLine($"{asset.Id}: già trascritto (usa --force per rifare).");
                    continue;
                }
                Console.WriteLine($"{asset.Id}: trascrizione di {asset.Name} ({Time(asset.Duration)})...");
                var lastPct = -1;
                var transcript = await WhisperTranscriber.TranscribeAsync(store, asset, model, lang, a.Opt("prompt"),
                    log: m => Console.WriteLine("  " + m),
                    progress: new Progress<double>(p =>
                    {
                        var pct = (int)(p * 100);
                        if (pct / 10 != lastPct / 10) Console.WriteLine($"  {pct}%");
                        lastPct = pct;
                    }));
                store.SaveTranscript(transcript);
                Console.WriteLine($"OK: {asset.Id} → {transcript.Words.Count} parole, {transcript.Segments.Count} frasi ({store.TranscriptPath(asset.Id)})");
            }
            return 0;
        }

        private static List<Word> TimelineWords(ProjectStore store, Project project)
        {
            var words = TranscriptTools.ToWords(TranscriptTools.ToTimeline(project, store.LoadTranscript));
            if (words.Count == 0)
                throw new CliException("Nessuna trascrizione sulla timeline. Esegui prima 'vedit transcribe'.");
            return words;
        }

        private static int Transcript(ProjectStore store, Project project, Args a)
        {
            List<Word> words;
            if (a.Positional.Count > 0)
            {
                var id = a.Positional[0];
                var t = store.LoadTranscript(id) ?? throw new CliException($"L'asset {id} non è trascritto. Usa 'vedit transcribe {id}'.");
                words = t.Words;
                if (!a.Flag("source"))
                {
                    words = TranscriptTools.ToWords(TranscriptTools.ToTimeline(project, x => x == id ? t : null));
                }
            }
            else
            {
                words = TimelineWords(store, project);
            }
            if (a.Flag("json")) Console.WriteLine(Json.Serialize(words));
            else if (a.Flag("srt")) Console.Write(TranscriptTools.ToSrt(words));
            else if (a.Flag("words")) Console.Write(TranscriptTools.FormatWords(words));
            else Console.Write(TranscriptTools.FormatReadable(words));
            return 0;
        }

        private static int Find(ProjectStore store, Project project, Args a)
        {
            var phrase = string.Join(' ', a.Positional);
            if (phrase.Length == 0) throw new CliException("Indica la frase da cercare.");
            var hits = TranscriptTools.Find(TimelineWords(store, project), phrase);
            if (hits.Count == 0) Console.WriteLine("Nessuna occorrenza.");
            foreach (var (s, e, text) in hits) Console.WriteLine($"{F(s)}–{F(e)}  [{Time(s)}]  {text}");
            return hits.Count > 0 ? 0 : 1;
        }

        private static int Fillers(ProjectStore store, Project project, Args a)
        {
            var hits = TranscriptTools.FindFillers(TimelineWords(store, project));
            if (hits.Count == 0)
            {
                Console.WriteLine("Nessun intercalare trovato.");
                return 0;
            }
            foreach (var (s, e, text) in hits) Console.WriteLine($"{F(s)}–{F(e)}  [{Time(s)}]  {text}");
            Console.WriteLine();
            Console.WriteLine("Per tagliarli tutti (controlla prima!):");
            Console.WriteLine($"vedit cut --ranges \"{string.Join(",", hits.Select(h => $"{F(h.Start)}-{F(h.End)}"))}\"");
            return 0;
        }

        private static async Task<int> Silences(ProjectStore store, Project project, Args a)
        {
            var (track, ranges) = await SilenceFinder.FindAsync(store, project, a.Opt("track"), a.Num("db") ?? -35,
                a.Num("min") ?? 0.6, a.Num("pad") ?? 0.15);
            Console.WriteLine($"Pause sulla traccia {track.Id}: {ranges.Count} (totale {F(ranges.Sum(r => r.To - r.From))} s)");
            foreach (var (f, t) in ranges) Console.WriteLine($"{F(f)}–{F(t)}  [{Time(f)}]  {F(t - f)} s");
            if (ranges.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Per tagliarle tutte:");
                Console.WriteLine($"vedit cut --ranges \"{string.Join(",", ranges.Select(r => $"{F(r.From)}-{F(r.To)}"))}\"");
            }
            return 0;
        }

        // ---------- Rendering ----------

        private static async Task<int> Render(ProjectStore store, Project project, Args a)
        {
            var options = new RenderOptions
            {
                Preview = a.Flag("preview"),
                Output = a.Opt("out"),
                Encoder = a.Opt("encoder") ?? "auto",
                Quality = a.Int("quality"),
                From = a.Num("from") ?? 0,
                To = a.Num("to"),
                Height = a.Int("height"),
            };
            var last = -1;
            var result = await Renderer.RenderAsync(store, project, options, new Progress<double>(p =>
            {
                var pct = (int)(p * 100);
                if (pct / 10 != last / 10) Console.WriteLine($"  {pct}%");
                last = pct;
            }));
            Console.WriteLine($"OK: {result.Output} ({Time(result.Duration)}, {result.Encoder}, in {result.Elapsed.TotalSeconds:0.0} s)");
            return 0;
        }

        // ---------- Editor ----------

        private static async Task<int> App(Args a)
        {
            var cmd = a.Pos(0, "status|seek|play|pause|select|reload|say");
            var args = new JsonObject();
            switch (cmd)
            {
                case "seek": args["time"] = a.PosTime(1, "tempo"); break;
                case "select": args["clipId"] = a.Pos(1, "clipId"); break;
                case "say": args["text"] = string.Join(' ', a.Positional.Skip(1)); break;
                case "status" or "play" or "pause" or "reload": break;
                default: throw new CliException($"Comando app sconosciuto: {cmd}.");
            }
            var data = await AppPipe.SendAsync(cmd, args);
            Console.WriteLine(data?.ToJsonString(Json.Options) ?? "OK");
            return 0;
        }

        // ---------- Supporto ----------

        private static List<(double From, double To)> ParseRanges(string text)
        {
            var result = new List<(double, double)>();
            foreach (var part in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                // Separatore "-" tra due tempi (i tempi non sono mai negativi).
                var idx = part.IndexOf('-', 1);
                if (idx <= 0) throw new CliException($"Intervallo non valido '{part}' (usa inizio-fine).");
                result.Add((Args.ParseTime(part[..idx], "inizio"), Args.ParseTime(part[(idx + 1)..], "fine")));
            }
            return result;
        }

        private static List<string>? SplitList(string? s) =>
            s?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        private static string Short(string s) => s.Replace('\n', ' ') is var t && t.Length > 40 ? t[..40] + "…" : t;

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Time(double v) => TranscriptTools.Time(v);
    }
}
