namespace Feather.GraphQL.Linq.Document;

/// <summary>A field, its arguments, and its nested selection set.</summary>
internal sealed record GqlField(string Name)
{
    public IReadOnlyList<GqlArgument> Arguments { get; init; } = [];
    public IReadOnlyList<GqlField> Selection { get; init; } = [];
}