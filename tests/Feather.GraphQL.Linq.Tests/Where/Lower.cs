using System.Buffers;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Feather.GraphQL.Linq.Document;
using Feather.GraphQL.Linq.Expressions;
using Feather.GraphQL.Linq.Filtering;

namespace Feather.GraphQL.Linq.Tests.Where;

/// <summary>
/// Lowers a predicate to its filter JSON.
/// </summary>
/// <remarks>
/// Straight through the translator. It used to go through <c>ToGraphQLFilter()</c>, which was the
/// public lowering surface until consumers gained the ability to print whole documents and it
/// stopped earning its place. Lowering is the subject of these tests either way, so the change is
/// to how they reach it and not to what they assert.
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
        => Chain(source => Queryable.Where(source, predicate));

    /// <summary>Lowers a composed chain, for cases where the composition itself is the subject.</summary>
    public static string Chain(Func<IQueryable<Person>, IQueryable<Person>> compose)
        => Of(compose(People).Expression);

    /// <summary>Lowers any chain's merged predicate, whatever element type it queries.</summary>
    public static string Of(Expression chain)
    {
        var predicate = QueryChain.Parse(chain).MergedPredicate();
        var filter = new FilterTranslator(HotChocolateFilterProvider.Instance).Translate(predicate);

        return Render(filter);
    }

    /// <summary>Writes a lowered value the way the transport does.</summary>
    private static string Render(GqlValue? value)
    {
        if (value is null)
            return "null";

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
            value.WriteTo(writer);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
