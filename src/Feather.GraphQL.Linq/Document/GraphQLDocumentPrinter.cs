using System.Text;

namespace Feather.GraphQL.Linq.Document;

/// <summary>
/// Prints a <see cref="GqlDocument"/> canonically: deterministic ordering, deterministic
/// variable numbering, single-space separators, no incidental whitespace.
/// </summary>
/// <remarks>
/// Canonical output is a requirement rather than a nicety. It is what makes the SHA-256 that
/// <c>GraphQLQuery</c> computes a stable APQ key across processes, and what makes golden-file
/// tests meaningful — two structurally identical queries must print byte-identically.
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
