using System.Text.Json.Nodes;

namespace Feather.GraphQL.Linq.Document;

/// <summary>An operation and its selection set. The IR the translator targets.</summary>
internal sealed record GqlDocument(
    IReadOnlyList<GqlVariableDefinition> Variables,
    GqlField Root);

/// <summary>A declared operation variable, e.g. <c>$where: PersonFilterInput</c>.</summary>
internal sealed record GqlVariableDefinition(string Name, string Type, JsonNode? Value);

/// <summary>A field, its arguments, and its nested selection set.</summary>
internal sealed record GqlField(string Name)
{
    public IReadOnlyList<GqlArgument> Arguments { get; init; } = [];
    public IReadOnlyList<GqlField> Selection { get; init; } = [];
}

/// <summary>An argument bound to a variable. v1 never inlines values — see §5.3.</summary>
internal sealed record GqlArgument(string Name, string VariableName);
