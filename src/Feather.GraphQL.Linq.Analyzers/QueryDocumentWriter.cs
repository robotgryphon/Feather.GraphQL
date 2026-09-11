using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// Prints the document a chain would produce, at compile time.
/// </summary>
/// <remarks>
/// A line-for-line mirror of the runtime translator's <c>Build</c> and the printer it feeds,
/// restricted to what can be known without values. It must agree with them byte for byte, and
/// the corpus test is what holds it to that.
/// </remarks>
internal static class QueryDocumentWriter
{
    /// <summary>The printed document, or null when this chain cannot be printed early.</summary>
    public static string? TryWrite(ChainFacts facts, SemanticModel model, CancellationToken token)
    {
        if (!ApplyResult(facts))
            return null;

        // Cursor paging has no offset to skip to — FGQL008.
        if (facts.HasSkip && facts.Paging == Paging.Cursor)
            return null;

        var selection = Selection(facts, model, token);
        if (selection is null)
            return null;

        var variables = new List<(string Name, string Type)>();
        var arguments = new List<(string Name, string Variable)>();

        string Bind(string argument, string type)
        {
            string name = "v" + variables.Count;
            variables.Add((name, type));
            arguments.Add((argument, name));
            return name;
        }

        // The binding order is the document's variable numbering, so it must match exactly.
        if (facts.HasFilter)
            Bind(facts.FilterArgument, facts.FilterInput ?? facts.ElementType.Name + "FilterInput");

        if (facts.HasOrdering)
            Bind(facts.OrderArgument, "[" + (facts.SortInput ?? facts.ElementType.Name + "SortInput") + "!]");

        if (facts.HasTake)
            Bind(facts.Paging == Paging.Cursor ? facts.FirstArgument : facts.TakeArgument, "Int");

        if (facts.HasSkip)
            Bind(facts.SkipArgument, "Int");

        if (facts.HasLast)
            Bind(facts.LastArgument, "Int");

        var builder = new StringBuilder("query");

        if (variables.Count > 0)
        {
            builder.Append('(');
            for (int i = 0; i < variables.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append('$').Append(variables[i].Name).Append(": ").Append(variables[i].Type);
            }

            builder.Append(')');
        }

        builder.Append(" { ").Append(facts.RootField);

        if (arguments.Count > 0)
        {
            builder.Append('(');
            for (int i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(arguments[i].Name).Append(": $").Append(arguments[i].Variable);
            }

            builder.Append(')');
        }

        builder.Append(" { ").Append(selection).Append(" } }");

        return builder.ToString();
    }

    /// <summary>
    /// Turns the terminal operator into the arguments the server is asked for. Mirrors
    /// <c>ApplyResultOperator</c>; where that throws, this declines.
    /// </summary>
    private static bool ApplyResult(ChainFacts facts)
    {
        switch (facts.Result)
        {
            case ResultKind.Sequence:
                return true;

            case ResultKind.First:
            case ResultKind.FirstOrDefault:
            case ResultKind.Any:
            case ResultKind.Single:
            case ResultKind.SingleOrDefault:
                facts.HasTake = true;
                return true;

            case ResultKind.Last:
            case ResultKind.LastOrDefault:
                if (facts.Paging != Paging.Cursor)
                    return false;

                facts.HasTake = false;
                facts.HasLast = true;
                return true;

            case ResultKind.Count:
            case ResultKind.LongCount:
                return facts.Paging != Paging.None && !facts.HasPaging;

            default:
                return false;
        }
    }

    /// <summary>What to ask for, wrapped in whatever the server pages with.</summary>
    private static string? Selection(ChainFacts facts, SemanticModel model, CancellationToken token)
    {
        // A count asks the wrapper itself, so it is never wrapped.
        if (facts.IsCount)
            return "totalCount";

        string? inner = facts.Result == ResultKind.Any
            ? CheapestScalar(facts.ElementType)
            : Fields(facts, model, token);

        if (inner is null)
            return null;

        return facts.Paging switch
        {
            Paging.Cursor => "nodes { " + inner + " }",
            Paging.Offset => "items { " + inner + " }",
            _ => inner
        };
    }

    private static string? Fields(ChainFacts facts, SemanticModel model, CancellationToken token)
    {
        var node = SelectionSetWriter.Build(facts.ElementType, facts.Projection, model, token);
        if (node is null)
            return null;

        var builder = new StringBuilder();
        SelectionSetWriter.Print(builder, node);

        return builder.ToString();
    }

    /// <summary>
    /// A selection set cannot be empty, so an existence check still names one field — the first
    /// scalar, since <c>Any()</c> throws the value away.
    /// </summary>
    private static string? CheapestScalar(ITypeSymbol elementType)
    {
        foreach (var property in GraphQLTypeFacts.Fields(elementType))
        {
            if (GraphQLTypeFacts.IsScalar(property.Type))
                return GraphQLTypeFacts.FieldName(property);
        }

        return null;
    }
}
