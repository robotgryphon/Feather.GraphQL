namespace GraphQL.Response;

public interface IGraphQLResponse
{
    GraphQLError[]? Errors { get; }

    IReadOnlyDictionary<string, object>? Extensions { get; }
}
