using System.Linq.Expressions;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// Async counterparts to LINQ's terminals, for callers who would rather not block a thread on
/// an HTTP round trip.
/// </summary>
/// <remarks>
/// The same shape EF Core uses, and for the same reason: <see cref="IQueryable{T}"/> has no
/// async contract, so the async terminal has to be an extension that reaches the provider
/// directly. Each one builds the expression its sync twin would have built, so
/// <c>FirstAsync()</c> and <c>First()</c> translate to byte-identical documents.
/// </remarks>
[PublicAPI]
public static class GraphQLAsyncQueryableExtensions
{
    extension<T>(IQueryable<T> source)
    {
        /// <summary>
        /// Exposes the query as an <see cref="IAsyncEnumerable{T}"/> for <c>await foreach</c>.
        /// </summary>
        /// <remarks>
        /// Needed because <see cref="IQueryable{T}"/> does not itself declare
        /// <c>GetAsyncEnumerator</c>, so the static type has to say so — the same reason EF Core
        /// has this method.
        /// </remarks>
        public IAsyncEnumerable<T> AsAsyncEnumerable()
        {
            ArgumentNullException.ThrowIfNull(source);

            return source as IAsyncEnumerable<T>
                ?? throw new GraphQLTranslationException("FGQL019",
                    $"'{source.GetType().Name}' is not a GraphQL queryable. Async enumeration "
                    + "requires one created with GraphQLQueryable.For or CreateQueryable.");
        }

        public async Task<List<T>> ToListAsync(CancellationToken cancellationToken = default)
            => [.. await Sequence(source, cancellationToken).ConfigureAwait(false)];

        public async Task<T[]> ToArrayAsync(CancellationToken cancellationToken = default)
            => [.. await Sequence(source, cancellationToken).ConfigureAwait(false)];

        public Task<T> FirstAsync(CancellationToken cancellationToken = default)
            => Scalar<T, T>(source, nameof(Queryable.First), null, cancellationToken);

        public Task<T> FirstAsync(Expression<Func<T, bool>> predicate,
            CancellationToken cancellationToken = default)
            => Scalar<T, T>(source, nameof(Queryable.First), predicate, cancellationToken);

        public Task<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
            => Scalar<T, T?>(source, nameof(Queryable.FirstOrDefault), null, cancellationToken);

        public Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate,
            CancellationToken cancellationToken = default)
            => Scalar<T, T?>(source, nameof(Queryable.FirstOrDefault), predicate, cancellationToken);

        public Task<T> SingleAsync(CancellationToken cancellationToken = default)
            => Scalar<T, T>(source, nameof(Queryable.Single), null, cancellationToken);

        public Task<T> SingleAsync(Expression<Func<T, bool>> predicate,
            CancellationToken cancellationToken = default)
            => Scalar<T, T>(source, nameof(Queryable.Single), predicate, cancellationToken);

        public Task<T?> SingleOrDefaultAsync(CancellationToken cancellationToken = default)
            => Scalar<T, T?>(source, nameof(Queryable.SingleOrDefault), null, cancellationToken);

        public Task<T?> SingleOrDefaultAsync(Expression<Func<T, bool>> predicate,
            CancellationToken cancellationToken = default)
            => Scalar<T, T?>(source, nameof(Queryable.SingleOrDefault), predicate, cancellationToken);

        /// <summary>Reads backwards with cursor paging's <c>last:</c>; FGQL015 without it.</summary>
        public Task<T> LastAsync(CancellationToken cancellationToken = default)
            => Scalar<T, T>(source, nameof(Queryable.Last), null, cancellationToken);

        /// <summary>Reads backwards with cursor paging's <c>last:</c>; FGQL015 without it.</summary>
        public Task<T?> LastOrDefaultAsync(CancellationToken cancellationToken = default)
            => Scalar<T, T?>(source, nameof(Queryable.LastOrDefault), null, cancellationToken);

        public Task<bool> AnyAsync(CancellationToken cancellationToken = default)
            => Scalar<T, bool>(source, nameof(Queryable.Any), null, cancellationToken);

        public Task<bool> AnyAsync(Expression<Func<T, bool>> predicate,
            CancellationToken cancellationToken = default)
            => Scalar<T, bool>(source, nameof(Queryable.Any), predicate, cancellationToken);

        /// <summary>Reads the connection's <c>totalCount</c>; FGQL009 on an un-paged field.</summary>
        public Task<int> CountAsync(CancellationToken cancellationToken = default)
            => Scalar<T, int>(source, nameof(Queryable.Count), null, cancellationToken);

        /// <summary>Reads the connection's <c>totalCount</c>; FGQL009 on an un-paged field.</summary>
        public Task<int> CountAsync(Expression<Func<T, bool>> predicate,
            CancellationToken cancellationToken = default)
            => Scalar<T, int>(source, nameof(Queryable.Count), predicate, cancellationToken);

        /// <summary>Reads the connection's <c>totalCount</c>; FGQL009 on an un-paged field.</summary>
        public Task<long> LongCountAsync(CancellationToken cancellationToken = default)
            => Scalar<T, long>(source, nameof(Queryable.LongCount), null, cancellationToken);

        /// <summary>Reads the connection's <c>totalCount</c>; FGQL009 on an un-paged field.</summary>
        public Task<long> LongCountAsync(Expression<Func<T, bool>> predicate,
            CancellationToken cancellationToken = default)
            => Scalar<T, long>(source, nameof(Queryable.LongCount), predicate, cancellationToken);
    }

    private static ValueTask<IReadOnlyList<T>> Sequence<T>(IQueryable<T> source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Provider(source).ExecuteSequenceAsync<T>(source.Expression, cancellationToken);
    }

    private static Task<TResult> Scalar<T, TResult>(
        IQueryable<T> source,
        string method,
        Expression<Func<T, bool>>? predicate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var call = predicate is null
            ? Expression.Call(typeof(Queryable), method, [typeof(T)], source.Expression)
            : Expression.Call(typeof(Queryable), method, [typeof(T)], source.Expression,
                Expression.Quote(predicate));

        return Provider(source).ExecuteAsync<TResult>(call, cancellationToken).AsTask();
    }

    /// <summary>
    /// The async terminals only mean anything over this library's provider — over an EF or
    /// in-memory queryable they would silently do something else.
    /// </summary>
    private static GraphQLQueryProvider Provider<T>(IQueryable<T> source)
        => source.Provider as GraphQLQueryProvider
            ?? throw new GraphQLTranslationException("FGQL019",
                $"'{source.Provider.GetType().Name}' is not a GraphQL query provider. The async "
                + "terminals require a queryable created with GraphQLQueryable.For or "
                + "CreateQueryable.");
}
