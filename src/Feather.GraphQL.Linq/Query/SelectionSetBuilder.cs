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
/// <c>Select</c> is a field-selection expression that borrows familiar syntax, which is why
/// projections are restricted to member-access trees here (FGQL013). The materializer reads a
/// projection back by walking these same paths, so whatever this builder cannot name it also
/// cannot read.
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
    /// The no-<c>Select</c> default: every mapped scalar. Object and collection fields are never
    /// walked implicitly — that is how a query quietly grows past a server's depth limit — so a
    /// type carrying them has to say what it wants.
    /// </summary>
    private static void CollectScalars(Type elementType, Node root)
    {
        var metadata = ReflectionTypeMetadata.For(elementType);

        foreach (var field in metadata.Fields)
        {
            if (field.IsIgnored)
                continue;

            if (!IsScalar(field.ClrType))
                throw new GraphQLTranslationException("FGQL014",
                    $"'{elementType.Name}.{field.ClrName}' is a nested field, so '{elementType.Name}' "
                    + "requires an explicit Select to say which of its fields to request.");

            root.Child(field.FieldName);
        }
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

            // A nested projection over a collection field: p.Tags.Select(t => t.Name).
            case MethodCallExpression { Method.Name: nameof(Queryable.Select) } select:
            {
                var source = select.Arguments.Count > 1 ? select.Arguments[0] : select.Object!;
                var inner = select.Arguments[^1] switch
                {
                    UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression q } => q,
                    LambdaExpression l => l,
                    _ => throw Computation(node)
                };

                var nested = Descend(source, parameter, target);
                Collect(inner.Body, inner.Parameters[0], nested);
                return;
            }

            case MemberExpression member:
                Descend(member, parameter, target);
                return;

            case ParameterExpression p when p == parameter:
                CollectScalars(p.Type, target);
                return;

            default:
                throw Computation(node);
        }
    }

    /// <summary>Walks a member chain onto the selection tree, returning the node it lands on.</summary>
    private static Node Descend(Expression expression, ParameterExpression parameter, Node target)
    {
        var node = target;
        foreach (string segment in FieldPath.Resolve(expression, parameter))
            node = node.Child(segment);

        return node;
    }

    private static GraphQLTranslationException Computation(Expression node) => FieldPath.Computation(node);

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
