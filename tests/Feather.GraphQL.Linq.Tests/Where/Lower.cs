using System.Linq.Expressions;
using Feather.GraphQL.Linq.Filtering;

namespace Feather.GraphQL.Linq.Tests.Where;

/// <summary>
/// Lowers a predicate to its filter JSON.
/// </summary>
/// <remarks>
/// An empty array is enough to carry an expression tree — §4 lowering needs no provider, no
/// attributes and no queryable of ours, which is the whole point of the extensions working over
/// any <see cref="IQueryable{T}"/>.
/// </remarks>
internal static class Lower
{
    public static IQueryable<Person> People => Array.Empty<Person>().AsQueryable();

    /// <summary>Lowers a single predicate.</summary>
    /// <remarks>
    /// Qualified deliberately: with <c>Feather.GraphQL.Linq.Filtering</c> imported, passing a
    /// prebuilt <see cref="Expression{TDelegate}"/> to <c>Where</c> is ambiguous between LINQ's
    /// own and this library's filter-shape overload.
    /// </remarks>
    public static string Where(Expression<Func<Person, bool>> predicate)
        => Queryable.Where(People, predicate).ToGraphQLFilter()?.ToJsonString() ?? "null";

    /// <summary>Lowers a composed chain, for cases where the composition itself is the subject.</summary>
    public static string Chain(Func<IQueryable<Person>, IQueryable<Person>> compose)
        => compose(People).ToGraphQLFilter()?.ToJsonString() ?? "null";
}
