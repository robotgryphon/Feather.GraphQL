using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>What one of a document's variables carries.</summary>
/// <remarks>
/// <c>Filter</c> is a whole filter passed as one value of the schema's filter input type;
/// <c>FilterValue</c> is a single comparison's value, for a filter whose structure went into the
/// document instead. There is no member for a last page: only a terminal asks for one, so its
/// size is always a number the document carries itself rather than a variable.
/// </remarks>
internal enum BoundValue { Filter, FilterValue, Order, Take, Skip }

/// <summary>One variable a document declared, and what the chain bound to it.</summary>
/// <remarks>
/// Recorded for a caller that has to <em>write</em> the payload rather than leave it to the
/// runtime. The order is the document's numbering, which is the order the payload's properties
/// have to be written in for the two to agree.
/// </remarks>
/// <param name="Kind">What the chain bound to it.</param>
/// <param name="Name">The variable's name in the document, which is its number.</param>
/// <param name="Type">The GraphQL type it was declared as.</param>
/// <param name="Hole">
/// Which of the filter's values this variable carries, for <see cref="BoundValue.FilterValue"/>;
/// -1 for everything else.
/// </param>
internal readonly record struct DocumentBinding(BoundValue Kind, string Name, string Type, int Hole = -1);

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
    /// <param name="facts">The chain to print.</param>
    /// <param name="model">The semantic model the chain was written in.</param>
    /// <param name="token">Cancels the analysis.</param>
    /// <param name="bindings">
    /// Collects what each variable carries, in the document's own numbering, which is the order
    /// the payload's properties have to be written in.
    /// </param>
    /// <param name="filter">
    /// The filter's printed shape, when the chain has one this could print. Given, and inlinable,
    /// the filter's structure is written into the document and only its values become variables;
    /// otherwise the whole filter is one variable of the schema's filter input type.
    /// </param>
    public static string? TryWrite(
        ChainFacts facts,
        SemanticModel model,
        CancellationToken token,
        List<DocumentBinding> bindings,
        FilterSkeletonModel? filter = null)
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
        var arguments = new List<(string Name, string Value)>();

        // A variable nothing else names: declared, and referred to wherever it is wanted.
        string Declare(string type, BoundValue kind, int hole = -1)
        {
            string name = "v" + variables.Count;
            variables.Add((name, type));
            bindings.Add(new DocumentBinding(kind, name, type, hole));
            return name;
        }

        // A variable that is the whole of one argument, which is most of them.
        void Bind(string argument, string type, BoundValue kind)
            => arguments.Add((argument, "$" + Declare(type, kind)));

        // The binding order is the document's variable numbering, so it must match exactly.
        if (facts.HasFilter)
        {
            // The structure written out, with a variable for each value it compares against.
            // What a cost analyser sees is then the filter itself rather than an opaque input
            // object it has to assume the worst of.
            if (facts.InlineFilter && filter is { CanInline: true })
                arguments.Add((facts.FilterArgument, Inline(filter, Declare)));
            else
                Bind(facts.FilterArgument, facts.FilterInput ?? facts.ElementType.Name + "FilterInput",
                    BoundValue.Filter);
        }

        if (facts.HasOrdering)
            Bind(facts.OrderArgument, "[" + (facts.SortInput ?? facts.ElementType.Name + "SortInput") + "!]",
                BoundValue.Order);

        // A page whose size the compiler already knows is written into the document as the number
        // it is. A variable would carry the same number to the same server on every call, and cost
        // it something on the way: a server sizing the query ahead of running it sees `take: $v0`
        // as "up to whatever the schema allows" and has to budget for that, where `take: 1` is one
        // row and it can say so. It is the reason a filter's structure is inlined too.
        if (facts.HasTake)
        {
            string argument = facts.Paging == Paging.Cursor ? facts.FirstArgument : facts.TakeArgument;

            if (Page(facts, facts.TakeValue, model, token) is { } size)
                arguments.Add((argument, size));
            else
                Bind(argument, "Int", BoundValue.Take);
        }

        if (facts.HasSkip)
        {
            if (Known(facts.SkipValue, model, token) is { } offset)
                arguments.Add((facts.SkipArgument, offset));
            else
                Bind(facts.SkipArgument, "Int", BoundValue.Skip);
        }

        // Only a terminal asks for a last page — there is no operator that takes a size for one —
        // so it is always the compiler's own number.
        if (facts.HasLast)
            arguments.Add((facts.LastArgument, (facts.ResultPage ?? 1).ToString()));

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

                builder.Append(arguments[i].Name).Append(": ").Append(arguments[i].Value);
            }

            builder.Append(')');
        }

        builder.Append(" { ").Append(selection).Append(" } }");

        return builder.ToString();
    }

    /// <summary>
    /// The page size to write into the document, or null when only a variable can carry it.
    /// </summary>
    /// <remarks>
    /// A terminal's page is the compiler's own decision — <c>First</c> means one, <c>Single</c>
    /// means two — so it is always known. A <c>Take</c> the chain wrote is known when what it was
    /// given is: a literal, or a <c>const</c>. What it is not is a value the method was handed,
    /// which is the one case a variable is for.
    /// </remarks>
    private static string? Page(
        ChainFacts facts,
        ExpressionSyntax? take,
        SemanticModel model,
        CancellationToken token)
        => facts.ExplicitTake ? Known(take, model, token) : (facts.ResultPage ?? 1).ToString();

    /// <summary>A count the compiler can read off the syntax, as the document would spell it.</summary>
    private static string? Known(ExpressionSyntax? expression, SemanticModel model, CancellationToken token)
        => expression is not null
            && model.GetConstantValue(expression, token) is { HasValue: true, Value: int count }
                ? count.ToString()
                : null;

    /// <summary>
    /// The filter as a value in the document, with a variable declared for each of its leaves.
    /// </summary>
    /// <remarks>
    /// The holes come out of the skeleton in ascending order, so declaring them as they are met
    /// numbers the variables in the order they are written — which is the order the payload has
    /// to put them in, and the only thing keeping the document and the body agreeing.
    /// </remarks>
    private static string Inline(
        FilterSkeletonModel filter,
        Func<string, BoundValue, int, string> declare)
    {
        var text = new StringBuilder();

        foreach (var step in filter.Literal)
        {
            if (step.Hole < 0)
                text.Append(step.Json);
            else
                text.Append('$').Append(
                    declare(filter.Holes[step.Hole].Scalar!, BoundValue.FilterValue, step.Hole));
        }

        return text.ToString();
    }

    /// <summary>
    /// Turns the terminal operator into the arguments the server is asked for. Mirrors
    /// <c>ApplyResultOperator</c>; where that throws, this declines.
    /// </summary>
    private static bool ApplyResult(ChainFacts facts)
    {
        // Recorded before the terminal contributes its own, so the two can be told apart later.
        facts.ExplicitTake = facts.HasTake;

        switch (facts.Result)
        {
            case ResultKind.Sequence:
                return true;

            case ResultKind.First:
            case ResultKind.FirstOrDefault:
            case ResultKind.Any:
                facts.HasTake = true;
                facts.ResultPage = 1;
                return true;

            // Two rows: enough to return the one, and enough to prove it was not two.
            case ResultKind.Single:
            case ResultKind.SingleOrDefault:
                facts.HasTake = true;
                facts.ResultPage = 2;
                return true;

            case ResultKind.Last:
            case ResultKind.LastOrDefault:
                if (facts.Paging != Paging.Cursor)
                    return false;

                facts.HasTake = false;
                facts.HasLast = true;
                facts.ResultPage = 1;
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
