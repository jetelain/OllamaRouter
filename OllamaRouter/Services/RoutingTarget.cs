using System.Text.Json;
using System.Text.Json.Serialization;

namespace OllamaRouter.Services;

/// <summary>
/// <see cref="JsonConverter{T}"/> that maps <see cref="RoutingTarget"/> to/from a JSON string and,
/// crucially, supports use as a dictionary key by overriding
/// <see cref="JsonConverter{TRoot}.WriteAsPropertyName"/> and
/// <see cref="JsonConverter{TRoot}.ReadAsPropertyName"/>. This is required for both the
/// source-generated <c>OllamaRouterJsonSerializerContext</c> and reflection-based serialization,
/// because an implicit string conversion alone is not honored for dictionary keys by
/// <see cref="JsonSerializer"/>.
/// </summary>
public sealed class RoutingTargetJsonConverter : JsonConverter<RoutingTarget>
{
    public override RoutingTarget Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, RoutingTarget value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);

    public override bool CanConvert(Type t) => t == typeof(RoutingTarget);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, RoutingTarget value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value);

    public override RoutingTarget ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? "");
}

/// <summary>
/// Identifies a routing target by name. Fleet targets are open-ended (any configured name),
/// while <see cref="Local"/>, <see cref="Remote"/> and <see cref="Cloud"/> are the well-known
/// legacy targets. <see cref="Cloud"/> is a pseudo-target that is always available in code:
/// the request is relayed through the local instance to ollama.com with the model name
/// rewritten to the configured cloud equivalent.
/// <para>
/// Equality and hashing are case-insensitive (OrdinalIgnoreCase) so that target names from
/// configuration files, <c>X-Ollama-Target</c> header pins and JSON state files all match the
/// same target (e.g. "local" == <see cref="Local"/>).
/// </para>
/// </summary>
[JsonConverter(typeof(RoutingTargetJsonConverter))]
public readonly struct RoutingTarget
{
    /// <summary>
    /// The name of the routing target (e.g. "Local", "desktop", "cloud").
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// The legacy local Ollama instance (signed in to ollama.com, also serves as the cloud relay).
    /// </summary>
    public static readonly RoutingTarget Local = new("Local");

    /// <summary>
    /// The legacy remote Ollama instance.
    /// </summary>
    public static readonly RoutingTarget Remote = new("Remote");

    /// <summary>
    /// Overflow target used when all fleet targets are busy: the request is still sent to the
    /// relay instance (which relays to ollama.com cloud using its own signed-in credentials),
    /// but the model name is rewritten to the configured cloud equivalent.
    /// </summary>
    public static readonly RoutingTarget Cloud = new("Cloud");

    public RoutingTarget(string? value)
    {
        Value = value ?? "";
    }

    /// <summary>
    /// Converts a target name (e.g. from a header pin or JSON state file) into a <see cref="RoutingTarget"/>.
    /// </summary>
    public static implicit operator RoutingTarget(string? value) => new(value);

    /// <summary>
    /// Converts a <see cref="RoutingTarget"/> into its name.
    /// </summary>
    public static implicit operator string(RoutingTarget target) => target.Value;

    public static bool operator ==(RoutingTarget left, RoutingTarget right) =>
        string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

    public static bool operator !=(RoutingTarget left, RoutingTarget right) =>
        !(left == right);

    // Note: no IEquatable<RoutingTarget> / Equals(RoutingTarget) overload is declared. With the
    // implicit string conversion present, such an overload would make calls like
    // target.Equals("name") silently convert the string and return true, which is surprising.
    // Routing through object.Equals keeps string comparison explicit via the == operator.
    public override bool Equals(object? obj) =>
        obj is RoutingTarget other && this == other;

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Value ?? "");

    public override string ToString() => Value;
}
