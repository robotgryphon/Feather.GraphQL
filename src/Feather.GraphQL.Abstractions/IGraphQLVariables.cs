using System.Text.Json;
using JetBrains.Annotations;

namespace Feather.GraphQL;

/// <summary>
/// The values a query's variables take, as something that can write itself.
/// </summary>
/// <remarks>
/// <para>
/// A payload is built once and written once, and nothing ever reads it back. That is the whole
/// argument for this shape: it used to be an <c>IReadOnlyDictionary&lt;string, object?&gt;</c>,
/// which meant allocating a dictionary per request, boxing every value into <see cref="object"/>,
/// and then — in the one place that consumed it — enumerating the dictionary and switching on
/// each value's runtime type to decide how to write it. None of that work told anyone anything
/// the translator did not already know.
/// </para>
/// <para>
/// It lives below both halves of the library because both touch it: the LINQ integration builds
/// one, and the transport writes it into the request body.
/// </para>
/// </remarks>
[PublicAPI]
public interface IGraphQLVariables
{
    /// <summary>
    /// True when the query binds no arguments, and the request should carry no
    /// <c>variables</c> member at all.
    /// </summary>
    bool IsEmpty { get; }

    /// <summary>
    /// Writes the payload, braces included, as the value of the request's <c>variables</c>.
    /// </summary>
    void WriteTo(Utf8JsonWriter writer);
}
