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
/// <param name="Scalar">
/// What the value is called in the schema, for a caller declaring a variable of its own for it;
/// null when <see cref="GraphQLTypeFacts.ScalarName"/> would be guessing.
/// </param>
internal sealed record FilterHole(string Type, string? Binding = null, string? Scalar = null);

/// <summary>
/// A filter's shape, printed at compile time, with its values left as holes.
/// </summary>
/// <remarks>
/// The lowering's two halves come apart cleanly: which fields, which operations and how they
/// nest is decided by the predicate's <em>syntax</em>, while only the leaves wait for the
/// expression tree. This is the first half, emitted as the body of a <c>WriteTo</c>.
/// </remarks>
internal sealed class FilterSkeletonModel(
    IReadOnlyList<FilterStep> steps,
    IReadOnlyList<FilterStep> literal,
    IReadOnlyList<FilterHole> holes)
{
    /// <summary>The filter as constant JSON and the places values go.</summary>
    /// <remarks>
    /// One rendering, for both the generators that use it. There were two while the precompiled
    /// plan still handed its values to a <c>Utf8JsonWriter</c> someone else owned; now that it
    /// writes its own bytes like everything else, the shape is written out one way.
    /// </remarks>
    public IReadOnlyList<FilterStep> Steps { get; } = steps;

    /// <summary>
    /// The same filter as a GraphQL value, for writing into the document instead of into a
    /// variable.
    /// </summary>
    /// <remarks>
    /// Not JSON: a GraphQL object literal leaves its field names unquoted, and a hole here stands
    /// for a reference to the variable that value was given rather than for the value itself. The
    /// hole numbering is <see cref="Steps"/>'s, so the two renderings agree about which value is
    /// which.
    /// </remarks>
    public IReadOnlyList<FilterStep> Literal { get; } = literal;

    /// <summary>The values to fill, in the order the runtime will collect them.</summary>
    public IReadOnlyList<FilterHole> Holes { get; } = holes;

    /// <summary>
    /// Whether this filter can be written into the document rather than passed whole.
    /// </summary>
    /// <remarks>
    /// Every value has to be able to name its own type, because inlining the structure means
    /// declaring a variable per value and a variable says what it is. One that cannot leaves the
    /// whole filter as it was: a partly inlined filter would need both forms at once.
    /// </remarks>
    public bool CanInline
    {
        get
        {
            if (Literal.Count == 0 || Holes.Count == 0)
                return false;

            foreach (var hole in Holes)
            {
                if (hole.Scalar is null)
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Whether two call sites printed the same filter.
    /// </summary>
    /// <remarks>
    /// Compared by shape rather than by the code that renders it, which is the same question
    /// asked of the thing that answers it: two chains agree when they filter the same fields with
    /// the same operations in the same order, whatever their values turn out to be.
    /// </remarks>
    public bool SameShapeAs(FilterSkeletonModel other)
    {
        if (Steps.Count != other.Steps.Count)
            return false;

        for (int i = 0; i < Steps.Count; i++)
        {
            if (Steps[i] != other.Steps[i])
                return false;
        }

        return true;
    }
}

/// <summary>One run of a filter: constant JSON, or the hole a value fills.</summary>
/// <param name="Json">The constant, when this is one.</param>
/// <param name="Hole">Which value goes here, or -1 for a constant.</param>
internal readonly record struct FilterStep(string Json, int Hole);

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
    /// <param name="Path">The field path the comparison names, from the filter's root.</param>
    /// <param name="Operation">The provider's name for the comparison — <c>eq</c> and the rest.</param>
    /// <param name="ValueType">How the value is declared where it is filled in.</param>
    /// <param name="Binding">The expression the value is read from, when a caller asked for one.</param>
    /// <param name="Scalar">
    /// What the schema calls the value, or null when nothing can be said for certain — which is
    /// what keeps the filter out of the document and in a variable of its own.
    /// </param>
    private sealed record Clause(
        IReadOnlyList<string> Path, string Operation, string ValueType, string? Binding, string? Scalar);

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

        clauses.Add(new Clause(
            path, operation, Display(valueType), binding, GraphQLTypeFacts.ScalarName(valueType)));

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

        var steps = new StepBuilder();
        var literal = new StepBuilder();
        var holes = new List<FilterHole>();

        // The filter object itself: the value of the variable the document declares, or — when
        // every value can name its own type — the value written into the document in its place.
        steps.Const("{");
        literal.Const("{ ");

        Write(steps, literal, clauses, prefix: [], depth: 0, holes);

        steps.Const("}");
        literal.Const(" }");

        return new FilterSkeletonModel(steps.Steps, literal.Steps, holes);
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
    /// <remarks>
    /// Both renderings are built in the one walk, because they have to describe the same filter
    /// and the surest way to keep them agreeing is for the same loop to write both. They differ
    /// only in how a name and a value are spelled: JSON quotes its field names, a GraphQL literal
    /// does not, and where JSON leaves a hole for the value the literal leaves one for the
    /// variable that carries it.
    /// </remarks>
    private static void Write(
        StepBuilder steps,
        StepBuilder literal,
        List<Clause> clauses,
        List<string> prefix,
        int depth,
        List<FilterHole> holes)
    {
        var written = new HashSet<string>();

        foreach (var clause in clauses)
        {
            if (!StartsWith(clause.Path, prefix) || written.Contains(clause.Path[depth]))
                continue;

            string segment = clause.Path[depth];

            // A writer puts its own commas in; bytes do not, so this level separates its members.
            steps.Separated(written.Count);
            literal.Separated(written.Count, ", ");
            written.Add(segment);

            steps.Property(segment);
            literal.Name(segment);

            if (clause.Path.Count == depth + 1)
            {
                steps.Const("{");
                steps.Property(clause.Operation);
                steps.Hole(holes.Count);
                steps.Const("}");

                literal.Const("{ ");
                literal.Name(clause.Operation);
                literal.Hole(holes.Count);
                literal.Const(" }");

                // A name that has to be quoted cannot go in the document, whatever its value's
                // type is, because the literal would not parse. Declining it here declines the
                // inlining for the whole filter, which is what CanInline reads off a null scalar.
                holes.Add(new FilterHole(
                    clause.ValueType,
                    clause.Binding,
                    Writable(clause) ? clause.Scalar : null));

                continue;
            }

            var deeper = new List<string>(prefix) { segment };

            steps.Const("{");
            literal.Const("{ ");

            Write(steps, literal, clauses, deeper, depth + 1, holes);

            steps.Const("}");
            literal.Const(" }");
        }
    }

    /// <summary>Collects a filter's runs, merging constants as they arrive.</summary>
    private sealed class StepBuilder
    {
        private readonly StringBuilder _constant = new();
        private readonly List<FilterStep> _steps = [];

        public IReadOnlyList<FilterStep> Steps
        {
            get
            {
                Flush();

                return _steps;
            }
        }

        public void Const(string json) => _constant.Append(json);

        public void Separated(int written, string separator = ",")
        {
            if (written > 0)
                _constant.Append(separator);
        }

        public void Property(string name)
        {
            _constant.Append('"');
            BodyPlan.Json(_constant, name);
            _constant.Append("\":");
        }

        /// <summary>A GraphQL field name, which carries no quotes and so escapes nothing.</summary>
        /// <remarks>
        /// Only ever called for a name <see cref="GraphQLTypeFacts.IsGraphQLName"/> has passed,
        /// which is what makes having nothing to escape true rather than hoped for.
        /// </remarks>
        public void Name(string name) => _constant.Append(name).Append(": ");

        public void Hole(int index)
        {
            Flush();
            _steps.Add(new FilterStep("", index));
        }

        private void Flush()
        {
            if (_constant.Length == 0)
                return;

            _steps.Add(new FilterStep(_constant.ToString(), -1));
            _constant.Clear();
        }
    }

    /// <summary>Whether every name this clause writes is one a document can carry unquoted.</summary>
    private static bool Writable(Clause clause)
    {
        foreach (string segment in clause.Path)
        {
            if (!GraphQLTypeFacts.IsGraphQLName(segment))
                return false;
        }

        return GraphQLTypeFacts.IsGraphQLName(clause.Operation);
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
