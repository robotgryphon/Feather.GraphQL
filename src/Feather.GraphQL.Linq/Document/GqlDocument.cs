namespace Feather.GraphQL.Linq.Document;

/// <summary>An operation and its selection set. The IR the translator targets.</summary>
internal sealed record GqlDocument(
    IReadOnlyList<GqlVariableDefinition> Variables,
    GqlField Root);