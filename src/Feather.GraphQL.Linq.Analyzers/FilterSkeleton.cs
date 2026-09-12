using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>One value the filter waits for, and the type it arrives as.</summary>
/// <param name="Type">How the value is declared where it is filled in.</param>
/// <param name="Binding">
/// The expression the value is read from, when a caller asked for the leaves to be bound at
/// compile time; null when the runtime supplies them from the expression tree.
/// </param>
internal sealed record FilterHole(string Type, string? Binding = null);

/// <summary>
/// A filter's shape, printed at compile time, with its values left as holes.
/// </summary>
/// <remarks>
/// The lowering's two halves come apart cleanly: which fields, which operations and how they
/// nest is decided by the predicate's <em>syntax</em>, while only the leaves wait for the
/// expression tree. This is the first half, emitted as the body of a <c>WriteTo</c>.
/// </remarks>
internal sealed class FilterSkeletonModel(string body, IReadOnlyList<FilterHole> holes)
{
    /// <summary>The C# that writes this filter, given fields named <c>_0</c>, <c>_1</c>, …</summary>
    public string Body { get; } = body;

    /// <summary>The values to fill, in the order the runtime will collect them.</summary>
    public IReadOnlyList<FilterHole> Holes { get; } = holes;
}

/// <summary>
/// Renders a predicate's filter shape into the code that writes it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow. It models a conjunction of comparisons on member paths — the shape most
/// predicates actually are — and declines everything else: disjunction, negation, quantifiers,
/// string operations, <c>Contains</c>. A declined predicate is not an error; the chain keeps the
/// runtime lowering it has always had, and only loses the saving.
/// </para>
/// <para>
/// The narrowness is what keeps this honest. The runtime's <c>FilterTranslator</c> normalises,
/// merges and collapses in ways that depend on the whole shape, and reproducing all of that here
/// would be a second implementation of the most delicate file in the library. What it reproduces
/// instead is the part with no such rules: distinct fields, one operation each, in source order.
/// </para>
/// </remarks>
internal static class FilterSkeleton
{
    /// <summary>Renders the chain's predicates, or null when any of them is outside the subset.</summary>
    /// <param name="predicates">The Wheres the chain applied, in the order it applied them.</param>
    /// <param name="model">The semantic model the predicates were written in.</param>
    /// <param name="token">Cancels the analysis.</param>
    /// <param name="bind">
    /// Where a comparison's value comes from, for a caller that means to fill the leaves at
    /// compile time rather than let the runtime read them out of the expression tree. Returning
    /// null declines the whole filter, which is how a caller says that a value it cannot see the
    /// origin of is not one it can bind.
    /// </param>
    public static FilterSkeletonModel? From(
        IReadOnlyList<LambdaExpressionSyntax> predicates,
        SemanticModel model,
        CancellationToken token,
        Func<ExpressionSyntax, string?>? bind = null)
    {
        if (predicates.Count == 0)
            return null;

        var clauses = new List<Clause>();

        foreach (var predicate in predicates)
        {
            if (predicate.Body is not ExpressionSyntax body
                || model.GetSymbolInfo(predicate, token).Symbol is not IMethodSymbol { Parameters.Length: 1 } lambda)
                return null;

            if (!Collect(body, lambda.Parameters[0], model, token, bind, clauses))
                return null;
        }

        return Render(clauses);
    }

    /// <summary>One comparison: a field path, an operation, and the value that goes under it.</summary>
    private sealed record Clause(
        IReadOnlyList<string> Path, string Operation, string ValueType, string? Binding);

    /// <summary>
    /// Flattens a conjunction into its comparisons, in source order.
    /// </summary>
    /// <remarks>
    /// Source order is the invariant the whole design rests on: the runtime collects the values
    /// by walking the same predicate the same way, so the Nth hole here is the Nth value there.
    /// An agreement test pins it for every predicate shape the library is tested against.
    /// </remarks>
    private static bool Collect(
        ExpressionSyntax node,
        IParameterSymbol parameter,
        SemanticModel model,
        CancellationToken token,
        Func<ExpressionSyntax, string?>? bind,
        List<Clause> clauses)
    {
        switch (node)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return Collect(parenthesized.Expression, parameter, model, token, bind, clauses);

            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression } and:
                return Collect(and.Left, parameter, model, token, bind, clauses)
                    && Collect(and.Right, parameter, model, token, bind, clauses);

            case BinaryExpressionSyntax binary when Operation(binary.Kind()) is { } operation:
                return Comparison(binary, operation, parameter, model, token, bind, clauses);

            default:
                return false;
        }
    }

    private static bool Comparison(
        BinaryExpressionSyntax binary,
        string operation,
        IParameterSymbol parameter,
        SemanticModel model,
        CancellationToken token,
        Func<ExpressionSyntax, string?>? bind,
        List<Clause> clauses)
    {
        // Only the member-on-the-left orientation. The flipped form is legal and the runtime
        // handles it; modelling it here would mean mirroring the operator too, for a shape
        // almost nobody writes.
        if (Path(binary.Left, parameter, model, token) is not { } path)
            return false;

        // The right side has to be a value, not another member of the element.
        if (Path(binary.Right, parameter, model, token) is not null)
            return false;

        if (model.GetTypeInfo(binary.Right, token).Type is not { } valueType
            || valueType.TypeKind == TypeKind.Error)
            return false;

        // An enum reaches the wire as its schema name, which is a rule the runtime owns.
        if (GraphQLTypeFacts.UnwrapNullable(valueType).TypeKind == TypeKind.Enum)
            return false;

        string? binding = null;

        if (bind is not null && (binding = bind(binary.Right)) is null)
            return false;

        clauses.Add(new Clause(path, operation, Display(valueType), binding));
        return true;
    }

    /// <summary>The field path a member chain names, or null when it is not one.</summary>
    private static IReadOnlyList<string>? Path(
        ExpressionSyntax expression,
        IParameterSymbol parameter,
        SemanticModel model,
        CancellationToken token)
    {
        var path = new List<string>();
        var current = expression;

        while (true)
        {
            switch (current)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } suppress:
                    current = suppress.Operand;
                    continue;

                case MemberAccessExpressionSyntax member:
                {
                    if (model.GetSymbolInfo(member, token).Symbol is not IPropertySymbol property
                        || GraphQLTypeFacts.IsIgnored(property))
                        return null;

                    path.Insert(0, GraphQLTypeFacts.FieldName(property));
                    current = member.Expression;
                    continue;
                }

                case IdentifierNameSyntax identifier:
                    return SymbolEqualityComparer.Default.Equals(
                            model.GetSymbolInfo(identifier, token).Symbol, parameter)
                        && path.Count > 0
                        ? path
                        : null;

                default:
                    return null;
            }
        }
    }

    /// <summary>HotChocolate's operation for a comparison, or null for anything else.</summary>
    /// <remarks>
    /// The names are the provider's, and this hard-codes the default one. A chain that names a
    /// different dialect is declined, because which names it would use is a runtime decision.
    /// </remarks>
    private static string? Operation(SyntaxKind kind)
        => kind switch
        {
            SyntaxKind.EqualsExpression => "eq",
            SyntaxKind.NotEqualsExpression => "neq",
            SyntaxKind.GreaterThanExpression => "gt",
            SyntaxKind.GreaterThanOrEqualExpression => "gte",
            SyntaxKind.LessThanExpression => "lt",
            SyntaxKind.LessThanOrEqualExpression => "lte",
            _ => null
        };

    /// <summary>
    /// Writes the clauses as nested writer calls, merging those that share a prefix.
    /// </summary>
    /// <remarks>
    /// Declines any pair that collides — the same field twice, or a path that is a prefix of
    /// another — because that is where the runtime falls back to an explicit <c>and:</c> array
    /// and reproducing the fallback is reproducing the merge.
    /// </remarks>
    private static FilterSkeletonModel? Render(List<Clause> clauses)
    {
        if (clauses.Count == 0)
            return null;

        for (int i = 0; i < clauses.Count; i++)
        {
            for (int j = i + 1; j < clauses.Count; j++)
            {
                if (Collides(clauses[i], clauses[j]))
                    return null;
            }
        }

        var body = new StringBuilder();
        var holes = new List<FilterHole>();

        // The filter object itself, which sits as the value of the variable the document declares.
        body.Append("            writer.WriteStartObject();\n");
        Write(body, clauses, prefix: [], depth: 0, holes);
        body.Append("            writer.WriteEndObject();\n");

        return new FilterSkeletonModel(body.ToString(), holes);
    }

    /// <summary>Two clauses collide when one's path is a prefix of the other's, or they match.</summary>
    private static bool Collides(Clause left, Clause right)
    {
        int shared = 0;
        while (shared < left.Path.Count && shared < right.Path.Count
            && left.Path[shared] == right.Path[shared])
        {
            shared++;
        }

        return shared == left.Path.Count || shared == right.Path.Count;
    }

    /// <summary>Emits every clause under one prefix, grouping those that descend together.</summary>
    private static void Write(
        StringBuilder body,
        List<Clause> clauses,
        List<string> prefix,
        int depth,
        List<FilterHole> holes)
    {
        string indent = new(' ', 16);
        var written = new HashSet<string>();

        foreach (var clause in clauses)
        {
            if (!StartsWith(clause.Path, prefix) || written.Contains(clause.Path[depth]))
                continue;

            string segment = clause.Path[depth];
            written.Add(segment);

            body.Append(indent).Append("writer.WritePropertyName(\"").Append(segment).Append("\");\n");

            if (clause.Path.Count == depth + 1)
            {
                body.Append(indent).Append("writer.WriteStartObject();\n")
                    .Append(indent).Append("writer.WritePropertyName(\"").Append(clause.Operation).Append("\");\n")
                    .Append(indent).Append("global::Feather.GraphQL.GraphQLVariableWriter.Write(writer, _")
                    .Append(holes.Count).Append(");\n")
                    .Append(indent).Append("writer.WriteEndObject();\n");

                holes.Add(new FilterHole(clause.ValueType, clause.Binding));
                continue;
            }

            var deeper = new List<string>(prefix) { segment };

            body.Append(indent).Append("writer.WriteStartObject();\n");
            Write(body, clauses, deeper, depth + 1, holes);
            body.Append(indent).Append("writer.WriteEndObject();\n");
        }
    }

    private static bool StartsWith(IReadOnlyList<string> path, List<string> prefix)
    {
        if (path.Count <= prefix.Count)
            return false;

        for (int i = 0; i < prefix.Count; i++)
        {
            if (path[i] != prefix[i])
                return false;
        }

        return true;
    }

    private static readonly SymbolDisplayFormat _qualified =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            & ~SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static string Display(ITypeSymbol type) => type.ToDisplayString(_qualified);
}
