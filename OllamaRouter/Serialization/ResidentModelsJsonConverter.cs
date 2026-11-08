using System.Text.Json;
using System.Text.Json.Serialization;

namespace OllamaRouter.Serialization;

/// <summary>
/// Task #45 (residency visibility): the fleet/monitor <c>residentModels</c> field is ALWAYS an
/// array in the wire contract (empty array when idle, never null, never omitted) even though
/// the DTO property is nullable (a nullable default of <c>null</c> is the only legal
/// compile-time default for an <see cref="IReadOnlyList{T}" /> record parameter). This
/// converter writes null as <c>[]</c> and reads any JSON array (including <c>[]</c>) back;
/// a JSON null reads as null. <see cref="HandleNull"/> is enabled so the serializer cannot
/// short-circuit a null value past <see cref="Write"/> (which would emit a JSON null and
/// break the always-array contract). AOT/source-generation safe (no reflection, pure
/// reader/writer primitives).
/// </summary>
public sealed class ResidentModelsJsonConverter : JsonConverter<IReadOnlyList<string>?>
{
    /// <summary>
    /// Required: without it the serializer writes a raw JSON <c>null</c> for a null property
    /// without consulting <see cref="Write"/>, violating the always-array wire contract.
    /// </summary>
    public override bool HandleNull => true;

    public override IReadOnlyList<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected a JSON array for the residentModels field but found {reader.TokenType}.");
        }

        // Pure reader primitives (no JsonSerializer round-trip) so the read path stays
        // trim/NativeAOT safe exactly like the write path.
        var models = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Expected a JSON string entry for the residentModels field but found {reader.TokenType}.");
            }

            models.Add(reader.GetString()!);
        }

        return models;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string>? value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        if (value is not null)
        {
            foreach (var name in value)
            {
                writer.WriteStringValue(name);
            }
        }

        writer.WriteEndArray();
    }
}
