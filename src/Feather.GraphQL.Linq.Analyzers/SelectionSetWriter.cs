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
/// recognise returns null, no interceptor is emitted, and the runtime translator does the work
/// exactly as it did before.
/// </para>
/// </remarks>
internal static class SelectionSetWriter
{
    /// <summary>A field and the fields selected beneath it, in the order they were named.</summary>
    internal sealed class Node
    {
        private readonly Dictionary<string, Node> _children = new(StringComparer.Ordinal);

        public List<string> Order { get; } = [];

        public Node Child(string name)
        {
            if (_children.TryGetValue(name, out var existing))
                return existing;

            var child = new Node();
            _children[name] = child;
            Order.Add(name);
            return child;
        }

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
    {
        var root = new Node();

        bool built = projection is null
            ? CollectScalars(elementType, root)
            : Collect(projection.Body, Parameter(projection), root, model, token);

        return built && root.Order.Count > 0 ? root : null;
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
    private static bool CollectScalars(ITypeSymbol type, Node target)
    {
        foreach (var property in GraphQLTypeFacts.Fields(type))
        {
            if (GraphQLTypeFacts.IsLeaf(property.Type))
                target.Child(GraphQLTypeFacts.FieldName(property));
        }

        return target.Order.Count > 0;
    }

    private static bool Collect(
        SyntaxNode node,
        string? parameter,
        Node target,
        SemanticModel model,
        CancellationToken token)
    {
        switch (node)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return Collect(parenthesized.Expression, parameter, target, model, token);

            case CastExpressionSyntax cast:
                return Collect(cast.Expression, parameter, target, model, token);

            case AnonymousObjectCreationExpressionSyntax anonymous:
                foreach (var initializer in anonymous.Initializers)
                {
                    if (!Collect(initializer.Expression, parameter, target, model, token))
                        return false;
                }

                return true;

            case ObjectCreationExpressionSyntax creation:
            {
                foreach (var argument in creation.ArgumentList?.Arguments ?? default)
                {
                    if (!Collect(argument.Expression, parameter, target, model, token))
                        return false;
                }

                foreach (var expression in creation.Initializer?.Expressions ?? default)
                {
                    // Only `Member = value`; a collection initializer projects nothing nameable.
                    if (expression is not AssignmentExpressionSyntax assignment
                        || !Collect(assignment.Right, parameter, target, model, token))
                        return false;
                }

                return true;
            }

            // A chain of LINQ operators over a collection member, all of which run client-side:
            // what they change is the shape of the result, never which fields to request.
            case InvocationExpressionSyntax invocation when IsLinqOperator(invocation, model, token):
            {
                var nested = CollectSequence(invocation, parameter, target, model, token);

                return nested is not null && Expand(TypeOf(invocation, model, token), nested);
            }

            case MemberAccessExpressionSyntax member:
            {
                var node2 = Descend(member, parameter, target, model, token);

                return node2 is not null && Expand(TypeOf(member, model, token), node2);
            }

            case IdentifierNameSyntax identifier when identifier.Identifier.ValueText == parameter:
                return CollectScalars(TypeOf(identifier, model, token)!, target);

            default:
                return false;
        }
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
        CancellationToken token)
    {
        switch (node)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return CollectSequence(parenthesized.Expression, parameter, target, model, token);

            case CastExpressionSyntax cast:
                return CollectSequence(cast.Expression, parameter, target, model, token);

            case InvocationExpressionSyntax invocation when IsLinqOperator(invocation, model, token):
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax access)
                    return null;

                var nested = CollectSequence(access.Expression, parameter, target, model, token);
                if (nested is null)
                    return null;

                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    if (argument.Expression is not LambdaExpressionSyntax lambda)
                        continue;

                    if (!Collect(lambda.Body, Parameter(lambda), nested, model, token))
                        return null;
                }

                return nested;
            }

            case MemberAccessExpressionSyntax member:
                return Descend(member, parameter, target, model, token);

            case IdentifierNameSyntax identifier when identifier.Identifier.ValueText == parameter:
                return target;

            default:
                return null;
        }
    }

    /// <summary>
    /// A GraphQL object field must carry a selection set, so naming one without saying what to
    /// take from it selects its scalars — that member's own, and no further.
    /// </summary>
    private static bool Expand(ITypeSymbol? memberType, Node node)
    {
        if (node.Order.Count > 0)
            return true;

        if (memberType is null)
            return false;

        if (GraphQLTypeFacts.IsLeaf(memberType))
            return true;

        var unwrapped = GraphQLTypeFacts.UnwrapNullable(memberType);

        return CollectScalars(GraphQLTypeFacts.ElementType(unwrapped) ?? unwrapped, node);
    }

    /// <summary>Walks a member chain onto the selection tree, returning the node it lands on.</summary>
    private static Node? Descend(
        SyntaxNode expression,
        string? parameter,
        Node target,
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

                case CastExpressionSyntax cast:
                    current = cast.Expression;
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

                case IdentifierNameSyntax identifier when identifier.Identifier.ValueText == parameter:
                {
                    var node = target;
                    foreach (string segment in path)
                        node = node.Child(segment);

                    return node;
                }

                default:
                    return null;
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
}
