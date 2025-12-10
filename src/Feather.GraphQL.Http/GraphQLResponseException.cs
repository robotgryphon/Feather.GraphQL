using GraphQL.Response;

#pragma warning disable CS9113 // Parameter is unread.

namespace Feather.GraphQL.Http;

public class GraphQLResponseException(IGraphQLResponse response) : Exception
{
    public IGraphQLResponse Response { get; } = response;
}
