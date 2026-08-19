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
    public static string Where(Expression<Func<Person, bool>> predicate)
        => People.Where(predicate).ToGraphQLFilter()?.ToJsonString() ?? "null";

    /// <summary>Lowers a composed chain, for cases where the composition itself is the subject.</summary>
    public static string Chain(Func<IQueryable<Person>, IQueryable<Person>> compose)
        => compose(People).ToGraphQLFilter()?.ToJsonString() ?? "null";
}
