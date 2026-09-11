using System.Linq.Expressions;

namespace Feather.GraphQL.Linq.Expressions;

internal sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
{
    public static Expression Rebind(Expression body, ParameterExpression from, ParameterExpression to)
        => new ParameterRebinder(from, to).Visit(body)!;

    protected override Expression VisitParameter(ParameterExpression node)
        => node == from ? to : base.VisitParameter(node);
}