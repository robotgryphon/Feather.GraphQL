using System.Linq.Expressions;
using System.Reflection;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Filtering;

/// <summary>
/// <c>Where</c> overloads for the two things LINQ's own cannot say: that the predicate is written
/// against the server's filter input, and what the server calls its filter argument.
/// </summary>
/// <remarks>
/// <para>
/// A filter input is not always shaped like the thing it filters. The countries API returns
/// <c>Country.continent</c> as an object with a <c>name</c>, but its
/// <c>CountryFilterInput.continent</c> takes a string filter directly — so
/// <c>c =&gt; c.Continent.Name == "Europe"</c> lowers to <c>{"continent":{"name":{"eq":…}}}</c>,
/// one level deeper than the server accepts. Modelling the input as its own type and filtering
/// against that keeps both descriptions honest: the queried type says what comes back, and the
/// filter type says what may be asked.
/// </para>
/// <para>
/// The argument name can travel with the predicate, which is all a simple query needs.
/// <see cref="GraphQLArgumentExtensions.WithGraphQLArguments{T}"/> is still there for renaming
/// several arguments at once; whichever comes last in the chain wins.
/// </para>
/// </remarks>
[PublicAPI]
public static class GraphQLWhereExtensions
{
    internal static readonly MethodInfo WhereShapeMethod =
        typeof(GraphQLWhereExtensions)
            .GetMethod(nameof(WhereShape), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// The call planted in the expression tree. Never invoked — the translator reads it.
    /// </summary>
    /// <remarks>
    /// It exists because an expression tree needs a real <see cref="MethodInfo"/> to name, and
    /// the extension members below cannot supply a stable one.
    /// </remarks>
    private static IQueryable<TSource> WhereShape<TSource, TFilter>(
        IQueryable<TSource> source,
        Expression<Func<TFilter, bool>> predicate,
        string? argumentName)
        => source;

    extension<T>(IQueryable<T> source)
    {
        /// <summary>
        /// Filters using <typeparamref name="TFilter"/>, a model of the server's filter input.
        /// </summary>
        /// <typeparam name="TFilter">
        /// A type whose members mirror the filter input's fields. Names resolve the usual way —
        /// <c>[JsonPropertyName]</c>, then <c>[DataMember]</c>, then camel-cased.
        /// </typeparam>
        /// <param name="predicate">The predicate, written against the filter input.</param>
        /// <returns>The chain, still over <typeparamref name="T"/>: only the predicate changes shape.</returns>
        /// <remarks>
        /// Type the lambda's parameter — <c>(CountryFilter f) =&gt; …</c> — rather than writing
        /// <c>Where&lt;CountryFilter&gt;(…)</c>, which does not compile: C# binds an explicit type
        /// argument to the extension's own type parameter first and would read it as the element
        /// type. A chain filters one way or the other; mixing this with a predicate over
        /// <typeparamref name="T"/> is <c>FGQL020</c>.
        /// </remarks>
        /// <example>
        /// <code>
        /// client.CreateQueryable&lt;Country&gt;()
        ///     .Where((CountryFilter f) => f.Continent == "Europe")
        ///     .ToListAsync(ct);
        /// </code>
        /// </example>
        public IQueryable<T> Where<TFilter>(Expression<Func<TFilter, bool>> predicate)
            => source.WhereFilter(predicate, argumentName: null);

        /// <summary>
        /// Filters using <typeparamref name="TFilter"/>, naming the server's filter argument
        /// inline.
        /// </summary>
        /// <param name="argumentName">
        /// What this schema calls its filter argument — <c>"filter"</c>, where HotChocolate says
        /// <c>"where"</c>.
        /// </param>
        /// <param name="predicate">The predicate, written against the filter input.</param>
        /// <remarks>
        /// The whole of what a simple query needs to say about a schema's naming. Reach for
        /// <c>WithGraphQLArguments</c> when the sort or paging arguments are renamed too.
        /// </remarks>
        /// <example>
        /// <code>
        /// client.CreateQueryable&lt;Country&gt;()
        ///     .Where("filter", (CountryFilter f) => f.Continent == "Europe")
        ///     .ToListAsync(ct);
        /// </code>
        /// </example>
        public IQueryable<T> Where<TFilter>(string argumentName, Expression<Func<TFilter, bool>> predicate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(argumentName);

            return source.WhereFilter(predicate, argumentName);
        }

        /// <summary>
        /// Filters over the queried type, naming the server's filter argument inline.
        /// </summary>
        /// <param name="argumentName">
        /// What this schema calls its filter argument — <c>"filter"</c>, where HotChocolate says
        /// <c>"where"</c>.
        /// </param>
        /// <param name="predicate">The predicate, written against <typeparamref name="T"/>.</param>
        /// <example>
        /// <code>
        /// client.CreateQueryable&lt;Person&gt;()
        ///     .Where("filter", p => p.Age > 30)
        ///     .ToListAsync(ct);
        /// </code>
        /// </example>
        public IQueryable<T> Where(string argumentName, Expression<Func<T, bool>> predicate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(argumentName);

            return source.WhereFilter(predicate, argumentName);
        }

        private IQueryable<T> WhereFilter<TFilter>(
            Expression<Func<TFilter, bool>> predicate,
            string? argumentName)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(predicate);

            // The filter's shape is printed by the compiler from this predicate's syntax. Nothing
            // reads it as a tree, so nothing builds one.
            throw GraphQLTranslationException.NotCompiled();
        }
    }
}
