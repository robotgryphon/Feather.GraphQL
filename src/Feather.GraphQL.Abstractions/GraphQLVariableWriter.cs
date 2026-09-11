using System.Collections;
using System.Text.Json;
using JetBrains.Annotations;

namespace Feather.GraphQL;

/// <summary>
/// Writes a CLR value as the JSON a GraphQL server expects for it.
/// </summary>
/// <remarks>
/// Public because generated code calls it. A filter whose shape the compiler printed still has
/// to put a runtime value at each leaf, and the rules for turning one into JSON — an
/// <see cref="DateTime"/> is ISO text, a <see cref="Guid"/> is its string form — are the
/// library's, not something to re-emit into every consumer's assembly.
/// </remarks>
[PublicAPI]
public static class GraphQLVariableWriter
{
    /// <summary>Writes one value.</summary>
    public static void Write(Utf8JsonWriter writer, object? value)
    {
        ArgumentNullException.ThrowIfNull(writer);

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
        }

        if (value is IEnumerable items)
        {
            writer.WriteStartArray();

            foreach (object? item in items)
                Write(writer, item);

            writer.WriteEndArray();
            return;
        }

        throw new NotSupportedException(
            $"Values of type '{value.GetType().Name}' cannot be sent as a filter value.");
    }
}
