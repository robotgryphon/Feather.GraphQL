using System.Linq.Expressions;
using System.Reflection;

namespace Feather.GraphQL.Linq.Expressions;

/// <summary>
/// Collapses every subtree that does not depend on the lambda parameter into a constant, so
/// the lowering walk only ever sees member accesses on the parameter and literal values.
/// </summary>
/// <remarks>
/// <para>
/// This is what turns a captured local into a value: the compiler rewrites <c>p.Name == name</c>
/// as a field read on a closure object, which is parameter-independent and therefore evaluated
/// here.
/// </para>
/// <para>
/// Subtrees are read directly — field and property reads, conversions, array literals, calls —
/// rather than through <see cref="LambdaExpression.Compile()"/>, which generates IL at runtime
/// and is the one thing NativeAOT cannot do at all. The compile survives only as a last resort
/// for shapes this does not recognise, and <see cref="CompiledSubtrees"/> counts how often that
/// happens so a test can hold it at zero.
/// </para>
/// </remarks>
internal static class PartialEvaluator
{
    private static int _compiled;

    /// <summary>
    /// How many subtrees have been evaluated by compiling them — the count of times this library
    /// has generated IL at runtime. Zero is the goal, and what the tests assert.
    /// </summary>
    internal static int CompiledSubtrees => Volatile.Read(ref _compiled);

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

        /// <summary>
        /// Reads a parameter-free subtree's value without generating code for it.
        /// </summary>
        /// <remarks>
        /// Recursive on purpose. A captured local is one field read, but a captured
        /// <em>field of a field</em> — <c>_options.Name</c> — is a chain, and the nominator marks
        /// the outermost node, so only a walk that descends reaches the closure at the bottom.
        /// </remarks>
        private static object? Evaluate(Expression node)
        {
            switch (node)
            {
                case ConstantExpression constant:
                    return constant.Value;

                case MemberExpression member:
                    return EvaluateMember(member);

                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked
                    or ExpressionType.TypeAs } convert:
                    return EvaluateConvert(convert);

                // `new[] { "Ada", "Grace" }.Contains(p.Name)` puts the whole array here.
                case NewArrayExpression { NodeType: ExpressionType.NewArrayInit } array:
                    return EvaluateArray(array);

                case MethodCallExpression call:
                    return EvaluateCall(call);

                case NewExpression creation when creation.Constructor is { } constructor:
                    return Invoke(() => constructor.Invoke(Arguments(creation.Arguments)));

                default:
                    return CompileAndInvoke(node);
            }
        }

        private static object? EvaluateMember(MemberExpression member)
        {
            // A static member has no instance; an instance one needs its owner evaluated first,
            // which is what makes a chain work.
            object? owner = member.Expression is null ? null : Evaluate(member.Expression);

            return member.Member switch
            {
                FieldInfo field => Invoke(() => field.GetValue(owner)),
                PropertyInfo property => Invoke(() => property.GetValue(owner)),
                _ => CompileAndInvoke(member)
            };
        }

        private static object? EvaluateConvert(UnaryExpression convert)
        {
            object? value = Evaluate(convert.Operand);

            // A user-defined conversion operator is a method like any other.
            if (convert.Method is { } method)
                return Invoke(() => method.Invoke(null, [value]));

            if (value is null)
                return null;

            var target = Nullable.GetUnderlyingType(convert.Type) ?? convert.Type;
            if (target.IsInstanceOfType(value))
                return value;

            // Numeric and enum widening, the only conversions left that change the value.
            return target.IsEnum || (target.IsPrimitive && value.GetType().IsPrimitive)
                ? Invoke(() => target.IsEnum
                    ? Enum.ToObject(target, value)
                    : System.Convert.ChangeType(value, target))
                : CompileAndInvoke(convert);
        }

        private static object EvaluateArray(NewArrayExpression array)
        {
            var elementType = array.Type.GetElementType()!;
            var values = Array.CreateInstance(elementType, array.Expressions.Count);

            for (int i = 0; i < array.Expressions.Count; i++)
                values.SetValue(Evaluate(array.Expressions[i]), i);

            return values;
        }

        private static object? EvaluateCall(MethodCallExpression call)
        {
            object? instance = call.Object is null ? null : Evaluate(call.Object);
            object?[] arguments = Arguments(call.Arguments);

            return Invoke(() => call.Method.Invoke(instance, arguments));
        }

        private static object?[] Arguments(IReadOnlyList<Expression> expressions)
        {
            var values = new object?[expressions.Count];
            for (int i = 0; i < expressions.Count; i++)
                values[i] = Evaluate(expressions[i]);

            return values;
        }

        /// <summary>
        /// Runs one reflective call, reporting what the member actually threw rather than the
        /// wrapper reflection puts around it.
        /// </summary>
        private static object? Invoke(Func<object?> read)
        {
            try
            {
                return read();
            }
            catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
            {
                throw inner;
            }
        }

        /// <summary>
        /// The last resort, and the only place this library still generates IL at runtime.
        /// Reached for a shape <see cref="Evaluate"/> does not recognise — a conditional, or an
        /// operator with no method behind it.
        /// </summary>
        private static object? CompileAndInvoke(Expression node)
        {
            Interlocked.Increment(ref _compiled);

            return Expression.Lambda(node).Compile().DynamicInvoke();
        }
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
