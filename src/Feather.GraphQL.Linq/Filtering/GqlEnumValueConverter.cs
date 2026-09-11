using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feather.GraphQL.Linq.Filtering;

/// <summary>Writes a <see cref="GqlEnumValue"/> as the plain JSON string the wire expects.</summary>
internal sealed class GqlEnumValueConverter : JsonConverter<GqlEnumValue>
{
    public override GqlEnumValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, GqlEnumValue value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Name);
}