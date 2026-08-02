using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartMacro.Macros.Model;

/// <summary>
/// THE serializer for macro graph files. Storage (W0.2b), tests, and any tooling must go
/// through this helper so every consumer agrees on one JSON dialect: <c>$type</c>
/// discriminators, enums as strings, indented output, nulls omitted.
/// </summary>
public static class MacroGraphJson
{
    /// <summary>
    /// The options every macro-graph (de)serialization uses. Exposed for consumers that
    /// need to embed a graph in a larger payload; do not mutate.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Hand-edited files shouldn't break just because "$type" isn't the first property.
        AllowOutOfOrderMetadataProperties = true,
    };

    /// <summary>Serializes a graph to indented JSON.</summary>
    public static string Serialize(MacroGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return JsonSerializer.Serialize(graph, Options);
    }

    /// <summary>
    /// Deserializes a graph from JSON. Malformed input — including an unknown <c>$type</c>
    /// discriminator or a literal <c>null</c> document — throws <see cref="JsonException"/>.
    /// </summary>
    public static MacroGraph Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        MacroGraph? graph;
        try
        {
            graph = JsonSerializer.Deserialize<MacroGraph>(json, Options);
        }
        catch (NotSupportedException ex)
        {
            // STJ reports some polymorphism failures as NotSupportedException; normalize
            // so callers only ever have to handle JsonException.
            throw new JsonException("Macro graph JSON is not deserializable.", ex);
        }

        return graph ?? throw new JsonException("Macro graph JSON is 'null'.");
    }
}
