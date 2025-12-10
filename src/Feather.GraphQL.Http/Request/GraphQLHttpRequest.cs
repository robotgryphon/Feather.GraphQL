using GraphQL;
using GraphQL.Request;
#pragma warning disable IDE0005
// see https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/8.0/implicit-global-using-netfx
using System.Diagnostics.CodeAnalysis;

#pragma warning restore IDE0005

namespace Feather.GraphQL.Http.Request;

public class GraphQLHttpRequest : GraphQLRequest
{
    public GraphQLHttpRequest()
    {
    }

    public GraphQLHttpRequest([StringSyntax("GraphQL")] string query, object? variables = null,
            string? operationName = null, Dictionary<string, object?>? extensions = null)
            : base(query, variables, operationName, extensions)
    {
    }

    public GraphQLHttpRequest(GraphQLQuery query, object? variables = null, string? operationName = null,
            Dictionary<string, object?>? extensions = null)
            : base(query, variables, operationName, extensions)
    {
    }

    public GraphQLHttpRequest(GraphQLRequest other)
            : base(other)
    {
    }
}
