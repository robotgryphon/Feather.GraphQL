using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Feather.GraphQL.Http.Request;

public static class HttpExtensions
{
    public static StringContent AsHttpMessageContent<T>(this T request)
    {
        var body = JsonSerializer.Serialize(request, new JsonSerializerOptions()
        {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
        });

        var content = new StringContent(body, Encoding.UTF8, "application/json");

        // Explicitly setting content header to avoid issues with some GraphQL servers
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return content;
    }

    public static HttpRequestMessage AddGraphQLRequestHeaders(this HttpRequestMessage message)
    {
        foreach (string contentType in GraphQLHttpConstants.RESPONSE_CONTENT_TYPES)
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(contentType));

        message.Headers.AcceptCharset.Add(new StringWithQualityHeaderValue("utf-8"));

        var a = typeof(HttpExtensions).Assembly;
        message.Headers.UserAgent.Add(new ProductInfoHeaderValue(a.GetName().Name!, a.GetName().Version!.ToString()));
        return message;
    }

    public static HttpRequestMessage AsHttpPost<T>(this T request)
    {
        var message = new HttpRequestMessage { Method = HttpMethod.Post, Content = request.AsHttpMessageContent() };
        message.AddGraphQLRequestHeaders();
        return message;
    }

    public static HttpRequestMessage AsHttpPost<T>(this T request, Uri endpoint)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = request.AsHttpMessageContent() };
        message.AddGraphQLRequestHeaders();
        return message;
    }

    public static HttpRequestMessage AsHttpPost<T>(this T request, [StringSyntax("Uri")] string endpoint)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = request.AsHttpMessageContent() };
        message.AddGraphQLRequestHeaders();
        return message;
    }
}
