using System.Linq.Expressions;
using System.Text;

namespace Feather.GraphQL.Linq.Execution;

/// <summary>
/// A stable name for a projection, computable from the expression tree at runtime and from the
/// syntax at compile time — which is what lets a generated shaper be found for one.
/// </summary>
/// <remarks>
/// <para>
/// The key is total over the projection language the translator allows, and that is not a
/// coincidence: computation is already <c>FGQL013</c>, so a projection is a set of member paths
/// and the names they are bound to. Two projections with the same key therefore read the same
/// fields into the same shape, and no third thing can distinguish them.
/// </para>
/// <para>
/// Anything outside that language returns null, and the caller compiles the lambda instead. A
/// key that cannot be computed costs performance; a key that collided would produce wrong data,
/// so the format errs toward being long.
/// </para>
/// </remarks>
internal static class ProjectionKey
{
    /// <summary>The key for a projection, or null when it is outside the shapeable language.</summary>
    public static string? For(LambdaExpression projection)
    {
        var parameter = projection.Parameters[0];
        var key = new StringBuilder(parameter.Type.FullName).Append("=>");

        return Append(key, projection.Body, parameter) ? key.ToString() : null;
    }

    private static bool Append(StringBuilder key, Expression node, ParameterExpression parameter)
    {
        switch (node)
        {
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                return Append(key, convert.Operand, parameter);

            // An anonymous type, or any type built positionally. Members carries the names the
            // properties took, which is half of what makes the shape identifiable.
            case NewExpression { Members: not null } init:
            {
                key.Append("new{");

                for (int i = 0; i < init.Arguments.Count; i++)
                {
                    if (i > 0)
                        key.Append(',');

                    key.Append(init.Members[i].Name).Append(':');

                    if (!Append(key, init.Arguments[i], parameter))
                        return false;
                }

                key.Append('}');
                return true;
            }

            // A named type built and then filled: `new Summary { Title = p.Name }`. The type is
            // part of the key where an anonymous type's is not, because two projections filling
            // two different types with the same members are two different shapes — while two
            // anonymous ones with the same members are the same type by construction.
            case MemberInitExpression { NewExpression.Arguments.Count: 0 } init:
            {
                key.Append("new").Append(init.Type.FullName).Append('{');

                for (int i = 0; i < init.Bindings.Count; i++)
                {
                    if (init.Bindings[i] is not MemberAssignment assignment)
                        return false;

                    if (i > 0)
                        key.Append(',');

                    key.Append(assignment.Member.Name).Append(':');

                    if (!Append(key, assignment.Expression, parameter))
                        return false;
                }

                key.Append('}');
                return init.Bindings.Count > 0;
            }

            case MemberExpression member:
                return AppendPath(key, member, parameter);

            default:
                return false;
        }
    }

    /// <summary>Writes a member chain as the dotted path from the lambda's own parameter.</summary>
    private static bool AppendPath(StringBuilder key, Expression node, ParameterExpression parameter)
    {
        var path = new List<string>();
        var current = node;

        while (true)
        {
            switch (current)
            {
                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                    current = convert.Operand;
                    continue;

                case MemberExpression member:
                    path.Insert(0, member.Member.Name);
                    current = member.Expression!;
                    continue;

                case ParameterExpression p when p == parameter:
                    key.AppendJoin('.', path);
                    return path.Count > 0;

                default:
                    return false;
            }
        }
    }
}
