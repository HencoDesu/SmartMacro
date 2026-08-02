using System.Text.Json;
using SmartMacro.Macros.Model;

namespace SmartMacro.Contracts.Ipc;

/// <summary>
/// THE serializer for everything that crosses the control pipe — envelopes, payloads and
/// the macro graphs embedded in them.
///
/// The options are a copy of <see cref="MacroGraphJson.Options"/>, which is the point: the
/// <c>$type</c> polymorphism of nodes and triggers is configured by attributes on the
/// model, but the string-enum converter and the out-of-order metadata allowance are not,
/// and a second hand-rolled options object would drift from the file dialect until a
/// graph that saves fine fails to cross the wire. The one deliberate difference is
/// <see cref="JsonSerializerOptions.WriteIndented"/>: the transport is JSON Lines, so a
/// message must never contain a newline.
/// </summary>
public static class IpcJson
{
    /// <summary>
    /// The options every IPC (de)serialization uses. Read-only — take a copy if you need
    /// a variant.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// Serializes a payload object into the <see cref="JsonElement"/> an envelope carries.
    /// </summary>
    public static JsonElement Write<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    /// <summary>
    /// Materializes an envelope payload into its typed form. A missing payload (<c>null</c>
    /// element) or a JSON <c>null</c> yields <c>default</c> — an argument-less message read
    /// as a typed payload is the caller's mistake to detect, not an exception here.
    /// </summary>
    /// <exception cref="JsonException">The payload is present but doesn't fit <typeparamref name="T"/>.</exception>
    public static T? Read<T>(JsonElement? payload) =>
        payload is not { } element || element.ValueKind == JsonValueKind.Null
            ? default
            : element.Deserialize<T>(Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(MacroGraphJson.Options)
        {
            // JSON Lines: one message per line, so no pretty-printing.
            WriteIndented = false,
        };
        // populateMissingResolver: the copy inherits no TypeInfoResolver (MacroGraphJson's
        // is filled in lazily on first use), and MakeReadOnly() refuses to freeze options
        // without one. This installs the default reflection resolver, which is what we'd
        // get implicitly anyway.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
