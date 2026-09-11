using System.Text.Json.Nodes;

namespace Feather.GraphQL.Linq.Document;

/// <summary>A declared operation variable, e.g. <c>$where: PersonFilterInput</c>.</summary>
internal sealed record GqlVariableDefinition(string Name, string Type, JsonNode? Value);