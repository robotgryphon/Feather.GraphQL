using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using Feather.GraphQL.Linq.Document;
using Feather.GraphQL.Linq.Expressions;
using Feather.GraphQL.Linq.Metadata;

namespace Feather.GraphQL.Linq.Filtering;

/// <summary>
/// Lowers a predicate expression to a server filter input object.
/// </summary>
/// <remarks>
/// The walk only ever sees member accesses on the lambda parameter and constants, because
/// <see cref="PartialEvaluator"/> has already collapsed everything else. Anything it does not
/// recognise throws rather than degrading — decision #2 of the design: no silent client-side
/// evaluation, ever.
/// </remarks>
internal sealed class FilterTranslator(IFilterTranslationProvider provider)
{
    public GqlObject? Translate(LambdaExpression? predicate)
    {
        if (predicate is null)
            return null;

        var body = PartialEvaluator.Reduce(predicate.Body);
        return (GqlObject)Normalize(Lower(body, predicate.Parameters[0], negated: false))!;
    }

    public GqlList? TranslateOrdering(IReadOnlyList<(LambdaExpression Key, bool Descending)> ordering)
    {
        if (ordering.Count == 0)
            return null;

        var order = new GqlList();
        foreach (var (key, descending) in ordering)
        {
            var key_ = ResolvePath(key.Body, key.Parameters[0])
                ?? throw GraphQLTranslationException.BadOrderingKey(
                    $"'{key.Body}' is not a member of the element type", key.Body);

            order.Items.Add(Nest(key_.Path,
                new GqlScalar(new GqlEnumValue(descending ? provider.Descending : provider.Ascending))));
        }

        return order;
    }

    private GqlObject Lower(Expression node, ParameterExpression parameter, bool negated)
    {
        switch (node)
        {
            case UnaryExpression { NodeType: ExpressionType.Not } not:
                return Lower(not.Operand, parameter, !negated);

            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                return Lower(convert.Operand, parameter, negated);

            // De Morgan: negation is pushed to the leaves so it can pick the negated operation
            // name, rather than needing a `not:` wrapper the server may not have.
            case BinaryExpression { NodeType: ExpressionType.AndAlso } and:
                return negated
                    ? Any(provider.Or, Lower(and.Left, parameter, true), Lower(and.Right, parameter, true))
                    : Combine(Lower(and.Left, parameter, false), Lower(and.Right, parameter, false));

            case BinaryExpression { NodeType: ExpressionType.OrElse } or:
                return negated
                    ? Combine(Lower(or.Left, parameter, true), Lower(or.Right, parameter, true))
                    : Any(provider.Or, Lower(or.Left, parameter, false), Lower(or.Right, parameter, false));

            case BinaryExpression binary when IsComparison(binary.NodeType):
                return Comparison(binary, parameter, negated);

            case MethodCallExpression call:
                return MethodCall(call, parameter, negated);

            // A bare boolean member: `p.IsActive` / `!p.IsActive`.
            case MemberExpression member when member.Type == typeof(bool) || member.Type == typeof(bool?):
            {
                var boolean = ResolvePath(member, parameter)
                    ?? throw GraphQLTranslationException.NotLowerable(
                        $"'{member}' is not a member of the filtered element", member);

                return Leaf(boolean.Path, provider.Equal, new GqlScalar(!negated));
            }

            case ConstantExpression constant:
                throw GraphQLTranslationException.NotLowerable(
                    $"a predicate that ignores its parameter and is always {constant.Value}", constant);

            default:
                throw GraphQLTranslationException.UnsupportedPredicate(
                    $"'{node.NodeType}' has no filter equivalent", node);
        }
    }

    private GqlObject Comparison(BinaryExpression binary, ParameterExpression parameter, bool negated)
    {
        // Either orientation is legal: `p.Age > 30` and `30 < p.Age` mean the same thing.
        var (path, value, flipped) = Orient(binary, parameter);

        var nodeType = flipped ? Flip(binary.NodeType) : binary.NodeType;
        string operation = nodeType switch
        {
            ExpressionType.Equal => negated ? provider.NotEqual : provider.Equal,
            ExpressionType.NotEqual => negated ? provider.Equal : provider.NotEqual,
            ExpressionType.GreaterThan => negated ? provider.LessThanOrEqual : provider.GreaterThan,
            ExpressionType.GreaterThanOrEqual => negated ? provider.LessThan : provider.GreaterThanOrEqual,
            ExpressionType.LessThan => negated ? provider.GreaterThanOrEqual : provider.LessThan,
            ExpressionType.LessThanOrEqual => negated ? provider.GreaterThan : provider.LessThanOrEqual,
            _ => throw GraphQLTranslationException.UnsupportedPredicate(
                $"comparison '{binary.NodeType}'", binary)
        };

        return Leaf(path, operation, value);
    }

    private (IReadOnlyList<string> Path, GqlValue? Value, bool Flipped) Orient(
        BinaryExpression binary, ParameterExpression parameter)
    {
        if (ResolvePath(binary.Left, parameter) is { } left)
            return (left.Path, Value(Constant(binary.Right, binary), left.LeafType), false);

        if (ResolvePath(binary.Right, parameter) is { } right)
            return (right.Path, Value(Constant(binary.Left, binary), right.LeafType), true);

        // A side that reads the parameter but is not a plain member chain is an unsupported
        // *expression* (FGQL002), not an un-mappable clause (FGQL003). The distinction matters:
        // one says "rewrite this predicate", the other says "this field has no filter".
        if (ReferencesParameter(binary.Left, parameter) || ReferencesParameter(binary.Right, parameter))
            throw GraphQLTranslationException.UnsupportedPredicate(
                $"'{binary}' computes over the filtered element instead of comparing a member to a value",
                binary);

        throw GraphQLTranslationException.NotLowerable(
            $"neither side of '{binary}' is a member of the filtered element", binary);
    }

    private static bool ReferencesParameter(Expression expression, ParameterExpression parameter)
        => new ParameterFinder(parameter).Found(expression);

    private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        private bool _found;

        public bool Found(Expression expression)
        {
            Visit(expression);
            return _found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter)
                _found = true;

            return base.VisitParameter(node);
        }
    }

    private GqlObject MethodCall(MethodCallExpression call, ParameterExpression parameter, bool negated)
    {
        // string.Contains / StartsWith / EndsWith — instance calls on a member of the element.
        if (call.Object is not null && call.Object.Type == typeof(string) && call.Arguments.Count == 1)
        {
            var text = ResolvePath(call.Object, parameter)
                ?? throw GraphQLTranslationException.NotLowerable(
                    $"'{call.Object}' is not a member of the filtered element", call);

            string operation = call.Method.Name switch
            {
                nameof(string.Contains) => negated ? provider.NotContains : provider.Contains,
                nameof(string.StartsWith) => negated ? provider.NotStartsWith : provider.StartsWith,
                nameof(string.EndsWith) => negated ? provider.NotEndsWith : provider.EndsWith,
                _ => throw GraphQLTranslationException.UnsupportedPredicate(
                    $"string method '{call.Method.Name}'", call)
            };

            return Leaf(text.Path, operation, Value(Constant(call.Arguments[0], call), typeof(string)));
        }

        if (call.Method.Name is nameof(Enumerable.Contains))
            return ContainsCall(call, parameter, negated);

        if (call.Method.Name is nameof(Enumerable.Any) or nameof(Enumerable.All))
            return Quantifier(call, parameter, negated);

        throw GraphQLTranslationException.UnsupportedPredicate(
            $"method '{call.Method.DeclaringType?.Name}.{call.Method.Name}'", call);
    }

    /// <summary>
    /// Two distinct shapes share the name <c>Contains</c>: a constant collection containing a
    /// member (<c>in</c>), and a member collection containing a constant (<c>some { eq }</c>).
    /// </summary>
    private GqlObject ContainsCall(MethodCallExpression call, ParameterExpression parameter, bool negated)
    {
        var (source, item) = call.Object is not null
            ? (call.Object, call.Arguments[0])
            : (call.Arguments[0], call.Arguments[1]);

        // On .NET 10 `array.Contains(x)` binds to MemoryExtensions over a ReadOnlySpan, so the
        // collection arrives wrapped in an implicit span conversion.
        source = StripSpanConversion(source);

        if (ResolvePath(item, parameter) is { } itemPath)
            return Leaf(itemPath.Path, negated ? provider.NotIn : provider.In,
                Value(Constant(source, call), itemPath.LeafType));

        if (ResolvePath(source, parameter) is { } sourcePath)
            return Nest(sourcePath.Path, new GqlObject(
                negated ? provider.None : provider.Some,
                new GqlObject(provider.Equal, Value(Constant(item, call), ElementType(sourcePath.LeafType)))));

        throw GraphQLTranslationException.NotLowerable(
            $"neither side of '{call}' is a member of the filtered element", call);
    }

    private GqlObject Quantifier(MethodCallExpression call, ParameterExpression parameter, bool negated)
    {
        var source = StripSpanConversion(call.Object ?? call.Arguments[0]);
        var resolved = ResolvePath(source, parameter)
            ?? throw GraphQLTranslationException.NotLowerable(
                $"'{source}' is not a collection member of the filtered element", call);
        var path = resolved.Path;

        bool isAny = call.Method.Name == nameof(Enumerable.Any);

        // `Any()` with no predicate asks only whether the collection is non-empty.
        if (call.Arguments.Count < 2 && call.Object is null || call.Object is not null && call.Arguments.Count == 0)
            return Nest(path, new GqlObject(negated ? provider.None : provider.Some, new GqlObject()));

        var inner = call.Arguments[^1] switch
        {
            UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression lambda } => lambda,
            LambdaExpression lambda => lambda,
            var other => throw GraphQLTranslationException.UnsupportedPredicate(
                $"expected a lambda inside '{call.Method.Name}' but found '{other.NodeType}'", call)
        };

        // `!Any(p)` is `none`, which is a distinct operation rather than a negated body — so the
        // negation is consumed here and the body lowers positively.
        string quantifier = (isAny, negated) switch
        {
            (true, false) => provider.Some,
            (true, true) => provider.None,
            (false, false) => provider.All,
            (false, true) => throw GraphQLTranslationException.UnsupportedPredicate(
                "negated All() has no filter equivalent; rewrite as Any() with the negated predicate", call)
        };

        return Nest(path, new GqlObject(
            quantifier,
            Lower(PartialEvaluator.Reduce(inner.Body), inner.Parameters[0], negated: false)));
    }

    /// <summary>A resolved member chain: the GraphQL field path, and the CLR type at its leaf.</summary>
    private readonly record struct MemberPath(List<string> Path, Type LeafType);

    /// <summary>
    /// Walks a member chain back to the lambda parameter, mapping each hop through the
    /// declaring type's field metadata. Returns null when the chain does not root at the
    /// parameter — which is how a constant subtree is told apart from a member access.
    /// </summary>
    private static MemberPath? ResolvePath(Expression expression, ParameterExpression parameter)
    {
        var path = new List<string>();
        var current = expression;
        Type? leafType = null;

        while (true)
        {
            switch (current)
            {
                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                    current = convert.Operand;
                    continue;

                case MemberExpression member:
                {
                    var declaring = member.Member.DeclaringType
                        ?? throw GraphQLTranslationException.NotLowerable($"'{member}' has no declaring type", member);

                    var metadata = ReflectionTypeMetadata.For(declaring);
                    if (!metadata.TryGetField(member.Member.Name, out var field))
                        return null;

                    if (field.IsIgnored)
                        throw GraphQLTranslationException.IgnoredMember(field.ClrName, declaring);

                    path.Insert(0, field.FieldName);
                    leafType ??= field.ClrType;
                    current = member.Expression
                        ?? throw GraphQLTranslationException.NotLowerable($"'{member}' is static", member);
                    continue;
                }

                case ParameterExpression p when p == parameter:
                    return path.Count > 0 ? new MemberPath(path, leafType!) : null;

                default:
                    return null;
            }
        }
    }

    private static object? Constant(Expression expression, Expression context)
        => PartialEvaluator.Reduce(expression) is ConstantExpression constant
            ? constant.Value
            : throw GraphQLTranslationException.UnsupportedPredicate(
                $"'{expression}' is not a constant and cannot be sent as a filter value", context);

    private static GqlObject Leaf(IReadOnlyList<string> path, string operation, GqlValue? value)
        => Nest(path, new GqlObject(operation, value));

    private static GqlObject Nest(IReadOnlyList<string> path, GqlValue? leaf)
    {
        var node = leaf;
        for (int i = path.Count - 1; i >= 0; i--)
            node = new GqlObject(path[i], node);

        return (GqlObject)node!;
    }

    /// <summary>
    /// Merges two conjuncts into one object, falling back to an explicit <c>and:</c> array when
    /// they collide. Merging is what makes <c>Where(a).Where(b)</c> read naturally; the array is
    /// what keeps <c>p.Age &gt; 1 &amp;&amp; p.Age &lt; 9</c> correct.
    /// </summary>
    private GqlObject Combine(GqlObject left, GqlObject right)
        => TryMerge(left, right, out var merged) ? merged : Any(provider.And, left, right);

    private static bool TryMerge(GqlObject left, GqlObject right, out GqlObject merged)
    {
        merged = new GqlObject();

        // Moved rather than cloned. A GqlValue has no parent, so a value can be in two shapes at
        // once — which a JsonNode cannot, and which is why this used to deep-clone every entry.
        foreach (var (key, value) in left.Fields)
            merged.Set(key, value);

        foreach (var (key, value) in right.Fields)
        {
            if (!merged.TryGet(key, out var existing))
            {
                merged.Set(key, value);
                continue;
            }

            // Both sides constrain the same field: mergeable only if they descend into disjoint
            // sub-objects. Two of the same operation on one field collide and need `and:`.
            if (existing is GqlObject a && value is GqlObject b && TryMerge(a, b, out var nested))
            {
                merged.Set(key, nested);
                continue;
            }

            merged = new GqlObject();
            return false;
        }

        return true;
    }

    private static GqlObject Any(string junction, GqlObject left, GqlObject right)
    {
        var branches = new GqlList();

        // Flatten a nested junction of the same kind so `a || b || c` is one array, not a tree.
        foreach (var operand in new[] { left, right })
        {
            if (operand.Count == 1 && operand.TryGet(junction, out var existing) && existing is GqlList nested)
                branches.Items.AddRange(nested.Items);
            else
                branches.Items.Add(operand);
        }

        return new GqlObject(junction, branches);
    }

    /// <summary>
    /// Collapses a junction of same-field, same-operation equality tests into a set operation:
    /// <c>or</c> of <c>eq</c> becomes <c>in</c>, <c>and</c> of <c>neq</c> becomes <c>nin</c>.
    /// </summary>
    /// <remarks>
    /// Runs as a pass over the finished object rather than inside the pairwise join, because
    /// <c>a || b || c</c> parses left-nested: collapsing eagerly would fold <c>a || b</c> into an
    /// <c>in</c> and then fail to see <c>c</c> as the same shape, yielding
    /// <c>or: [{in: [1,2]}, {eq: 3}]</c>. Normalising afterwards sees the whole array at once, and
    /// reaches junctions nested inside quantifiers for free.
    /// </remarks>
    private GqlValue? Normalize(GqlValue? node)
    {
        if (node is not GqlObject obj)
            return node;

        for (int i = 0; i < obj.Count; i++)
        {
            var (key, child) = obj[i];
            obj.Set(key, Normalize(child));
        }

        if (obj.Count != 1)
            return obj;

        var (junction, value) = obj[0];
        if (value is not GqlList branches || branches.Items.Count < 2)
            return obj;

        string? operation = junction == provider.Or ? provider.Equal
            : junction == provider.And ? provider.NotEqual
            : null;

        if (operation is null)
            return obj;

        List<string>? shared = null;
        var values = new List<GqlValue?>();

        foreach (var branch in branches.Items)
        {
            if (branch is not GqlObject candidate || !TryUnwrap(candidate, out var path, out string? op, out var leaf)
                || op != operation)
                return obj;

            if (shared is null)
                shared = path;
            else if (!shared.SequenceEqual(path, StringComparer.Ordinal))
                return obj;

            values.Add(leaf);
        }

        var set = new GqlList();
        set.Items.AddRange(values);

        return Leaf(shared!, operation == provider.Equal ? provider.In : provider.NotIn, set);
    }

    /// <summary>
    /// Unwraps a single-key object chain into its field path, terminal operation and value.
    /// Returns false for anything branching, which is what keeps the collapse conservative.
    /// </summary>
    private static bool TryUnwrap(
        GqlObject node,
        out List<string> path,
        out string? operation,
        out GqlValue? value)
    {
        path = [];
        operation = null;
        value = null;

        var current = node;
        while (true)
        {
            if (current.Count != 1)
                return false;

            var (key, child) = current[0];

            if (child is GqlObject nested)
            {
                path.Add(key);
                current = nested;
                continue;
            }

            operation = key;
            value = child;
            return path.Count > 0;
        }
    }

    private static bool IsComparison(ExpressionType type)
        => type is ExpressionType.Equal or ExpressionType.NotEqual
            or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
            or ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

    private static ExpressionType Flip(ExpressionType type)
        => type switch
        {
            ExpressionType.GreaterThan => ExpressionType.LessThan,
            ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
            ExpressionType.LessThan => ExpressionType.GreaterThan,
            ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
            _ => type
        };

    /// <summary>
    /// Unwraps the implicit <c>ReadOnlySpan&lt;T&gt;</c> conversion the compiler inserts for
    /// array-receiver calls, and explicit <c>AsSpan()</c> calls, back to the underlying collection.
    /// </summary>
    private static Expression StripSpanConversion(Expression expression)
        => expression switch
        {
            UnaryExpression { NodeType: ExpressionType.Convert } convert when convert.Type.IsByRefLike
                => StripSpanConversion(convert.Operand),
            // The span conversion surfaces as a user-defined op_Implicit call, not a Convert node.
            MethodCallExpression { Method.Name: "AsSpan" or "AsReadOnlySpan" or "op_Implicit" } call
                when call.Type.IsByRefLike
                => StripSpanConversion(call.Object ?? call.Arguments[0]),
            _ => expression
        };

    private static Type? ElementType(Type collectionType)
    {
        if (collectionType.IsArray)
            return collectionType.GetElementType();

        return collectionType.GetInterfaces().Append(collectionType)
            .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    /// <summary>
    /// Converts a CLR value to a JSON node. <paramref name="declaredType"/> is the member's
    /// declared type, which is how an enum survives: the compiler lowers
    /// <c>p.Status == Status.Active</c> to an <c>int</c> comparison, so the enum identity is only
    /// recoverable from the member, not from the value.
    /// </summary>
    /// <summary>
    /// Wraps a constant as a value, recovering an enum's identity on the way.
    /// </summary>
    /// <remarks>
    /// The declared type is how an enum survives: the compiler lowers <c>p.Kind == Kind.Complex</c>
    /// to an <c>int</c> comparison, so what it is can only be read off the member. Everything else
    /// stays exactly as the predicate supplied it, and is converted when it is written.
    /// </remarks>
    private static GqlValue? Value(object? value, Type? declaredType = null)
    {
        if (value is not null && declaredType is not null)
        {
            var target = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

            if (target.IsEnum && value is not Enum && value.GetType().IsPrimitive)
                value = Enum.ToObject(target, value);
        }

        return new GqlScalar(value);
    }


}
