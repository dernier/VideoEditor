using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VideoEditor.Core.Model;

/// <summary>Opzioni JSON comuni: camelCase, indentato, enum come stringhe.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create(indented: true);
    public static readonly JsonSerializerOptions Compact = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Options : Compact);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("JSON vuoto.");

    /// <summary>
    /// Applica una patch parziale (merge JSON) a un oggetto: le proprietà presenti nella patch
    /// sostituiscono quelle esistenti, <c>null</c> le azzera.
    /// </summary>
    public static T Merge<T>(T target, JsonObject patch)
    {
        var node = JsonSerializer.SerializeToNode(target, Options)!.AsObject();
        foreach (var (key, value) in patch)
        {
            var existing = node.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Key;
            var prop = typeof(T).GetProperty(key, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (prop is null || !prop.CanWrite || prop.IsDefined(typeof(JsonIgnoreAttribute), true))
                throw new ArgumentException($"Proprietà sconosciuta: '{key}'.");
            var name = existing ?? JsonNamingPolicy.CamelCase.ConvertName(key);
            if (value is JsonObject childPatch && node[name] is JsonObject childTarget)
            {
                foreach (var (k, v) in childPatch) childTarget[k] = v?.DeepClone();
            }
            else
            {
                node[name] = value?.DeepClone();
            }
        }
        return node.Deserialize<T>(Options) ?? throw new InvalidDataException("Patch non valida.");
    }
}
