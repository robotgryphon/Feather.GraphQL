using System.Text.Json;
using JetBrains.Annotations;

namespace Feather.GraphQL;

/// <summary>
/// The payload of a query that binds nothing.
/// </summary>
/// <remarks>
/// One instance for the whole process, and public because generated code names it. A payload is
/// read and never written, so every query with nothing to say can share the one that says
/// nothing.
/// </remarks>
[PublicAPI]
public sealed class GraphQLNoVariables : IGraphQLVariables
{
    /// <summary>The instance.</summary>
    public static readonly IGraphQLVariables Instance = new GraphQLNoVariables();

    private GraphQLNoVariables() { }

    /// <inheritdoc/>
    public bool IsEmpty => true;

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteEndObject();
    }
}
