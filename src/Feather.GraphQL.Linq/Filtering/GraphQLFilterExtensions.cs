using System.Text.Json.Nodes;
using Feather.GraphQL.Linq.Expressions;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Filtering;

/// <summary>
/// Lowers a LINQ chain to GraphQL field arguments. Works over <em>any</em>
/// <see cref="IQueryable{T}"/> — an EF <c>DbSet</c>, a <c>List&lt;T&gt;.AsQueryable()</c>, or this
/// library's own queryable — because lowering needs only an expression tree and a field-name map.
/// </summary>
/// <remarks>
/// Deliberately not in an auto-imported namespace: an extension on <see cref="IQueryable{T}"/>
/// otherwise appears on every <c>DbSet</c> in a solution.
/// </remarks>
[PublicAPI]
public static class GraphQLFilterExtensions
{
    extension<T>(IQueryable<T> source)
    {
        /// <summary>
        /// Lowers the chain's <c>Where</c> clauses to a filter input object, ready to drop into
        /// the request's variables payload. Multiple <c>Where</c> calls merge with <c>&amp;&amp;</c>.
        /// </summary>
        /// <returns>The filter object, or null when the chain has no predicate.</returns>
        /// <exception cref="GraphQLTranslationException">
        /// The chain contains operators this method does not consume, or a predicate that has no
        /// filter equivalent. Unconsumed operators throw rather than being ignored — a dropped
        /// <c>Take</c> would surface as missing data, not as an error.
        /// </exception>
        public JsonObject? ToGraphQLFilter(IFilterTranslationProvider? provider = null)
        {
            ArgumentNullException.ThrowIfNull(source);

            var chain = QueryChain.Parse(source.Expression);
            RejectUnconsumed(chain, nameof(ToGraphQLFilter), ordering: true, paging: true, projection: true);

            return new FilterTranslator(provider ?? HotChocolateFilterProvider.Instance)
                .Translate(chain.MergedPredicate());
        }

        /// <summary>
        /// Lowers the chain's ordering operators to a sort input array, in chain order.
        /// </summary>
        /// <returns>The sort array, or null when the chain is unordered.</returns>
        public JsonArray? ToGraphQLSort(IFilterTranslationProvider? provider = null)
        {
            ArgumentNullException.ThrowIfNull(source);

            var chain = QueryChain.Parse(source.Expression);
            RejectUnconsumed(chain, nameof(ToGraphQLSort), filter: true, paging: true, projection: true);

            return new FilterTranslator(provider ?? HotChocolateFilterProvider.Instance)
                .TranslateOrdering(chain.Ordering);
        }

        /// <summary>
        /// Lowers filtering, ordering and paging together — the "consume everything" form, for
        /// callers proxying a whole query upstream.
        /// </summary>
        public GraphQLFieldArguments ToGraphQLArguments(IFilterTranslationProvider? provider = null)
        {
            ArgumentNullException.ThrowIfNull(source);

            var chain = QueryChain.Parse(source.Expression);
            RejectUnconsumed(chain, nameof(ToGraphQLArguments), projection: true);

            var translator = new FilterTranslator(provider ?? HotChocolateFilterProvider.Instance);

            return new GraphQLFieldArguments
            {
                Where = translator.Translate(chain.MergedPredicate()),
                Order = translator.TranslateOrdering(chain.Ordering),
                Skip = chain.Skip,
                Take = chain.Take
            };
        }
    }

    private static void RejectUnconsumed(
        QueryChain chain,
        string method,
        bool filter = false,
        bool ordering = false,
        bool paging = false,
        bool projection = false)
    {
        if (filter && chain.HasFilter)
            throw Unconsumed(method, "Where", "ToGraphQLFilter() or ToGraphQLArguments()");

        if (ordering && chain.HasOrdering)
            throw Unconsumed(method, "OrderBy", "ToGraphQLSort() or ToGraphQLArguments()");

        if (paging && chain.HasPaging)
            throw Unconsumed(method, "Skip/Take", "ToGraphQLArguments()");

        if (projection && chain.Projection is not null)
            throw Unconsumed(method, "Select", "ToGraphQLQuery()");
    }

    private static GraphQLTranslationException Unconsumed(string method, string ignored, string alternative)
        => new("FGQL001",
            $"{method}() does not consume '{ignored}', and would have silently dropped it. Use {alternative}.");
}
