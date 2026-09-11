using System.Linq.Expressions;
using System.Reflection;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Filtering;

/// <summary>
/// Names the server's arguments for one query, from inside the chain.
/// </summary>
/// <remarks>
/// Written as a classic extension method rather than an extension block because it needs a
/// stable <see cref="MethodInfo"/> for the call it plants in the expression tree — the same
/// mechanism EF Core's <c>Include</c> uses, and the reason this composes anywhere in the chain
/// and over any <see cref="IQueryable{T}"/>.
/// </remarks>
[PublicAPI]
public static class GraphQLArgumentExtensions
{
    internal static readonly MethodInfo WithGraphQLArgumentsMethod =
        typeof(GraphQLArgumentExtensions).GetMethod(nameof(WithGraphQLArguments))!;

    /// <summary>
    /// Tells the translator what this schema calls its arguments.
    /// </summary>
    /// <param name="source">The chain being composed.</param>
    /// <param name="arguments">
    /// The names to use. Unset members keep their HotChocolate defaults, so naming one argument
    /// costs one line: <c>new() { Filter = "filter" }</c>.
    /// </param>
    /// <returns>The chain, so composition continues normally.</returns>
    /// <example>
    /// <code>
    /// client.CreateQueryable&lt;Country&gt;()
    ///     .WithGraphQLArguments(new() { Filter = "filter" })
    ///     .Where(c => c.Continent.Name == "Europe")
    ///     .ToListAsync(ct);
    /// </code>
    /// </example>
    public static IQueryable<T> WithGraphQLArguments<T>(
        this IQueryable<T> source,
        GraphQLArgumentNames arguments)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(arguments);

        return source.Provider.CreateQuery<T>(
            Expression.Call(
                WithGraphQLArgumentsMethod.MakeGenericMethod(typeof(T)),
                source.Expression,
                Expression.Constant(arguments)));
    }
}
