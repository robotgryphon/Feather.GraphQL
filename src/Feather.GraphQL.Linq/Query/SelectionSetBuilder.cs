using System.Collections;
using System.Linq.Expressions;
using Feather.GraphQL.Linq.Document;
using Feather.GraphQL.Linq.Metadata;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// Derives a GraphQL selection set from a projection, or from the element type when the chain
/// has no projection.
/// </summary>
/// <remarks>
/// A projection has two jobs, and this type does the first: name the fields to request. The
/// second — producing the projected value — happens after materialization, by running the same
/// lambda over the deserialized element.
///
/// It reads member trees, nested LINQ chains over collection members, and object members named
/// without a projection of their own — enough to say what to ask for. Computation it cannot see
/// through is FGQL013: requesting the wrong fields is a worse failure than refusing the chain.
/// </remarks>
internal static class SelectionSetBuilder
{
    private sealed class Node
    {
        public Dictionary<string, Node> Children { get; } = [];
        public List<string> Order { get; } = [];

        public Node Child(string name)
        {
            if (Children.TryGetValue(name, out var existing))
                return existing;

            var child = new Node();
            Children[name] = child;
            Order.Add(name);
            return child;
        }

        public IReadOnlyList<GqlField> ToFields()
            => Order.Select(name => new GqlField(name) { Selection = Children[name].ToFields() }).ToArray();
    }

    public static IReadOnlyList<GqlField> Build(Type elementType, LambdaExpression? projection)
    {
        var root = new Node();

        if (projection is null)
            CollectScalars(elementType, root);
        else
            Collect(projection.Body, projection.Parameters[0], root);

        if (root.Order.Count == 0)
            throw new GraphQLTranslationException("FGQL012",
                $"The query selects no fields from '{elementType.Name}'.");

        return root.ToFields();
    }

    /// <summary>
    /// The automatic selection: a type's own leaf fields, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Object and collection fields are skipped rather than walked. A scalar is there for the
    /// asking, so taking every one of them costs nothing anybody would object to; a nested field
    /// is a second trip through a resolver, and helping yourself to those is how a query quietly
    /// grows past a server's depth limit — or, when the graph loops back, has no depth at which
    /// to stop.
    /// </para>
    /// <para>
    /// The consequence is worth stating plainly: a nested field you did not ask for comes back
    /// unset. Ask for it with a projection.
    /// </para>
    /// </remarks>
    private static void CollectScalars(Type elementType, Node root)
    {
        var metadata = ReflectionTypeMetadata.For(elementType);

        foreach (var field in metadata.Fields)
        {
            if (field.IsIgnored || !IsLeaf(field.ClrType))
                continue;

            root.Child(field.FieldName);
        }

        // Every field is nested, so there is nothing to select automatically and a selection set
        // cannot be empty.
        if (root.Order.Count == 0)
            throw new GraphQLTranslationException("FGQL014",
                $"'{elementType.Name}' has no scalar fields, so there is nothing to select from it "
                + "automatically. Say which of its fields to request with a Select.");
    }

    /// <summary>
    /// A field that needs no selection set of its own: a scalar, or a list of them.
    /// </summary>
    private static bool IsLeaf(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return IsScalar(ElementType(underlying) ?? underlying);
    }

    private static void Collect(Expression node, ParameterExpression parameter, Node target)
    {
        switch (node)
        {
            case NewExpression init:
                foreach (var argument in init.Arguments)
                    Collect(argument, parameter, target);
                return;

            case MemberInitExpression memberInit:
                Collect(memberInit.NewExpression, parameter, target);
                foreach (var binding in memberInit.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                        throw Computation(node);

                    Collect(assignment.Expression, parameter, target);
                }

                return;

            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                Collect(convert.Operand, parameter, target);
                return;

            // A chain of LINQ operators over a collection field:
            // p.Attacks.Fast.Select(a => new { a.Name }).ToArray().
            case MethodCallExpression call when IsLinqOperator(call):
                // Expand covers the chain that never projects — p.Attacks.Fast.ToArray() — and
                // returns immediately when a Select in the chain already said what to take.
                Expand(call.Type, CollectSequence(call, parameter, target));
                return;

            case MemberExpression member:
                Expand(member.Type, Descend(member, parameter, target));
                return;

            case ParameterExpression p when p == parameter:
                CollectScalars(p.Type, target);
                return;

            default:
                throw Computation(node);
        }
    }

    /// <summary>
    /// Walks a chain of LINQ operators over a collection member, returning the node its elements
    /// select into.
    /// </summary>
    /// <remarks>
    /// Every operator in such a chain runs client-side over what came back, so none of them
    /// changes <em>which</em> fields to request: the source names the collection, and each lambda
    /// names fields of its elements. That is why <c>Select(…)</c> and
    /// <c>Select(…).ToArray()</c> ask for exactly the same thing — the materializing call is
    /// C# needing an array, not GraphQL needing anything.
    /// </remarks>
    private static Node CollectSequence(Expression node, ParameterExpression parameter, Node target)
    {
        switch (node)
        {
            case MethodCallExpression call when IsLinqOperator(call):
            {
                bool extension = call.Object is null;
                var source = extension ? call.Arguments[0] : call.Object!;
                var nested = CollectSequence(source, parameter, target);

                for (int i = extension ? 1 : 0; i < call.Arguments.Count; i++)
                {
                    if (Unquote(call.Arguments[i]) is LambdaExpression lambda)
                        Collect(lambda.Body, lambda.Parameters[0], nested);
                }

                return nested;
            }

            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                return CollectSequence(convert.Operand, parameter, target);

            case MemberExpression member:
                return Descend(member, parameter, target);

            case ParameterExpression p when p == parameter:
                return target;

            default:
                throw Computation(node);
        }
    }

    /// <summary>
    /// A GraphQL object field must carry a selection set, so naming one without saying what to
    /// take from it selects its scalars.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose: that member's own leaf fields, and no further. Nested fields inside it
    /// are skipped, which is what keeps a projection from walking past a server's depth limit — or
    /// from looping forever on a graph that points back at itself.
    /// </remarks>
    private static void Expand(Type memberType, Node node)
    {
        // An explicit projection already said what it wants.
        if (node.Order.Count > 0)
            return;

        // A scalar, or a list of them: a selection set on it would be invalid.
        if (IsLeaf(memberType))
            return;

        var type = Nullable.GetUnderlyingType(memberType) ?? memberType;
        CollectScalars(ElementType(type) ?? type, node);
    }

    /// <summary>The element type of a collection, or null when the type is not one.</summary>
    private static Type? ElementType(Type type)
    {
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
            return null;

        if (type.IsArray)
            return type.GetElementType();

        return type.GetInterfaces().Append(type)
            .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    private static bool IsLinqOperator(MethodCallExpression call)
        => call.Method.DeclaringType == typeof(Queryable)
            || call.Method.DeclaringType == typeof(Enumerable);

    private static Expression Unquote(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Quote, Operand: { } operand }
            ? operand
            : expression;

    /// <summary>Walks a member chain onto the selection tree, returning the node it lands on.</summary>
    private static Node Descend(Expression expression, ParameterExpression parameter, Node target)
    {
        var path = new List<string>();
        var current = expression;

        while (true)
        {
            switch (current)
            {
                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                    current = convert.Operand;
                    continue;

                case MemberExpression member:
                {
                    var declaring = member.Member.DeclaringType ?? throw Computation(expression);
                    var metadata = ReflectionTypeMetadata.For(declaring);

                    if (!metadata.TryGetField(member.Member.Name, out var field))
                        throw Computation(expression);

                    if (field.IsIgnored)
                        throw GraphQLTranslationException.IgnoredMember(field.ClrName, declaring);

                    path.Insert(0, field.FieldName);
                    current = member.Expression ?? throw Computation(expression);
                    continue;
                }

                case ParameterExpression p when p == parameter:
                {
                    var node = target;
                    foreach (string segment in path)
                        node = node.Child(segment);

                    return node;
                }

                default:
                    throw Computation(expression);
            }
        }
    }

    private static GraphQLTranslationException Computation(Expression node)
        => new("FGQL013",
            $"'{node}' computes over the projected value, and the fields it needs cannot be "
            + "derived from it. Project the fields you want first, then compute over the result.",
            node);

    internal static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying.IsPrimitive || underlying.IsEnum)
            return true;

        if (underlying == typeof(string) || underlying == typeof(decimal) || underlying == typeof(Guid)
            || underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset)
            || underlying == typeof(DateOnly) || underlying == typeof(TimeOnly)
            || underlying == typeof(TimeSpan) || underlying == typeof(Uri))
            return true;

        return !typeof(IEnumerable).IsAssignableFrom(underlying) && underlying.IsValueType
            && underlying.Assembly == typeof(int).Assembly;
    }
}
