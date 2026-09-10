using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feather.GraphQL.Linq.Filtering;

/// <summary>
/// An enum value on its way to the server, carried as itself rather than as a string.
/// </summary>
/// <remarks>
/// In a variables payload an enum is JSON text — <c>"ASC"</c> — and indistinguishable from a
/// string, which is fine while the server does the coercing. Inlined into a document it is a
/// GraphQL enum literal and must be <em>un</em>quoted, so the distinction has to survive
/// translation. Wrapping it here keeps one representation for both: the converter writes the
/// same JSON as before, and the inline printer can still tell the two apart.
/// </remarks>
[JsonConverter(typeof(GqlEnumValueConverter))]
internal readonly record struct GqlEnumValue(string Name);

/// <summary>Writes a <see cref="GqlEnumValue"/> as the plain JSON string the wire expects.</summary>
internal sealed class GqlEnumValueConverter : JsonConverter<GqlEnumValue>
{
    public override GqlEnumValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, GqlEnumValue value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Name);
}
