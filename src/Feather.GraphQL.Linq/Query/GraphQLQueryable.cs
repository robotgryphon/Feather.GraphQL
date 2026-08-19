using System.Collections;
using System.Linq.Expressions;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// Entry point for composing a GraphQL query with LINQ.
/// </summary>
/// <remarks>
/// A queryable is an unexecuted expression, not a connection — it holds no
/// <see cref="HttpClient"/> and performs no I/O. Sending it is a separate, explicit step:
/// <c>ToArrayAsync(client)</c> or <c>ToListAsync(client)</c>.
/// </remarks>
[PublicAPI]
public static class GraphQLQueryable
{
    /// <summary>Starts a query over a type carrying <c>[GenerateQueryable]</c>.</summary>
    public static IQueryable<T> For<T>() => new GraphQLQueryable<T>();
}

/// <inheritdoc cref="GraphQLQueryable"/>
internal sealed class GraphQLQueryable<T> : IQueryable<T>, IOrderedQueryable<T>
{
    public Type ElementType => typeof(T);
    public Expression Expression { get; }
    public IQueryProvider Provider { get; }

    public GraphQLQueryable()
    {
        Provider = new GraphQLQueryProvider();
        Expression = Expression.Constant(this);
    }

    internal GraphQLQueryable(IQueryProvider provider, Expression expression)
    {
        Provider = provider;
        Expression = expression;
    }

    // Enumerating means executing, and executing means I/O that has no synchronous form.
    public IEnumerator<T> GetEnumerator()
        => throw new GraphQLTranslationException("FGQL001",
            "A GraphQL queryable cannot be enumerated. Await ToArrayAsync() or ToListAsync() and "
            + "enumerate the result — or call ToGraphQLRequest() to translate without sending.");

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Captures composition and refuses execution.
/// </summary>
/// <remarks>
/// Every terminal reads <see cref="IQueryable.Expression"/> directly, so
/// <see cref="IQueryProvider.Execute"/> is never called. It stays unimplemented on purpose:
/// <see cref="IQueryProvider"/> has no async member, so honouring it would mean blocking on a
/// network call from inside <c>ToList()</c>. On Blazor WebAssembly that deadlocks outright — the
/// response cannot arrive until the thread returns to the browser's event loop — and on Blazor
/// Server it holds a thread-pool thread per render. Callers await a terminal instead.
/// </remarks>
internal sealed class GraphQLQueryProvider : IQueryProvider
{
    public IQueryable CreateQuery(Expression expression)
    {
        var elementType = expression.Type.GetInterfaces().Append(expression.Type)
            .First(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IQueryable<>))
            .GetGenericArguments()[0];

        return (IQueryable)Activator.CreateInstance(
            typeof(GraphQLQueryable<>).MakeGenericType(elementType), this, expression)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new GraphQLQueryable<TElement>(this, expression);

    public object Execute(Expression expression) => throw NotExecutable();

    public TResult Execute<TResult>(Expression expression) => throw NotExecutable();

    private static GraphQLTranslationException NotExecutable()
        => new("FGQL001",
            "A GraphQL queryable does not execute synchronously. Await ToArrayAsync() or "
            + "ToListAsync(), or call ToGraphQLRequest() to translate without sending.");
}
