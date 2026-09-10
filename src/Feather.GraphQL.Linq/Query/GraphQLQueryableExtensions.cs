using Feather.GraphQL.Linq.Filtering;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// The translate-without-sending terminal, for inspecting or caching what a chain would ask for.
/// </summary>
/// <remarks>
/// Sending is not here: a queryable from <see cref="IGraphQLQueryableSource"/>
/// executes through its own terminals, the way any other LINQ provider does.
/// </remarks>
[PublicAPI]
public static class GraphQLQueryableExtensions
{
    extension<T>(IQueryable<T> source)
    {
        /// <summary>
        /// Translates the chain into a self-contained GraphQL document, with every argument
        /// written out exactly as the chain describes it. Pure — no client, no I/O, and the unit
        /// most worth reading.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the debugging form. What actually goes over the wire binds every argument to
        /// a variable, so the sent document says <c>where: $v0</c> and reveals nothing about the
        /// filter; here the values are in the text and the result can be pasted into a playground
        /// as-is.
        /// </para>
        /// <para>
        /// Because of that it is not the document to hash for APQ, or to send. Use
        /// <c>ToQueryPlan().Query</c> for the parameterized one.
        /// </para>
        /// </remarks>
        public string ToGraphQLQuery(IFilterTranslationProvider? provider = null)
        {
            ArgumentNullException.ThrowIfNull(source);

            return new GraphQLQueryTranslator(provider ?? HotChocolateFilterProvider.Instance)
                .TranslateInline(source.Expression);
        }

        /// <summary>
        /// Translates the chain into the document, its variables, and the shape of its answer —
        /// what a custom <see cref="Execution.IGraphQLQueryExecutor"/> needs in order to run it.
        /// </summary>
        public GraphQLQueryPlan ToQueryPlan(IFilterTranslationProvider? provider = null)
        {
            ArgumentNullException.ThrowIfNull(source);

            return new GraphQLQueryTranslator(provider ?? HotChocolateFilterProvider.Instance)
                .Translate(source.Expression);
        }
    }
}
