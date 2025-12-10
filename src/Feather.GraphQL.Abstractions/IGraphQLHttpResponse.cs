using System.Net;
using System.Net.Http.Headers;
using GraphQL;
using GraphQL.Response;

namespace Feather.GraphQL;

public interface IGraphQLHttpResponse : IGraphQLResponse
{
    HttpResponseHeaders ResponseHeaders { get; }

    HttpStatusCode StatusCode { get; }
}

public interface IGraphQLHttpDataResponse<out TData> : IGraphQLHttpResponse, IGraphQLDataResponse<TData>;
