using Feather.GraphQL.Primitives;

namespace Feather.GraphQL.Response;

public interface IGraphQLResponse
{
    GraphQLError[]? Errors { get; }

    IReadOnlyDictionary<string, object>? Extensions { get; }
}
