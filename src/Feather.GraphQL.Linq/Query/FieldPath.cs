using System.Linq.Expressions;
using Feather.GraphQL.Linq.Metadata;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// Resolves a member chain rooted at a lambda parameter into the GraphQL field names it names.
/// </summary>
/// <remarks>
/// One resolver, two consumers: <see cref="SelectionSetBuilder"/> turns the path into a selection
/// set, and the materializer walks the same path back out of the response. Splitting them would
/// let a request ask for one name and a reader look for another.
/// </remarks>
internal static class FieldPath
{
    /// <summary>
    /// Walks <paramref name="expression"/> back to <paramref name="parameter"/>, returning the
    /// field names in source order.
    /// </summary>
    public static IReadOnlyList<string> Resolve(Expression expression, ParameterExpression parameter)
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
                    return path;

                default:
                    throw Computation(expression);
            }
        }
    }

    /// <summary>
    /// FGQL013. Projections name fields; they do not compute over them. The selection set has to
    /// be derivable from the projection, and an expression the server never sees cannot be one.
    /// </summary>
    public static GraphQLTranslationException Computation(Expression node)
        => new("FGQL013",
            $"'{node}' computes over the projected value. Projections select fields only — a "
            + "GraphQL selection set has no room for client-side computation.",
            node);
}
