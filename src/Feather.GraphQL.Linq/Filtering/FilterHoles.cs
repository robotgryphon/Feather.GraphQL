using System.Linq.Expressions;
using Feather.GraphQL.Linq.Expressions;

namespace Feather.GraphQL.Linq.Filtering;

/// <summary>
/// Collects the values a compiler-printed filter left holes for.
/// </summary>
/// <remarks>
/// <para>
/// The other half of a precompiled filter. The compiler decided which fields, which operations
/// and how they nest; what it could not decide is what the predicate compares against, because
/// that lives in the expression tree the caller's own code builds on every execution. This walks
/// that tree and takes the values, and nothing else — no paths to resolve, no operators to map,
/// no shape to build.
/// </para>
/// <para>
/// The order is the contract: the compiler numbered its holes by flattening the conjunction
/// left to right, and so does this. Merged predicates fold left, so several <c>Where</c> calls
/// come out in the order they were written.
/// </para>
/// <para>
/// A shape that does not match what was expected returns null rather than guessing, and the
/// caller lowers the predicate the ordinary way. That makes a disagreement between the two halves
/// a slower query rather than a wrong one — which is the only acceptable way to have two halves.
/// </para>
/// </remarks>
internal static class FilterHoles
{
    public static object?[]? Collect(LambdaExpression predicate, int expected)
    {
        var body = PartialEvaluator.Reduce(predicate.Body);
        var values = new object?[expected];
        int filled = 0;

        return Walk(body, values, ref filled) && filled == expected ? values : null;
    }

    private static bool Walk(Expression node, object?[] values, ref int filled)
    {
        switch (node)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso } and:
                return Walk(and.Left, values, ref filled) && Walk(and.Right, values, ref filled);

            case BinaryExpression binary when IsComparison(binary.NodeType):
            {
                if (filled >= values.Length || Unwrap(binary.Right) is not ConstantExpression constant)
                    return false;

                values[filled++] = constant.Value;
                return true;
            }

            default:
                return false;
        }
    }

    private static Expression Unwrap(Expression node)
        => node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
            ? Unwrap(convert.Operand)
            : node;

    private static bool IsComparison(ExpressionType type)
        => type is ExpressionType.Equal or ExpressionType.NotEqual
            or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
            or ExpressionType.LessThan or ExpressionType.LessThanOrEqual;
}
