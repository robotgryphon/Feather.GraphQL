using System.Collections.Generic;
using System.Text;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// Derives a selection set from a projection at compile time — the symbol-space counterpart of
/// the translator's <c>SelectionSetBuilder</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place where a second implementation carries real risk. Everywhere else a
/// disagreement between the compiler's view and the runtime's shows up as a missing optimisation;
/// here it would show up as a query asking for the wrong fields, which the server answers
/// happily. The mitigation is not care, it is a test: every chain in the corpus is translated
/// both ways and the two documents compared byte for byte.
/// </para>
/// <para>
/// Because of that, the rule here is to decline rather than guess. Anything this does not
/// recognise returns null — and says, through <see cref="Refusals"/>, which expression it was and
/// why, because a decline is now an error against the author's own projection rather than a
/// silent fall back to a translator that no longer exists.
/// </para>
/// </remarks>
internal static class SelectionSetWriter
{
    /// <summary>A field and the fields selected beneath it, in the order they were named.</summary>
    /// <remarks>
    /// Each field remembers where it was named, when it was named anywhere: the reply is modelled
    /// from this tree rather than from the projection, so a member the reader cannot fill has
    /// nothing else to point back at.
    /// </remarks>
    internal sealed class Node
    {
        private readonly Dictionary<string, Node> _children = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Location> _named = new(StringComparer.Ordinal);

        public List<string> Order { get; } = [];

        public Node Child(string name, Location? named = null)
        {
            if (named is not null && !_named.ContainsKey(name))
                _named[name] = named;

            if (_children.TryGetValue(name, out var existing))
                return existing;

            var child = new Node();
            _children[name] = child;
            Order.Add(name);
            return child;
        }

        /// <summary>Where a field was named, or null when nothing in the source named it.</summary>
        public Location? Named(string name) => _named.TryGetValue(name, out var where) ? where : null;

        public Node this[string name] => _children[name];
    }

    /// <summary>
    /// The selection set for a projection, or null when it cannot be derived here.
    /// </summary>
    public static Node? Build(
        ITypeSymbol elementType,
        LambdaExpressionSyntax? projection,
        SemanticModel model,
        CancellationToken token)
        => Build(elementType, projection, model, token, out _);

    /// <summary>
    /// The selection set for a projection, or null and the reason it could not be derived.
    /// </summary>
    /// <remarks>
    /// <c>refusal</c> is what stopped the walk, and where. Set only where null is returned — and
    /// set even when nothing in the source was at fault, since a caller reporting a decline needs
    /// something to say either way.
    /// </remarks>
    public static Node? Build(
        ITypeSymbol elementType,
        LambdaExpressionSyntax? projection,
        SemanticModel model,
        CancellationToken token,
        out Refusal? refusal)
    {
        var refusals = new Refusals();
        var root = new Node();

        bool built = projection is null
            ? CollectScalars(elementType, root, null)
            : Collect(projection.Body, Parameter(projection), root, model, token, refusals);

        if (built && root.Order.Count > 0)
        {
            refusal = null;
            return root;
        }

        if (built && projection is not null)
        {
            refusals.Note(projection,
                $"its Select names no field of '{elementType.Name}', so there is nothing to ask the server for");
        }

        refusal = refusals.First
            ?? new Refusal($"'{elementType.Name}' has no scalar fields of its own to select", null);

        return null;
    }

    /// <summary>Writes a built selection set in the printer's canonical form.</summary>
    public static void Print(StringBuilder builder, Node node)
    {
        for (int i = 0; i < node.Order.Count; i++)
        {
            if (i > 0)
                builder.Append(' ');

            string name = node.Order[i];
            builder.Append(name);

            var child = node[name];
            if (child.Order.Count == 0)
                continue;

            builder.Append(" { ");
            Print(builder, child);
            builder.Append(" }");
        }
    }

    private static string? Parameter(LambdaExpressionSyntax lambda)
        => lambda switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized
                => parenthesized.ParameterList.Parameters[0].Identifier.ValueText,
            _ => null
        };

    /// <summary>
    /// The automatic selection: a type's own leaf fields, and nothing else. A type with none is
    /// <c>FGQL014</c> at runtime, and nothing to emit here.
    /// </summary>
    /// <remarks>
    /// <paramref name="named"/> is where the member being expanded was written, which every field
    /// this adds inherits — nothing named them individually, so that is the closest the source
    /// comes to saying where they were asked for.
    /// </remarks>
    private static bool CollectScalars(ITypeSymbol type, Node target, Location? named)
    {
        foreach (var property in GraphQLTypeFacts.Fields(type))
        {
            if (GraphQLTypeFacts.IsLeaf(property.Type))
                target.Child(GraphQLTypeFacts.FieldName(property), named);
        }

        return target.Order.Count > 0;
    }

    private static bool Collect(
        SyntaxNode node,
        string? parameter,
        Node target,
        SemanticModel model,
        CancellationToken token,
        Refusals refusals)
    {
        switch (node)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return Collect(parenthesized.Expression, parameter, target, model, token, refusals);

            case CastExpressionSyntax cast:
                return Collect(cast.Expression, parameter, target, model, token, refusals);

            case AnonymousObjectCreationExpressionSyntax anonymous:
                foreach (var initializer in anonymous.Initializers)
                {
                    if (!Collect(initializer.Expression, parameter, target, model, token, refusals))
                        return false;
                }

                return true;

            case ObjectCreationExpressionSyntax creation:
            {
                foreach (var argument in creation.ArgumentList?.Arguments ?? default)
                {
                    if (!Collect(argument.Expression, parameter, target, model, token, refusals))
                        return false;
                }

                foreach (var expression in creation.Initializer?.Expressions ?? default)
                {
                    // Only `Member = value`; a collection initializer projects nothing nameable.
                    if (expression is not AssignmentExpressionSyntax assignment)
                    {
                        return refusals.No(expression,
                            "a collection initializer inside the Select names no member, so there is "
                            + "nothing to trace a field through — assign the values to named members");
                    }

                    if (!Collect(assignment.Right, parameter, target, model, token, refusals))
                        return false;
                }

                return true;
            }

            // A chain of LINQ operators over a collection member, all of which run client-side:
            // what they change is the shape of the result, never which fields to request.
            case InvocationExpressionSyntax invocation when IsLinqOperator(invocation, model, token):
            {
                var nested = CollectSequence(invocation, parameter, target, model, token, refusals);

                // Expanded from what the chain reads rather than from what it produces: a chain
                // that named no field at all still needs the member it ran over to carry a
                // selection set, and the scalars of whatever it turned that member into would be
                // fields of a type the node does not stand for.
                var origin = Origin(invocation);

                return nested is not null
                    && Expand(TypeOf(origin, model, token), nested, origin, refusals);
            }

            // Any other call — a method of the caller's own, an extension over what a member
            // holds — runs client-side too, over the values it is handed. Those are named here,
            // so they are collected and the call itself is where the tracing stops.
            case InvocationExpressionSyntax invocation:
                return CollectCall(invocation, parameter, target, model, token, refusals, out _);

            case MemberAccessExpressionSyntax member:
            {
                var reached = Descend(member, parameter, target, model, token, refusals);

                return reached is not null
                    && Expand(TypeOf(member, model, token), reached, member, refusals);
            }

            case IdentifierNameSyntax identifier when identifier.Identifier.ValueText == parameter:
            {
                var type = TypeOf(identifier, model, token)!;

                return CollectScalars(type, target, identifier.GetLocation())
                    || refusals.No(identifier,
                        $"'{type.Name}' has no scalar fields of its own, and a selection set cannot be "
                        + "empty — say what to take from it with a nested Select");
            }

            // Anything else built out of what the element holds — a comparison, a concatenation,
            // a conditional, an index — asks for the fields its parts name and for nothing
            // besides, since what it does with them it does client-side.
            default:
                return Parts(node, parameter, target, model, token, refusals);
        }
    }

    /// <summary>
    /// Collects what the parts of an expression read, for an expression that is nothing but its
    /// parts.
    /// </summary>
    /// <remarks>
    /// A part that never names the projection's parameter cannot read the element, so it asks for
    /// nothing; one that does has to be an expression this understands outright. That is the same
    /// bargain the cases above strike, applied to the operators between them rather than to a
    /// list of the ones that were thought of.
    /// </remarks>
    private static bool Parts(
        SyntaxNode node,
        string? parameter,
        Node target,
        SemanticModel model,
        CancellationToken token,
        Refusals refusals)
    {
        foreach (var child in node.ChildNodes())
        {
            if (!Mentions(child, parameter))
                continue;

            bool collected = child is ExpressionSyntax expression
                ? Collect(expression, parameter, target, model, token, refusals)
                // An argument list, an initializer: not an expression itself, but made of them.
                : Parts(child, parameter, target, model, token, refusals);

            if (!collected)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Walks a client-side operator chain over a collection member back to the member it reads,
    /// collecting whatever its lambdas name along the way.
    /// </summary>
    private static Node? CollectSequence(
        SyntaxNode node,
        string? parameter,
        Node target,
        SemanticModel model,
        CancellationToken token,
        Refusals refusals)
    {
        switch (node)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return CollectSequence(parenthesized.Expression, parameter, target, model, token, refusals);

            case CastExpressionSyntax cast:
                return CollectSequence(cast.Expression, parameter, target, model, token, refusals);

            case InvocationExpressionSyntax invocation when IsLinqOperator(invocation, model, token):
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax access)
                {
                    return refusals.Nothing<Node>(invocation,
                        "a LINQ operator called as a plain static method cannot be traced back to the "
                        + "member it reads — call it on the member instead");
                }

                var nested = CollectSequence(access.Expression, parameter, target, model, token, refusals);
                if (nested is null)
                    return null;

                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    if (argument.Expression is not LambdaExpressionSyntax lambda)
                        continue;

                    if (!Collect(lambda.Body, Parameter(lambda), nested, model, token, refusals))
                        return null;
                }

                return nested;
            }

            // A call this does not know is still something the operators in front of it read
            // from, so what it was handed is what they run over.
            case InvocationExpressionSyntax invocation:
                return CollectCall(invocation, parameter, target, model, token, refusals, out var source)
                    ? source
                    : null;

            case MemberAccessExpressionSyntax member:
                return Descend(member, parameter, target, model, token, refusals);

            case IdentifierNameSyntax identifier when identifier.Identifier.ValueText == parameter:
                return target;

            default:
                return refusals.Nothing<Node>(node,
                    $"'{Brief(node)}' is not something a projection can be traced through — a field has "
                    + "to be reached from the row the Select was given");
        }
    }

    /// <summary>
    /// Collects what a call outside <c>System.Linq</c> reads, and stops there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whatever the method does, it does client-side and over the values it is handed — and those
    /// are named where the call is written, as its receiver and its arguments. So each of them is
    /// collected the way any other expression is, and what the method makes of them afterwards
    /// changes the shape of the answer rather than the document. That is what lets a projection
    /// end in a method of the caller's own: the fields are traceable even when the method is not.
    /// </para>
    /// <para>
    /// What it is handed is selected whole, because this cannot see which of it the method reads
    /// and a field left unrequested would arrive empty rather than missing. Whole means that
    /// member's own scalars, the same answer naming a member without projecting gets — and only
    /// when the chain in front of the call did not change what the sequence holds, since one that
    /// did has already said what it wants and anything more would be fields of a type the
    /// selection does not stand for.
    /// </para>
    /// <para>
    /// <paramref name="source"/> is the node the receiver landed on, so an operator further out
    /// can go on collecting into it. It is null when the call reads nothing through a receiver —
    /// a static method, or an extension called as one — which is not a refusal.
    /// </para>
    /// </remarks>
    private static bool CollectCall(
        InvocationExpressionSyntax invocation,
        string? parameter,
        Node target,
        SemanticModel model,
        CancellationToken token,
        Refusals refusals,
        out Node? source)
    {
        source = null;

        var receiver = invocation.Expression is MemberAccessExpressionSyntax access
            ? access.Expression
            : null;

        // Called some other way than through a receiver or by name — through a delegate the
        // element holds, say — which is a call this cannot follow.
        if (receiver is null && Mentions(invocation.Expression, parameter))
        {
            return refusals.No(invocation.Expression,
                $"'{Brief(invocation.Expression)}' is called through a value the row holds, which is not "
                + "a call this can trace a field through");
        }

        if (receiver is not null && Mentions(receiver, parameter))
        {
            source = CollectSequence(receiver, parameter, target, model, token, refusals);

            if (source is null)
                return false;

            var beginning = Origin(receiver);
            var origin = TypeOf(beginning, model, token);
            var handed = TypeOf(receiver, model, token);

            if (origin is not null
                && handed is not null
                && !GraphQLTypeFacts.IsLeaf(origin)
                && SymbolEqualityComparer.Default.Equals(
                    GraphQLTypeFacts.Unwrap(origin), GraphQLTypeFacts.Unwrap(handed)))
            {
                CollectScalars(GraphQLTypeFacts.Unwrap(origin), source, beginning.GetLocation());
            }

            if (!Expand(origin, source, beginning, refusals))
                return false;
        }

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (!CollectArgument(
                argument.Expression, invocation, receiver, parameter, target, source, model, token, refusals))
                return false;
        }

        return true;
    }

    /// <summary>
    /// What one argument of such a call reads.
    /// </summary>
    /// <remarks>
    /// An expression that never names the projection's parameter cannot read the element and is
    /// skipped; one that does is collected where it is rooted. A lambda is the exception, because
    /// it names the parameter of its own: it ranges over what the receiver holds, exactly as one
    /// given to a LINQ operator does, and is collected onto the receiver's node — but only when
    /// its parameter is that element, since a lambda over anything else names members this could
    /// not place.
    /// </remarks>
    private static bool CollectArgument(
        ExpressionSyntax argument,
        InvocationExpressionSyntax call,
        ExpressionSyntax? receiver,
        string? parameter,
        Node target,
        Node? source,
        SemanticModel model,
        CancellationToken token,
        Refusals refusals)
    {
        if (argument is not LambdaExpressionSyntax lambda)
        {
            return !Mentions(argument, parameter)
                || Collect(argument, parameter, target, model, token, refusals);
        }

        var handed = receiver is null ? null : TypeOf(receiver, model, token);
        var over = handed is null ? null : GraphQLTypeFacts.Unwrap(handed);

        // Nothing under it to select — a sequence of scalars, or a call that reads nothing
        // through a receiver at all — so the lambda names no field of the graph, unless it
        // reaches back out to the element, which is a read this could not place.
        if (source is null || over is null || GraphQLTypeFacts.IsScalar(over))
        {
            return !Mentions(lambda, parameter)
                || refusals.No(lambda,
                    $"the lambda given to '{Called(call)}' reads the row from outside the values that call "
                    + "was handed, which is not a read this can place in the selection set");
        }

        if (model.GetSymbolInfo(lambda, token).Symbol is not IMethodSymbol { Parameters.Length: 1 } written
            || !SymbolEqualityComparer.Default.Equals(written.Parameters[0].Type, over))
        {
            return refusals.No(lambda,
                $"the lambda given to '{Called(call)}' does not range over the '{over.Name}' values its "
                + "receiver holds, so the members it names cannot be placed in the selection set");
        }

        return Collect(lambda.Body, Parameter(lambda), source, model, token, refusals);
    }

    /// <summary>The expression a client-side chain reads from, which its operators run over.</summary>
    private static ExpressionSyntax Origin(ExpressionSyntax expression)
        => expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => Origin(parenthesized.Expression),
            CastExpressionSyntax cast => Origin(cast.Expression),
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
                => Origin(access.Expression),
            _ => expression
        };

    /// <summary>
    /// Whether an expression names the projection's parameter anywhere inside it.
    /// </summary>
    /// <remarks>
    /// By name, as everything else here matches it, and deliberately in the direction that costs
    /// a decline rather than a wrong document: a projection whose parameter cannot be named at
    /// all is treated as named everywhere, so every expression has to be understood outright.
    /// </remarks>
    private static bool Mentions(SyntaxNode node, string? parameter)
    {
        if (parameter is null)
            return true;

        foreach (var name in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (name.Identifier.ValueText == parameter)
                return true;
        }

        return false;
    }

    /// <summary>
    /// A GraphQL object field must carry a selection set, so naming one without saying what to
    /// take from it selects its scalars — that member's own, and no further.
    /// </summary>
    private static bool Expand(ITypeSymbol? memberType, Node node, SyntaxNode where, Refusals refusals)
    {
        if (node.Order.Count > 0)
            return true;

        if (memberType is null)
        {
            return refusals.No(where,
                $"'{Brief(where)}' has no type the compiler could read, so what to select from it is not "
                + "knowable here");
        }

        if (GraphQLTypeFacts.IsLeaf(memberType))
            return true;

        var unwrapped = GraphQLTypeFacts.UnwrapNullable(memberType);
        var element = GraphQLTypeFacts.ElementType(unwrapped) ?? unwrapped;

        return CollectScalars(element, node, where.GetLocation())
            || refusals.No(where,
                $"'{Brief(where)}' selects nothing: '{element.Name}' has no scalar fields of its own, and "
                + "a GraphQL selection set cannot be empty — say what to take from it with a nested Select");
    }

    /// <summary>Walks a member chain onto the selection tree, returning the node it lands on.</summary>
    private static Node? Descend(
        SyntaxNode expression,
        string? parameter,
        Node target,
        SemanticModel model,
        CancellationToken token,
        Refusals refusals)
    {
        var path = new List<(string Name, Location Where)>();
        var current = expression;

        while (true)
        {
            switch (current)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case CastExpressionSyntax cast:
                    current = cast.Expression;
                    continue;

                case MemberAccessExpressionSyntax member:
                {
                    if (model.GetSymbolInfo(member, token).Symbol is not IPropertySymbol property)
                    {
                        return refusals.Nothing<Node>(member.Name,
                            $"'{member.Name}' is not a property, and only a property is a field the query "
                            + "can ask the server for");
                    }

                    if (GraphQLTypeFacts.IsIgnored(property))
                    {
                        return refusals.Nothing<Node>(member.Name,
                            $"'{property.Name}' is marked [JsonIgnore], so it is not a field the query can "
                            + "ask the server for");
                    }

                    path.Insert(0, (GraphQLTypeFacts.FieldName(property), member.Name.GetLocation()));
                    current = member.Expression;
                    continue;
                }

                case IdentifierNameSyntax identifier when identifier.Identifier.ValueText == parameter:
                {
                    var node = target;
                    foreach (var (name, where) in path)
                        node = node.Child(name, where);

                    return node;
                }

                // A member named on its own has to be the row's, because naming it is what puts a
                // field in the document. One that reads the row through something else — a
                // lambda's own parameter from further out — is a read this cannot place; one that
                // does not read the row at all is a value from somewhere the document has no way
                // to ask for, and a parameter is how such a value reaches a compiled query.
                default:
                    return refusals.Nothing<Node>(expression, Mentions(expression, parameter)
                        ? $"'{Brief(expression)}' is not read from the row the Select was given, and only "
                            + "what is read from the row can be asked of the server"
                        : $"'{Brief(expression)}' is named on its own in the Select without reading the row, "
                            + "and a member named there is what puts a field in the document — take the "
                            + "value as a parameter of the query method instead");
            }
        }
    }

    private static bool IsLinqOperator(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken token)
        => model.GetSymbolInfo(invocation, token).Symbol is IMethodSymbol method
            && method.ContainingType?.ToDisplayString() is "System.Linq.Queryable" or "System.Linq.Enumerable";

    private static ITypeSymbol? TypeOf(SyntaxNode node, SemanticModel model, CancellationToken token)
        => model.GetTypeInfo(node, token).Type;

    /// <summary>What a call is called, for a message that has to name it.</summary>
    private static string Called(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.ToString(),
            _ => Brief(invocation.Expression)
        };

    /// <summary>
    /// An expression as a message can quote it.
    /// </summary>
    /// <remarks>
    /// The diagnostic points at the expression, so the message does not have to carry all of it —
    /// a long one is cut rather than wrapped across the build log.
    /// </remarks>
    private static string Brief(SyntaxNode node)
    {
        string written = string.Join(" ", node.ToString().Split(
            ['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));

        return written.Length <= 48 ? written : written.Substring(0, 45) + "...";
    }
}
