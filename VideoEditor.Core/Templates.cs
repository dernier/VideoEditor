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
}
