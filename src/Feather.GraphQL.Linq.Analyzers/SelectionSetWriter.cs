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
/// <para>
/// What it walks is a <see cref="Scope"/> rather than a single parameter. A projection nests:
/// <c>c => new(c.Name, c.Permissions.Select(p => p.Code + c.Code))</c> has two rows in scope
/// inside the inner lambda, and a read belongs to whichever one it starts at. Tracking only the
/// innermost is why <c>c.Code</c> there was once dropped from the document and then read from a
/// row that had no such field — a wrong document and generated code that did not compile, from a
/// projection that is ordinary C#.
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
    /// Where a read landed: in the selection set, outside the graph, or nowhere at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three are different answers and were once two. <em>In the selection set</em> is a read
    /// of a row, and the fields it names are asked of the server. <em>Outside</em> is a read of
    /// something that is not a row — a constant, a value the caller passed in, a static of
    /// somebody else's — which asks for nothing and is copied into the shaping unchanged.
    /// <em>Nowhere</em> is a refusal.
    /// </para>
    /// <para>
    /// <see cref="Element"/> is the type whose fields <see cref="Node"/> stands for, which an
    /// operator chain needs in order to tell a lambda that still ranges over those rows from one
    /// that ranges over what a projection turned them into.
    /// </para>
    /// </remarks>
    private readonly record struct Reach(Node? Node, ITypeSymbol? Element, bool Outside)
    {
        /// <summary>The walk could not place this read.</summary>
        public static readonly Reach No = new(null, null, false);

        /// <summary>The read is not of the graph at all, so it asks the server for nothing.</summary>
        public static readonly Reach None = new(null, null, true);

        public static Reach At(Node node, ITypeSymbol? element) => new(node, element, false);

        public bool Declined => Node is null && !Outside;
    }

    /// <summary>
    /// The rows a projection has in scope, innermost first.
    /// </summary>
    /// <remarks>
    /// One entry per lambda parameter between the projection and the expression being read, each
    /// with the <see cref="Reach"/> its members are collected onto. A name that is in none of them
    /// is not a row — it is the caller's own value, and reads of it ask for nothing.
    /// </remarks>
    private sealed class Scope
    {
        private readonly Scope? _outer;
        private readonly string? _name;
        private readonly Reach _reach;

        private Scope(string? name, Reach reach, Scope? outer)
        {
            _name = name;
            _reach = reach;
            _outer = outer;
        }

        public static Scope Of(string? name, Node target, ITypeSymbol element)
            => new(name, Reach.At(target, element), null);

        /// <summary>The same scope with one more row in it, for the body of a nested lambda.</summary>
        public Scope With(string? name, Reach reach) => new(name, reach, this);

        /// <summary>
        /// Whether the scope holds a parameter this could not name.
        /// </summary>
        /// <remarks>
        /// The load-bearing flag. A name that is in no scope is ordinarily a value of the
        /// caller's own, which asks the server for nothing — but where a parameter could not be
        /// named, a read of that parameter looks exactly the same, and calling it a value of the
        /// caller's own would drop a field the shaping then reads off a row that has not got it.
        /// So where anything is opaque, a name in no scope is a refusal instead.
        /// </remarks>
        public bool Opaque
        {
            get
            {
                for (var scope = this; scope is not null; scope = scope._outer)
                {
                    if (scope._name is null)
                        return true;
                }

                return false;
            }
        }

        /// <summary>What a name in scope reads from, or null when it is not one of the rows.</summary>
        public Reach? Find(string name)
        {
            for (var scope = this; scope is not null; scope = scope._outer)
            {
                if (scope._name == name)
                    return scope._reach;
            }

            return null;
        }

        /// <summary>
        /// Whether an expression names any row in scope.
        /// </summary>
        /// <remarks>
        /// By name, as everything else here matches them, and deliberately in the direction that
        /// costs a decline rather than a wrong document: a lambda whose parameter cannot be named
        /// at all makes every expression count as naming it, so every expression has to be
        /// understood outright.
        /// </remarks>
        public bool Mentions(SyntaxNode node)
        {
            if (Opaque)
                return true;

            foreach (var name in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (Find(name.Identifier.ValueText) is not null)
                    return true;
            }

            return false;
        }
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
            : Collect(
                projection.Body, Scope.Of(Parameter(projection), root, elementType), model, token, refusals);

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
            if (GraphQLTypeFacts.IsLeaf(property))
                target.Child(GraphQLTypeFacts.FieldName(property), named);
        }

        return target.Order.Count > 0;
    }

    private static bool Collect(
        SyntaxNode node,
        Scope scope,
        SemanticModel model,
        CancellationToken token,
        Refusals refusals)
    {
        // An expression that names no row in scope cannot read one, so it asks the server for
        // nothing: a constant, a value the method was handed, a static of somebody else's. It is
        // still copied into the shaping, where it goes on meaning what it means here — which is
        // the whole of what a projection mixing outside data in needs.
        if (!scope.Mentions(node))
            return true;

        switch (node)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return Collect(parenthesized.Expression, scope, model, token, refusals);

            case CastExpressionSyntax cast:
                return Collect(cast.Expression, scope, model, token, refusals);

            case AnonymousObjectCreationExpressionSyntax anonymous:
                foreach (var initializer in anonymous.Initializers)
                {
                    if (!Collect(initializer.Expression, scope, model, token, refusals))
                        return false;
                }

                return true;

            case ObjectCreationExpressionSyntax creation:
            {
                foreach (var argument in creation.ArgumentList?.Arguments ?? default)
                {
                    if (!Collect(argument.Expression, scope, model, token, refusals))
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

                    if (!Collect(assignment.Right, scope, model, token, refusals))
                        return false;
                }

                return true;
            }

            // A chain of LINQ operators over a collection member, all of which run client-side:
            // what they change is the shape of the result, never which fields to request.
            case InvocationExpressionSyntax invocation when IsLinqOperator(invocation, model, token):
            {
                var reach = CollectSequence(invocation, scope, model, token, refusals);

                if (reach.Declined)
                    return false;

                if (reach.Outside)
                    return true;

                // Expanded by what the node stands for rather than by what the chain started
                // from: a chain that named no field at all still needs the member it ran over to
                // carry a selection set, and the fields of whatever the chain turned those rows
                // into are fields the node does not stand for. The two are the same member until
                // an operator flattens, and then they are not.
                return Expand(reach.Element, reach.Node!, Origin(invocation), refusals);
            }

            // Any other call — a method of the caller's own, an extension over what a member
            // holds — runs client-side too, over the values it is handed. Those are named here,
            // so they are collected and the call itself is where the tracing stops.
            case InvocationExpressionSyntax invocation:
                return CollectCall(invocation, scope, model, token, refusals, out _);

            case MemberAccessExpressionSyntax member:
            {
                var reach = Descend(member, scope, model, token, refusals);

                if (reach.Declined)
                    return false;

                return reach.Outside
                    || Expand(TypeOf(member, model, token), reach.Node!, member, refusals);
            }

            // A row named where a value is wanted: its own scalars, as naming an object member
            // does. One of the caller's own values named the same way asks for nothing.
            case IdentifierNameSyntax identifier when scope.Find(identifier.Identifier.ValueText) is { } reach:
            {
                if (reach.Outside)
                    return true;

                if (reach.Node is null)
                    return refusals.No(identifier, Unplaceable(identifier));

                var type = TypeOf(identifier, model, token)!;

                return CollectScalars(type, reach.Node, identifier.GetLocation())
                    || refusals.No(identifier,
                        $"'{type.Name}' has no scalar fields of its own, and a selection set cannot be "
                        + "empty — say what to take from it with a nested Select");
            }

            // Anything else built out of what the rows hold — a comparison, a concatenation, a
            // conditional, an index — asks for the fields its parts name and for nothing besides,
            // since what it does with them it does client-side.
            default:
                return Parts(node, scope, model, token, refusals);
        }
    }

    /// <summary>
    /// Collects what the parts of an expression read, for an expression that is nothing but its
    /// parts.
    /// </summary>
    /// <remarks>
    /// A part that names no row in scope cannot read one, so it asks for nothing; one that does
    /// has to be an expression this understands outright. That is the same bargain the cases above
    /// strike, applied to the operators between them rather than to a list of the ones that were
    /// thought of.
    /// </remarks>
    private static bool Parts(
        SyntaxNode node,
        Scope scope,
        SemanticModel model,
        CancellationToken token,
        Refusals refusals)
    {
        foreach (var child in node.ChildNodes())
        {
            if (!scope.Mentions(child))
                continue;

            bool collected = child is ExpressionSyntax expression
                ? Collect(expression, scope, model, token, refusals)
                // An argument list, an initializer: not an expression itself, but made of them.
                : Parts(child, scope, model, token, refusals);

            if (!collected)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Walks a client-side operator chain back to what it reads, collecting whatever its lambdas
    /// name along the way.
    /// </summary>
    /// <remarks>
    /// What it reads may be a member of a row, and may equally be a collection of the caller's
    /// own — a chain filtering a list against a field of the row is both at once. The first is
    /// where the operators' lambdas collect; the second asks for nothing, and its lambdas may
    /// still read a row from further out.
    /// </remarks>
    private static Reach CollectSequence(
        SyntaxNode node,
        Scope scope,
        SemanticModel model,
        CancellationToken token,
        Refusals refusals)
    {
        switch (node)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return CollectSequence(parenthesized.Expression, scope, model, token, refusals);

            case CastExpressionSyntax cast:
                return CollectSequence(cast.Expression, scope, model, token, refusals);

            case InvocationExpressionSyntax invocation when IsLinqOperator(invocation, model, token):
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax access)
                {
                    return Cannot(refusals, invocation,
                        "a LINQ operator called as a plain static method cannot be traced back to the "
                        + "member it reads — call it on the member instead");
                }

                var nested = CollectSequence(access.Expression, scope, model, token, refusals);

                if (nested.Declined)
                    return Reach.No;

                // A flattening operator hands back what its selector returned rather than what it
                // ran over, so the operators after it name fields of that — which is somewhere
                // else in the document. Following the selector to where it lands is what keeps
                // those fields from being asked for one level too high.
                bool flattens = access.Name.Identifier.ValueText == "SelectMany";
                var produced = Reach.None;

                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    if (argument.Expression is not LambdaExpressionSyntax lambda)
                        continue;

                    var inner = Inside(lambda, scope, nested, produced, model, token);

                    if (flattens && produced.Outside && produced.Node is null)
                    {
                        produced = CollectSequence(lambda.Body, inner, model, token, refusals);

                        if (produced.Declined)
                            return Reach.No;

                        continue;
                    }

                    if (!Collect(lambda.Body, inner, model, token, refusals))
                        return Reach.No;
                }

                if (produced.Node is null)
                    return nested;

                // The member the chain ran over still has to carry a selection set of its own,
                // whether or not the selector named anything on it — the flattened rows hang
                // below it, and a field with an empty one is not a document a server accepts.
                if (nested.Node is not null
                    && !Expand(nested.Element, nested.Node, access.Expression, refusals))
                    return Reach.No;

                // What the operators after this run over is what the selector reached, which is
                // somewhere else in the document than what it ran over.
                return produced;
            }

            // A call this does not know is still something the operators in front of it read
            // from, so what it was handed is what they run over.
            case InvocationExpressionSyntax invocation:
                return CollectCall(invocation, scope, model, token, refusals, out var source) ? source : Reach.No;

            case MemberAccessExpressionSyntax member:
                return Descend(member, scope, model, token, refusals);

            // A row, or a collection of the caller's own held in a parameter — told apart by
            // whether the name is one of the rows in scope, which is a distinction only worth
            // drawing while every row in scope can be named.
            case IdentifierNameSyntax identifier:
            {
                if (scope.Find(identifier.Identifier.ValueText) is { } named)
                    return named;

                return scope.Opaque ? Cannot(refusals, identifier, Opaquely(identifier)) : Reach.None;
            }

            default:
                return Cannot(refusals, node,
                    $"'{Brief(node)}' is not something a projection can be traced through — a field has "
                    + "to be reached from one of the rows the Select was given");
        }
    }

    /// <summary>
    /// What a lambda handed to a client-side operator ranges over.
    /// </summary>
    /// <remarks>
    /// The rows the receiver holds, when it still holds those: the node stands for one type's
    /// fields, and an operator in front of this one may have turned them into something else. A
    /// lambda over what a projection produced names members of a type the node does not stand
    /// for, so binding it there would ask the server for fields that are not the member's —
    /// unless what it produced is a scalar, which has no fields to name at all.
    /// </remarks>
    private static Scope Inside(
        LambdaExpressionSyntax lambda,
        Scope scope,
        Reach over,
        Reach produced,
        SemanticModel model,
        CancellationToken token)
    {
        var names = Parameters(lambda);

        // A lambda whose parameters cannot be read one for one against the delegate it binds to
        // is one whose reads cannot be placed. It joins the scope unnamed, which makes every name
        // inside it something to understand outright rather than something to pass over.
        if (names is null
            || model.GetSymbolInfo(lambda, token).Symbol is not IMethodSymbol written
            || written.Parameters.Length != names.Count)
            return scope.With(null, Reach.No);

        for (int i = 0; i < names.Count; i++)
            scope = scope.With(names[i], Ranges(written.Parameters[i].Type, over, produced));

        return scope;
    }

    /// <summary>
    /// What one parameter of such a lambda ranges over.
    /// </summary>
    /// <remarks>
    /// Decided by its type rather than by its position, which is what lets one rule serve every
    /// operator: <c>Zip</c> hands both of its parameters rows of the sequences it was given,
    /// <c>SelectMany</c>'s result selector hands one of each, and neither needs to be known here
    /// by name. What a projection turned the rows into is not a row of the graph, and its members
    /// are not fields — unless it turned them into a scalar, which has no members to name.
    /// </remarks>
    private static Reach Ranges(ITypeSymbol parameter, Reach over, Reach produced)
    {
        var ranges = GraphQLTypeFacts.Unwrap(parameter);

        // What a flattening selector reached is the more particular answer, so it is asked first.
        if (Stands(produced, ranges))
            return produced;

        if (Stands(over, ranges))
            return over;

        return GraphQLTypeFacts.IsScalar(ranges) ? Reach.None : Reach.No;
    }

    /// <summary>Whether a node stands for rows of this type.</summary>
    private static bool Stands(Reach reach, ITypeSymbol type)
        => reach.Node is not null
            && reach.Element is not null
            && !GraphQLTypeFacts.IsScalar(reach.Element)
            && SymbolEqualityComparer.Default.Equals(type, reach.Element);

    /// <summary>A lambda's parameter names, in order, or null when they cannot be read.</summary>
    private static List<string>? Parameters(LambdaExpressionSyntax lambda)
        => lambda switch
        {
            SimpleLambdaExpressionSyntax simple => [simple.Parameter.Identifier.ValueText],
            ParenthesizedLambdaExpressionSyntax parenthesized
                => [.. parenthesized.ParameterList.Parameters.Select(x => x.Identifier.ValueText)],
            _ => null
        };

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
    /// <paramref name="source"/> is where the receiver landed, so an operator further out can go
    /// on collecting into it. It reaches nothing when the call reads nothing through a receiver —
    /// a static method, or an extension called as one — which is not a refusal.
    /// </para>
    /// </remarks>
    private static bool CollectCall(
        InvocationExpressionSyntax invocation,
        Scope scope,
        SemanticModel model,
        CancellationToken token,
        Refusals refusals,
        out Reach source)
    {
        source = Reach.None;

        var receiver = invocation.Expression is MemberAccessExpressionSyntax access
            ? access.Expression
            : null;

        // Called some other way than through a receiver or by name — through a delegate a row
        // holds, say — which is a call this cannot follow.
        if (receiver is null && scope.Mentions(invocation.Expression))
        {
            return refusals.No(invocation.Expression,
                $"'{Brief(invocation.Expression)}' is called through a value a row holds, which is not a "
                + "call this can trace a field through");
        }

        if (receiver is not null && scope.Mentions(receiver))
        {
            source = CollectSequence(receiver, scope, model, token, refusals);

            if (source.Declined)
                return false;
        }

        if (source.Node is not null)
        {
            var beginning = Origin(receiver!);
            var origin = TypeOf(beginning, model, token);
            var handed = TypeOf(receiver!, model, token);

            if (origin is not null
                && handed is not null
                && !GraphQLTypeFacts.IsLeaf(origin)
                && SymbolEqualityComparer.Default.Equals(
                    GraphQLTypeFacts.Unwrap(origin), GraphQLTypeFacts.Unwrap(handed)))
            {
                CollectScalars(GraphQLTypeFacts.Unwrap(origin), source.Node, beginning.GetLocation());
            }

            if (!Expand(origin, source.Node, beginning, refusals))
                return false;
        }

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (!CollectArgument(argument.Expression, scope, source, model, token, refusals))
                return false;
        }

        return true;
    }

    /// <summary>
    /// What one argument of such a call reads.
    /// </summary>
    /// <remarks>
    /// An ordinary expression is collected where it is rooted, exactly as anywhere else. A lambda
    /// is the one that needs saying: it ranges over what the receiver holds, as one given to a
    /// LINQ operator does, so its own parameter joins the scope and its body is collected with
    /// the rows further out still in it.
    /// </remarks>
    private static bool CollectArgument(
        ExpressionSyntax argument,
        Scope scope,
        Reach source,
        SemanticModel model,
        CancellationToken token,
        Refusals refusals)
        => argument is LambdaExpressionSyntax lambda
            ? Collect(lambda.Body, Inside(lambda, scope, source, Reach.None, model, token), model, token, refusals)
            : Collect(argument, scope, model, token, refusals);

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

    /// <summary>What a member chain is read off, which decides whether it is a read of the graph.</summary>
    private static SyntaxNode Root(SyntaxNode expression)
        => expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => Root(parenthesized.Expression),
            CastExpressionSyntax cast => Root(cast.Expression),
            PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } suppress
                => Root(suppress.Operand),
            MemberAccessExpressionSyntax member => Root(member.Expression),

            // Taking one of many changes which row is read and not which fields it has, so a
            // path reads through an index the way it reads through a `!`.
            ElementAccessExpressionSyntax element => Root(element.Expression),
            _ => expression
        };

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

    /// <summary>
    /// Walks a member chain onto the selection tree, returning where it landed.
    /// </summary>
    /// <remarks>
    /// The root is settled first, because it decides whether any of this is a read of the graph.
    /// A chain rooted at a row is a path through the document, and every member along it has to be
    /// a field; one rooted anywhere else is the caller's own value, where a member is a member and
    /// none of this library's business.
    /// </remarks>
    private static Reach Descend(
        SyntaxNode expression,
        Scope scope,
        SemanticModel model,
        CancellationToken token,
        Refusals refusals)
    {
        var root = Root(expression);
        var reach = Start(root, expression, scope, model, token, refusals);

        if (reach.Node is not { } from)
            return reach;

        var path = new List<(string Name, Location Where, IPropertySymbol Property)>();
        var current = expression;

        while (current != root)
        {
            switch (current)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case CastExpressionSyntax cast:
                    current = cast.Expression;
                    continue;

                case PostfixUnaryExpressionSyntax
                    { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } suppress:
                    current = suppress.Operand;
                    continue;

                // One of what a member holds. The fields are the member's either way, so nothing
                // is placed for the index — but the index itself may read a row, and that is a
                // read like any other.
                case ElementAccessExpressionSyntax indexed:
                {
                    if (!Parts(indexed.ArgumentList, scope, model, token, refusals))
                        return Reach.No;

                    current = indexed.Expression;
                    continue;
                }

                case MemberAccessExpressionSyntax member:
                {
                    if (model.GetSymbolInfo(member, token).Symbol is not IPropertySymbol property)
                    {
                        return Cannot(refusals, member.Name,
                            $"'{member.Name}' is not a property, and only a property is a field the query "
                            + "can ask the server for");
                    }

                    if (GraphQLTypeFacts.IsIgnored(property))
                    {
                        return Cannot(refusals, member.Name,
                            $"'{property.Name}' is marked [JsonIgnore], so it is not a field the query can "
                            + "ask the server for");
                    }

                    path.Insert(0, (GraphQLTypeFacts.FieldName(property), member.Name.GetLocation(), property));

                    current = member.Expression;
                    continue;
                }

                default:
                    return Cannot(refusals, expression,
                        $"'{Brief(expression)}' is not read from one of the rows the Select was given, and "
                        + "only what is read from a row can be asked of the server");
            }
        }

        var node = from;
        var element = reach.Element;

        foreach (var (name, where, property) in path)
        {
            // A member of something the chain made rather than of the row it stands for —
            // `IGrouping.Key`, whose value came from the key selector and is not a field any
            // server has. It is read where the rows are, like anything else client-side.
            if (element is not null && !Holds(element, property))
                return Reach.None;

            node = node.Child(name, where);
            element = GraphQLTypeFacts.Unwrap(property.Type);

            // A field that needs no selection set ends the path: what is written after it reads
            // the value the server sent — `c.Name.Length`, `c.Founded.Year`, a property of a
            // value the model converts — which happens where the rows are and asks for nothing.
            // Tracing on would ask for `name { length }`, which is not a thing a schema has.
            //
            // The field itself is already on the tree, and what the chain does with it from here
            // is the caller's own — which is what reaching nothing means. Saying so rather than
            // handing back the value's type is what keeps an operator further out from taking
            // that type for rows of the graph and collecting its properties onto the field.
            if (GraphQLTypeFacts.IsLeaf(property))
                return Reach.None;
        }

        return Reach.At(node, element);
    }

    /// <summary>
    /// Where a member chain starts reading from, which is what decides whether any of it is a
    /// read of the graph.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row in scope is the ordinary case, and a name that is in none of them is the caller's
    /// own value — neither needs saying twice. The one that does is a chain read off what a
    /// client-side call produced, which is three cases wearing one shape.
    /// </para>
    /// <para>
    /// Where the call produced a scalar, nothing under it could have been asked for, so what
    /// follows is the caller's own business and only what the call itself reads is collected:
    /// <c>c.Name.Trim().Length</c> asks for <c>name</c> and stops. Where it produced the very
    /// rows a member holds — <c>c.Permissions.First()</c> — the path goes on from where that
    /// chain landed, because <c>.Code</c> after it is that member's field and nothing else.
    /// Where it produced something else, a member of it might be a field or might not, and one
    /// left out of the document is one the shaping goes on to read off a row that has not got it.
    /// That last is the refusal.
    /// </para>
    /// </remarks>
    private static Reach Start(
        SyntaxNode root,
        SyntaxNode expression,
        Scope scope,
        SemanticModel model,
        CancellationToken token,
        Refusals refusals)
    {
        if (root is IdentifierNameSyntax identifier)
        {
            if (scope.Find(identifier.Identifier.ValueText) is not { } named)
            {
                return scope.Opaque
                    ? Cannot(refusals, identifier, Opaquely(identifier))
                    : Reach.None;
            }

            if (named.Outside)
                return Reach.None;

            return named.Node is null ? Cannot(refusals, identifier, Unplaceable(identifier)) : named;
        }

        if (root is not InvocationExpressionSyntax call || TypeOf(call, model, token) is not { } produced)
        {
            return Cannot(refusals, expression,
                $"'{Brief(expression)}' is read off something this cannot follow, and a field has to be "
                + "reached from one of the rows the Select was given");
        }

        if (GraphQLTypeFacts.IsLeaf(produced))
            return Parts(expression, scope, model, token, refusals) ? Reach.None : Reach.No;

        var landed = CollectSequence(call, scope, model, token, refusals);

        if (landed.Declined)
            return Reach.No;

        if (landed.Outside)
            return Reach.None;

        // Only where what the call handed back is still the rows that node stands for. A chain
        // that projected handed back something else, whose members are not that member's fields.
        if (!SymbolEqualityComparer.Default.Equals(GraphQLTypeFacts.Unwrap(produced), landed.Element))
        {
            return Cannot(refusals, expression,
                $"'{Brief(expression)}' reads a member of what '{Brief(call)}' produced, which is not one "
                + "of the rows the server sends — read the fields before the chain changes what it holds");
        }

        return landed;
    }

    /// <summary>
    /// Whether a type has this property, as one of its own or as something it is.
    /// </summary>
    /// <remarks>
    /// The question a member chain asks at every step, and for a long time it asked only whether
    /// the member was a property at all. <c>IGrouping.Key</c> is a property, and grouping rows of
    /// the graph leaves a lambda ranging over something that holds them without being one — so
    /// <c>g.Key</c> was written into the document as a field, which no schema has.
    /// </remarks>
    private static bool Holds(ITypeSymbol element, IPropertySymbol property)
    {
        if (property.ContainingType is not { } owner)
            return false;

        for (var type = element; type is not null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, owner.OriginalDefinition))
                return true;
        }

        foreach (var implemented in element.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, owner.OriginalDefinition))
                return true;
        }

        return false;
    }

    /// <summary>Records why a read could not be placed, and answers a refusal.</summary>
    private static Reach Cannot(Refusals refusals, SyntaxNode where, string reason)
    {
        refusals.Note(where, reason);
        return Reach.No;
    }

    /// <summary>Why a name cannot be told from a row while a lambda's parameters are unreadable.</summary>
    private static string Opaquely(IdentifierNameSyntax identifier)
        => $"'{identifier}' cannot be told from a row here: a lambda in this projection has parameters "
        + "this could not read, so a name that is not one of them might still be one of its rows — and "
        + "a field left out of the document is one the rows come back without";

    /// <summary>Why reading a field off a lambda's own parameter cannot be placed.</summary>
    private static string Unplaceable(IdentifierNameSyntax identifier)
        => $"'{identifier}' ranges over what an operator in front of it produced rather than over rows of "
        + "the graph, so the fields named on it are not fields the server has — read them before the "
        + "chain changes what it holds";

    private static bool IsLinqOperator(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken token)
        => model.GetSymbolInfo(invocation, token).Symbol is IMethodSymbol method
            && method.ContainingType?.ToDisplayString() is "System.Linq.Queryable" or "System.Linq.Enumerable";

    private static ITypeSymbol? TypeOf(SyntaxNode node, SemanticModel model, CancellationToken token)
        => model.GetTypeInfo(node, token).Type;

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
