using System.Linq.Expressions;
using System.Reflection;

namespace Feather.GraphQL.Linq.Expressions;

/// <summary>
/// Collapses every subtree that does not depend on the lambda parameter into a constant, so
/// the lowering walk only ever sees member accesses on the parameter and literal values.
/// </summary>
/// <remarks>
/// This is what turns a captured local into a value: the compiler rewrites <c>p.Name == name</c>
/// as a field read on a closure object, which is parameter-independent and therefore evaluated
/// here. Closure field reads are handled directly rather than through
/// <see cref="LambdaExpression.Compile()"/> — it is the overwhelmingly common case, and avoiding
/// the compile keeps the path allocation-light and AOT-safe.
/// </remarks>
internal static class PartialEvaluator
{
    public static Expression Reduce(Expression expression)
        => new Evaluator(Nominator.Nominate(expression)).Visit(expression)!;

    private sealed class Evaluator(HashSet<Expression> candidates) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node)
        {
            if (node is null)
                return null;

            if (node.NodeType == ExpressionType.Constant || !candidates.Contains(node))
                return base.Visit(node);

            return Expression.Constant(Evaluate(node), node.Type);
        }

        private static object? Evaluate(Expression node)
        {
            // Closure field/property read — the shape the compiler emits for a captured local.
            if (node is MemberExpression { Expression: ConstantExpression owner } member)
                return member.Member switch
                {
                    FieldInfo field => field.GetValue(owner.Value),
                    PropertyInfo property => property.GetValue(owner.Value),
                    _ => CompileAndInvoke(node)
                };

            // A static closure-free capture, e.g. a literal already folded by the compiler.
            if (node is MemberExpression { Expression: null } staticMember)
                return staticMember.Member switch
                {
                    FieldInfo field => field.GetValue(null),
                    PropertyInfo property => property.GetValue(null),
                    _ => CompileAndInvoke(node)
                };

            return CompileAndInvoke(node);
        }

        private static object? CompileAndInvoke(Expression node)
            => Expression.Lambda(node).Compile().DynamicInvoke();
    }

    /// <summary>
    /// Marks the maximal subtrees containing no <see cref="ParameterExpression"/>, which are
    /// exactly the subtrees whose value is known without a row to evaluate against.
    /// </summary>
    private sealed class Nominator : ExpressionVisitor
    {
        private readonly HashSet<Expression> _candidates = [];
        private bool _dependsOnParameter;

        public static HashSet<Expression> Nominate(Expression expression)
        {
            var nominator = new Nominator();
            nominator.Visit(expression);
            return nominator._candidates;
        }

        public override Expression? Visit(Expression? node)
        {
            if (node is null)
                return null;

            bool enclosing = _dependsOnParameter;
            _dependsOnParameter = false;

            base.Visit(node);

            if (!_dependsOnParameter)
            {
                // Lambdas are structure, not values: evaluating one would erase the body the
                // quantifier lowering (some/all/none) needs to read.
                //
                // Ref structs cannot be boxed, so they cannot be the result of an evaluated
                // subtree. This matters more than it sounds: on .NET 10 `array.Contains(x)`
                // binds to MemoryExtensions.Contains(ReadOnlySpan<T>, T) rather than
                // Enumerable.Contains, putting an implicit span conversion in the tree.
                if (node.NodeType is ExpressionType.Parameter or ExpressionType.Lambda
                    || node.Type.IsByRefLike)
                    _dependsOnParameter = true;
                else
                    _candidates.Add(node);
            }

            _dependsOnParameter |= enclosing;
            return node;
        }
    }
}
