using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace VideoEditor.Cli;

/// <summary>Analisi semplice degli argomenti: posizionali, <c>--opzione valore</c> e flag booleani.</summary>
internal sealed class Args
{
    private static readonly HashSet<string> Flags =
    [
        "json", "ripple", "grid", "keep-fillers", "fill", "fit", "only", "copy", "add", "preview", "force", "source", "words", "srt", "help", "timeline", "all",
    ];

    public List<string> Positional { get; } = [];
    public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(IEnumerable<string> argv)
    {
        var a = new Args();
        var list = argv.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var t = list[i];
            if (t == "-p") t = "--project";
            if (t == "-h") t = "--help";
            if (t.StartsWith("--", StringComparison.Ordinal) && t.Length > 2)
            {
                var key = t[2..];
                var eq = key.IndexOf('=');
                if (eq > 0)
                {
                    a.Options[key[..eq]] = key[(eq + 1)..];
                }
                else if (Flags.Contains(key) || i + 1 >= list.Count || list[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    a.Options[key] = "true";
                }
                else
                {
                    a.Options[key] = list[++i];
                }
            }
            else
            {
                a.Positional.Add(t);
            }
        }
        return a;
    }

    public bool Has(string key) => Options.ContainsKey(key);
    public bool Flag(string key) => Options.TryGetValue(key, out var v) && v != "false";
    public string? Opt(string key) => Options.GetValueOrDefault(key);

    public double? Num(string key) => Options.TryGetValue(key, out var v) ? ParseTime(v, key) : null;

    public int? Int(string key) => Options.TryGetValue(key, out var v)
        ? int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new CliException($"--{key}: numero intero non valido '{v}'.")
        : null;

    public string Pos(int index, string name) =>
        index < Positional.Count ? Positional[index] : throw new CliException($"Manca l'argomento <{name}>.");

    public double PosTime(int index, string name) => ParseTime(Pos(index, name), name);

    /// <summary>Accetta secondi (12.5), mm:ss(.cc) o hh:mm:ss(.cc).</summary>
    public static double ParseTime(string value, string name)
    {
        var v = value.Trim().Replace(',', '.');
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        var parts = v.Split(':');
        if (parts.Length is 2 or 3 && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            var nums = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            return parts.Length == 2 ? nums[0] * 60 + nums[1] : nums[0] * 3600 + nums[1] * 60 + nums[2];
        }
        throw new CliException($"{name}: tempo non valido '{value}' (usa secondi, mm:ss o hh:mm:ss).");
    }

    /// <summary>
    /// Converte coppie <c>chiave=valore</c> in una patch JSON. Il valore è interpretato come JSON
    /// (numeri, true/false, null, oggetti) e altrimenti come stringa; <c>style.fontSize=80</c> crea oggetti annidati.
    /// </summary>
    public static JsonObject ToPatch(IEnumerable<string> pairs)
    {
        var patch = new JsonObject();
        foreach (var pair in pairs)
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) throw new CliException($"Atteso chiave=valore, trovato '{pair}'.");
            var key = pair[..eq].Trim();
            var raw = pair[(eq + 1)..];
            JsonNode? value;
            try { value = raw.Length == 0 ? JsonValue.Create("") : JsonNode.Parse(raw.Replace("\\n", "\n")); }
            catch (System.Text.Json.JsonException) { value = JsonValue.Create(raw.Replace("\\n", "\n")); }
            var path = key.Split('.');
            var target = patch;
            for (var i = 0; i < path.Length - 1; i++)
            {
                if (target[path[i]] is not JsonObject child) target[path[i]] = child = new JsonObject();
                target = child;
            }
            target[path[^1]] = value;
        }
        return patch;
    }
}

internal sealed class CliException(string message) : Exception(message);
