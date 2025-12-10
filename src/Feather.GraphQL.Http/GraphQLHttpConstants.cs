namespace Feather.GraphQL.Http;

public static class GraphQLHttpConstants
{
    public static readonly IReadOnlyCollection<string> RESPONSE_CONTENT_TYPES =
    [
            "application/graphql+json",
            "application/json",
            "application/graphql-response+json"
    ];
}
