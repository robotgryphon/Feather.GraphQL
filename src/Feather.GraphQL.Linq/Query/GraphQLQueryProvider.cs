using System.Linq.Expressions;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// The provider a chain is composed against, and which never runs one.
/// </summary>
/// <remarks>
/// <para>
/// A chain exists to be read by the compiler, not executed. Every call to a
/// <c>[GraphQLQuery]</c> method is replaced by the request its chain compiles to, so the body
/// holding the chain is unreachable — and this provider, which that body would have composed
/// against, is unreachable with it.
/// </para>
/// <para>
/// It is still a real <see cref="IQueryProvider"/> because <see cref="IQueryable{T}"/> requires
/// one, and composing the chain is how the chain gets its type. What it no longer is, is an
/// engine: there is no translator behind it, no expression is walked, and nothing is compiled at
/// run time. Reaching any member below means a call was not replaced, which is a bug in the build
/// rather than a slower path — so it says so instead of quietly doing the work.
/// </para>
/// </remarks>
internal sealed class GraphQLQueryProvider(GraphQLQueryOptions options) : IQueryProvider
{
    /// <summary>The options the chain was created with, which the compiler reads from the call.</summary>
    public GraphQLQueryOptions Options { get; } = options;

    /// <summary>
    /// Composes, which is all a chain ever needed.
    /// </summary>
    /// <remarks>
    /// Typed only. The untyped overload is what
    /// <c>Activator.CreateInstance(typeof(GraphQLQueryable&lt;&gt;).MakeGenericType(...))</c> used
    /// to serve, and a generic instantiation made at run time is exactly what an AOT build cannot
    /// see — so it is refused rather than made to work.
    /// </remarks>
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new GraphQLQueryable<TElement>(this, expression);

    /// <inheritdoc cref="CreateQuery{TElement}"/>
    public IQueryable CreateQuery(Expression expression) => throw GraphQLTranslationException.NotCompiled();

    public object Execute(Expression expression) => throw GraphQLTranslationException.NotCompiled();

    public TResult Execute<TResult>(Expression expression) => throw GraphQLTranslationException.NotCompiled();

    /// <summary>What every terminal reaches, and none of them should.</summary>
    public IReadOnlyList<TElement> ExecuteSequence<TElement>(Expression expression)
        => throw GraphQLTranslationException.NotCompiled();

    /// <inheritdoc cref="ExecuteSequence{TElement}"/>
    public ValueTask<IReadOnlyList<TElement>> ExecuteSequenceAsync<TElement>(
        Expression expression,
        CancellationToken cancellationToken)
        => throw GraphQLTranslationException.NotCompiled();

    /// <inheritdoc cref="ExecuteSequence{TElement}"/>
    public ValueTask<TResult> ExecuteAsync<TResult>(
        Expression expression,
        CancellationToken cancellationToken)
        => throw GraphQLTranslationException.NotCompiled();

}
