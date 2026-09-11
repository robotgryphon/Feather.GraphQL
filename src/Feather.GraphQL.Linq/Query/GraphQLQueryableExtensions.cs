using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// The translate-without-sending terminal, for inspecting or caching what a chain would ask for.
/// </summary>
/// <remarks>
/// Sending is not here: a queryable created with an executor runs through its own terminals,
/// the way any other LINQ provider does.
/// </remarks>
[PublicAPI]
public static class GraphQLQueryableExtensions
{
    /// <summary>
    /// The options given here, or the ones the queryable was created with.
    /// </summary>
    /// <remarks>
    /// A queryable from this library already knows its schema; one from anywhere else has to be
    /// told, which is what the parameter is for.
    /// </remarks>
    private static GraphQLQueryOptions Resolve<T>(IQueryable<T> source, GraphQLQueryOptions? options)
        => options
            ?? (source.Provider as GraphQLQueryProvider)?.Options
            ?? new GraphQLQueryOptions();

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
        /// Because of that it is not the document that gets sent — that one binds every argument
        /// to a variable, and a transport receives it through
        /// <see cref="Execution.IGraphQLQueryExecutor"/>, which is also where an APQ hash belongs.
        /// </para>
        /// </remarks>
        public string ToGraphQLQuery(GraphQLQueryOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(source);

            return new GraphQLQueryTranslator(Resolve(source, options))
                .TranslateInline(source.Expression);
        }

        /// <summary>
        /// Translates the chain into the document, its variables, and the shape of its answer —
        /// what a custom <see cref="Execution.IGraphQLQueryExecutor"/> needs in order to run it.
        /// </summary>
        internal GraphQLQueryPlan ToQueryPlan(GraphQLQueryOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(source);

            return new GraphQLQueryTranslator(Resolve(source, options)).Translate(source.Expression);
        }
    }
}
