using System;
using System.IO;
using System.Reflection;

namespace VideoEditor.Core;

/// <summary>File di testo incorporati nella libreria.</summary>
public static class Templates
{
    /// <summary>Istruzioni per Claude Code copiate in ogni nuovo progetto.</summary>
    public static string ClaudeMd()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("VideoEditor.Core.Templates.CLAUDE.md")
                      ?? throw new InvalidOperationException("Risorsa CLAUDE.md mancante.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private const string Header = "# Progetto video — istruzioni per Claude";
    private const string NotesMarker = "## Note del progetto";

    /// <summary>
    /// Crea o aggiorna il CLAUDE.md di un progetto. Se il file è quello generato dall'editor viene
    /// riscritto con la versione attuale, conservando quanto scritto dall'utente sotto «Note del progetto»;
    /// un CLAUDE.md scritto a mano (senza l'intestazione dell'editor) non viene toccato.
    /// </summary>
    public static void UpdateProjectClaudeMd(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, "CLAUDE.md");
        var template = ClaudeMd();
        if (!File.Exists(path))
        {
            File.WriteAllText(path, template);
            return;
        }
        var current = File.ReadAllText(path);
        if (!current.StartsWith(Header, StringComparison.Ordinal)) return;
        var idx = current.IndexOf(NotesMarker, StringComparison.Ordinal);
        var userNotes = idx >= 0 ? current[(idx + NotesMarker.Length)..] : "";
        var tIdx = template.IndexOf(NotesMarker, StringComparison.Ordinal);
        var updated = idx >= 0 && tIdx >= 0 ? template[..(tIdx + NotesMarker.Length)] + userNotes : template;
        if (updated != current) File.WriteAllText(path, updated);
    }
}
