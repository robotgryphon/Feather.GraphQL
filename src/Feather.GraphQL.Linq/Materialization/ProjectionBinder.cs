using System.Linq.Expressions;
using System.Text.Json;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Materialization;

/// <summary>
/// Rebuilds a projection to run over a response element instead of over an entity.
/// </summary>
/// <remarks>
/// A projection cannot simply be deserialized into. <c>Select(p =&gt; new { p.Name, p.Email })</c>
/// produces an anonymous type whose members are named after the CLR properties, while the response
/// carries the GraphQL field names — <c>emailAddress</c>, not <c>Email</c>. The names only line up
/// on the source type, so the projection is rewritten in terms of the field paths
/// <see cref="SelectionSetBuilder"/> asked for and the shape is assembled from those.
/// </remarks>
internal static class ProjectionBinder
{
    /// <summary>
    /// Compiles <paramref name="projection"/> into a reader over one element of the result.
    /// </summary>
    /// <remarks>
    /// Compiled per materialization rather than cached: the chain builds a fresh expression tree
    /// on every call, so a cache would need to key on tree structure — more machinery than one
    /// small <see cref="Expression.Lambda{TDelegate}(Expression, ParameterExpression[])"/> per
    /// request is worth.
    /// </remarks>
    public static Func<JsonElement, TResult> Compile<TResult>(LambdaExpression projection)
    {
        var element = Expression.Parameter(typeof(JsonElement), "element");
        var body = Rewrite(projection.Body, projection.Parameters[0], element);

        if (body.Type != typeof(TResult))
            body = Expression.Convert(body, typeof(TResult));

        return Expression.Lambda<Func<JsonElement, TResult>>(body, element).Compile();
    }

    /// <summary>
    /// Mirrors <see cref="SelectionSetBuilder"/>'s walk of the same tree: every node it can turn
    /// into a selection, this turns into a read.
    /// </summary>
    private static Expression Rewrite(Expression node, ParameterExpression parameter, ParameterExpression element)
    {
        switch (node)
        {
            case NewExpression init:
            {
                var arguments = init.Arguments.Select(a => Rewrite(a, parameter, element)).ToArray();

                if (init.Constructor is null)
                    return Expression.New(init.Type);

                return init.Members is null
                    ? Expression.New(init.Constructor, arguments)
                    : Expression.New(init.Constructor, arguments, init.Members);
            }

            case MemberInitExpression memberInit:
            {
                var bindings = memberInit.Bindings.Select(binding => binding is MemberAssignment assignment
                    ? Expression.Bind(assignment.Member, Rewrite(assignment.Expression, parameter, element))
                    : throw FieldPath.Computation(node));

                return Expression.MemberInit(
                    (NewExpression)Rewrite(memberInit.NewExpression, parameter, element),
                    bindings);
            }

            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                return Expression.Convert(Rewrite(convert.Operand, parameter, element), convert.Type);

            // A nested projection over a collection field: p.Tags.Select(t => t.Name).
            case MethodCallExpression { Method.Name: nameof(Queryable.Select) } select:
                return RewriteSelect(select, parameter, element);

            case MemberExpression member:
                return Expression.Call(
                    JsonReader.ValueMethod.MakeGenericMethod(member.Type),
                    element,
                    Path(member, parameter));

            case ParameterExpression p when p == parameter:
                return Expression.Call(JsonReader.ObjectMethod.MakeGenericMethod(p.Type), element);

            default:
                throw FieldPath.Computation(node);
        }
    }

    private static Expression RewriteSelect(
        MethodCallExpression select,
        ParameterExpression parameter,
        ParameterExpression element)
    {
        var source = select.Arguments.Count > 1 ? select.Arguments[0] : select.Object!;
        var inner = select.Arguments[^1] switch
        {
            UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression quoted } => quoted,
            LambdaExpression lambda => lambda,
            _ => throw FieldPath.Computation(select)
        };

        var item = Expression.Parameter(typeof(JsonElement), "item");
        var body = Rewrite(inner.Body, inner.Parameters[0], item);

        if (body.Type != inner.ReturnType)
            body = Expression.Convert(body, inner.ReturnType);

        var read = Expression.Call(
            JsonReader.ListMethod.MakeGenericMethod(inner.ReturnType),
            element,
            Path(source, parameter),
            Expression.Lambda(
                typeof(Func<,>).MakeGenericType(typeof(JsonElement), inner.ReturnType), body, item));

        // ReadList hands back IEnumerable<T>. Anything narrower in the projection — an IQueryable
        // member, say — would have needed an operator the selection set could not carry anyway.
        return select.Type.IsAssignableFrom(read.Type) ? read : throw FieldPath.Computation(select);
    }

    private static ConstantExpression Path(Expression expression, ParameterExpression parameter)
        => Expression.Constant(FieldPath.Resolve(expression, parameter).ToArray(), typeof(string[]));
}
