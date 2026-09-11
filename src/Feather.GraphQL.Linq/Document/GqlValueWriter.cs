using System.Collections;
using System.Text.Json;
using Feather.GraphQL.Linq.Filtering;

namespace Feather.GraphQL.Linq.Document;

/// <summary>
/// Writes a CLR value as the JSON a server expects for it.
/// </summary>
/// <remarks>
/// The conversions the lowering used to perform eagerly, into <c>JsonValue</c> wrappers, done at
/// the point of writing instead. Nothing between the two ever looked at a value, so the wrappers
/// existed only to be unwrapped again.
/// </remarks>
internal static class GqlValueWriter
{
    public static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); return;
            case string s: writer.WriteStringValue(s); return;
            case bool b: writer.WriteBooleanValue(b); return;
            case int i: writer.WriteNumberValue(i); return;
            case long l: writer.WriteNumberValue(l); return;
            case short sh: writer.WriteNumberValue(sh); return;
            case byte by: writer.WriteNumberValue(by); return;
            case double d: writer.WriteNumberValue(d); return;
            case float f: writer.WriteNumberValue(f); return;
            case decimal m: writer.WriteNumberValue(m); return;
            case Guid g: writer.WriteStringValue(g.ToString()); return;
            case DateTime dt: writer.WriteStringValue(dt.ToString("O")); return;
            case DateTimeOffset dto: writer.WriteStringValue(dto.ToString("O")); return;
            case DateOnly date: writer.WriteStringValue(date.ToString("O")); return;
            case TimeOnly time: writer.WriteStringValue(time.ToString("O")); return;

            // An enum reaches the wire as its schema name, which is not its CLR name. Everything
            // else is the shared conversion, which generated code calls too.
            case GqlEnumValue enumValue: writer.WriteStringValue(enumValue.Name); return;
            case Enum e: writer.WriteStringValue(GqlEnumNaming.Of(e)); return;
        }

        if (value is IEnumerable enumerable)
        {
            writer.WriteStartArray();

            foreach (object? item in enumerable)
                Write(writer, item);

            writer.WriteEndArray();
            return;
        }

        throw GraphQLTranslationException.UnsupportedPredicate(
            $"values of type '{value.GetType().Name}' cannot be sent as a filter value");
    }
}
