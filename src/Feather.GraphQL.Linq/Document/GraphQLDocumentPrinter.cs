using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Feather.GraphQL.Linq.Filtering;

namespace Feather.GraphQL.Linq.Document;

/// <summary>
/// Prints a <see cref="GqlDocument"/> canonically: deterministic ordering, deterministic
/// variable numbering, single-space separators, no incidental whitespace.
/// </summary>
/// <remarks>
/// Canonical output is a requirement rather than a nicety. It is what lets a hash of the printed
/// text serve as a stable APQ key across processes, and what makes golden-file tests meaningful —
/// two structurally identical queries must print byte-identically.
/// </remarks>
internal static class GraphQLDocumentPrinter
{
    public static string Print(GqlDocument document)
    {
        var builder = new StringBuilder("query");

        if (document.Variables.Count > 0)
        {
            builder.Append('(');
            for (int i = 0; i < document.Variables.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append('$').Append(document.Variables[i].Name)
                    .Append(": ").Append(document.Variables[i].Type);
            }

            builder.Append(')');
        }

        builder.Append(" { ");
        PrintField(builder, document.Root);
        builder.Append(" }");

        return builder.ToString();
    }

    /// <summary>
    /// Prints the same document with every variable substituted for its value.
    /// </summary>
    /// <remarks>
    /// For reading, not for sending. The parameterized form is what goes over the wire — it is
    /// what makes one APQ hash cover every predicate of a given shape, and what makes injection
    /// structurally impossible — but it also means the printed document says <c>where: $v0</c>
    /// and nothing about what was actually asked. This form answers that question, and is
    /// self-contained enough to paste into a playground.
    /// </remarks>
    /// <summary>
    /// Relaxed escaping, because this output is read by people and pasted into playgrounds
    /// rather than embedded in HTML. The default encoder would render a quote as <c>\u0022</c>
    /// and every accented letter as an escape — valid GraphQL, unreadable as a diagnostic.
    /// </summary>
    private static readonly JsonSerializerOptions _literals =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string PrintInline(GqlDocument document)
    {
        var values = new Dictionary<string, GqlValue?>(document.Variables.Count, StringComparer.Ordinal);
        foreach (var variable in document.Variables)
            values[variable.Name] = variable.Value;

        // No variables are declared, because none are referenced.
        var builder = new StringBuilder("query { ");
        PrintField(builder, document.Root, values);
        builder.Append(" }");

        return builder.ToString();
    }

    private static void PrintField(StringBuilder builder, GqlField field, Dictionary<string, GqlValue?> values)
    {
        builder.Append(field.Name);

        if (field.Arguments.Count > 0)
        {
            builder.Append('(');
            for (int i = 0; i < field.Arguments.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(field.Arguments[i].Name).Append(": ");
                PrintValue(builder, values.GetValueOrDefault(field.Arguments[i].VariableName));
            }

            builder.Append(')');
        }

        if (field.Selection.Count == 0)
            return;

        builder.Append(" { ");
        for (int i = 0; i < field.Selection.Count; i++)
        {
            if (i > 0)
                builder.Append(' ');

            PrintField(builder, field.Selection[i], values);
        }

        builder.Append(" }");
    }

    /// <summary>
    /// Writes a JSON value as a GraphQL literal. The two languages agree on numbers, booleans,
    /// null and string escaping, so those are handed to the JSON writer; the two that differ are
    /// object keys, which GraphQL leaves unquoted, and enum values, which it un-quotes entirely.
    /// </summary>
    private static void PrintValue(StringBuilder builder, GqlValue? node)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                return;

            case GqlList list:
            {
                builder.Append('[');

                for (int i = 0; i < list.Items.Count; i++)
                {
                    if (i > 0)
                        builder.Append(", ");

                    PrintValue(builder, list.Items[i]);
                }

                builder.Append(']');
                return;
            }

            case GqlObject obj:
            {
                builder.Append('{');
                bool first = true;

                foreach (var (key, value) in obj.Fields)
                {
                    if (!first)
                        builder.Append(", ");

                    first = false;
                    builder.Append(key).Append(": ");
                    PrintValue(builder, value);
                }

                builder.Append('}');
                return;
            }

            case GqlScalar scalar:
                PrintScalar(builder, scalar.Value);
                return;
        }
    }

    /// <summary>
    /// Writes one value as a GraphQL literal.
    /// </summary>
    /// <remarks>
    /// The two languages agree on numbers, booleans, null and string escaping, so those go
    /// through the JSON writer. The one that differs is an enum, which GraphQL leaves unquoted —
    /// which is the whole reason an enum is carried as itself rather than as text.
    /// </remarks>
    private static void PrintScalar(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                return;

            case GqlEnumValue enumValue:
                builder.Append(enumValue.Name);
                return;

            case Enum e:
                builder.Append(GqlEnumNaming.Of(e));
                return;

            case string text:
                builder.Append(JsonSerializer.Serialize(text, _literals));
                return;

            case bool flag:
                builder.Append(flag ? "true" : "false");
                return;

            case System.Collections.IEnumerable items and not string:
            {
                builder.Append('[');
                bool first = true;

                foreach (object? item in items)
                {
                    if (!first)
                        builder.Append(", ");

                    first = false;
                    PrintScalar(builder, item);
                }

                builder.Append(']');
                return;
            }

            default:
                builder.Append(JsonSerializer.Serialize(value, value.GetType(), _literals));
                return;
        }
    }

    private static void PrintField(StringBuilder builder, GqlField field)
    {
        builder.Append(field.Name);

        if (field.Arguments.Count > 0)
        {
            builder.Append('(');
            for (int i = 0; i < field.Arguments.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(field.Arguments[i].Name).Append(": $").Append(field.Arguments[i].VariableName);
            }

            builder.Append(')');
        }

        if (field.Selection.Count == 0)
            return;

        builder.Append(" { ");
        for (int i = 0; i < field.Selection.Count; i++)
        {
            if (i > 0)
                builder.Append(' ');

            PrintField(builder, field.Selection[i]);
        }

        builder.Append(" }");
    }
}
