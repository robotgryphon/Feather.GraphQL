using System.Collections;
using System.Linq.Expressions;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// Entry point for composing a GraphQL query with LINQ.
/// </summary>
/// <remarks>
/// A queryable is an unexecuted expression, not a connection — it holds no
/// <see cref="HttpClient"/> and performs no I/O. Sending it is a separate, explicit step.
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

    // Enumerating would mean executing, and v1 produces requests rather than results.
    public IEnumerator<T> GetEnumerator()
        => throw new GraphQLTranslationException("FGQL001",
            "A GraphQL queryable cannot be enumerated. Call ToGraphQLRequest() to translate it, "
            + "or SendAsync() to translate and send it.");

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Captures composition and refuses execution.
/// </summary>
/// <remarks>
/// Every v1 terminal reads <see cref="IQueryable.Expression"/> directly, so
/// <see cref="IQueryProvider.Execute"/> is never called. That is also why v1 sidesteps
/// <see cref="IQueryable"/>'s lack of an async contract entirely: there is nothing to execute.
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
            "A GraphQL queryable does not execute. Call ToGraphQLRequest() to translate it, or "
            + "SendAsync() to translate and send it. Result operators such as First(), Count() and "
            + "ToList() arrive with the response system.");
}
