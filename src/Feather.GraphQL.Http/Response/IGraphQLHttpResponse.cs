using System.Net;
using System.Net.Http.Headers;
using Feather.GraphQL.Response;
using JetBrains.Annotations;

namespace Feather.GraphQL.Http.Response;

[PublicAPI]
public interface IGraphQLHttpResponse : IGraphQLResponse
{
    public HttpResponseHeaders ResponseHeaders { get; }

    public HttpStatusCode StatusCode { get; }
}
