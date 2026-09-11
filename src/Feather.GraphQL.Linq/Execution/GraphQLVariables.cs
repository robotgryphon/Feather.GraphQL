using System.Text.Json;
using Feather.GraphQL.Linq.Document;

namespace Feather.GraphQL.Linq.Execution;

/// <summary>The payloads this library builds.</summary>
internal static class GraphQLVariables
{
    /// <summary>
    /// The payload of a query that binds nothing, which is most of them.
    /// </summary>
    /// <remarks>
    /// One instance for the whole process. A payload is read and never written, so a query with
    /// nothing to say can share the one that says nothing.
    /// </remarks>
    public static readonly IGraphQLVariables None = new Empty();

    /// <summary>The payload of a query whose only variable is a page size.</summary>
    /// <remarks>
    /// What a precompiled plan binds for <c>First</c> or <c>Single</c>. The value is an
    /// <see cref="int"/> and stays one all the way to the request: a page of one has no need of
    /// a <c>JsonNode</c> to represent it.
    /// </remarks>
    public static IGraphQLVariables Page(string name, int value) => new PageOnly(name, value);

    /// <summary>The payload the translator lowered a chain's arguments into.</summary>
    public static IGraphQLVariables Lowered(IReadOnlyList<GqlVariableDefinition> definitions)
        => definitions.Count == 0 ? None : new Lowered_(definitions);

    private sealed class Empty : IGraphQLVariables
    {
        public bool IsEmpty => true;

        public void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }

    private sealed class PageOnly(string name, int value) : IGraphQLVariables
    {
        public bool IsEmpty => false;

        public void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteNumber(name, value);
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// Written straight from the definitions the translator produced.
    /// </summary>
    /// <remarks>
    /// The values are still <c>JsonNode</c>s, because that is what the filter lowering builds and
    /// unbuilding it is a larger change than this one — but they are no longer copied into a
    /// dictionary, boxed, or looked up by name on the way out.
    /// </remarks>
    private sealed class Lowered_(IReadOnlyList<GqlVariableDefinition> definitions) : IGraphQLVariables
    {
        public bool IsEmpty => definitions.Count == 0;

        public void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();

            foreach (var definition in definitions)
            {
                writer.WritePropertyName(definition.Name);

                if (definition.Value is null)
                    writer.WriteNullValue();
                else
                    definition.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }
    }
}
