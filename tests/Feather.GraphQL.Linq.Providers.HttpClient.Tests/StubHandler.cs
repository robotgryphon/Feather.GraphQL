using System.Net;
using System.Text;

namespace Feather.GraphQL.Linq.Providers.Tests;

/// <summary>Answers every request from a canned body, and records what it was sent.</summary>
internal sealed class StubHandler(string json, HttpStatusCode status = HttpStatusCode.OK)
    : HttpMessageHandler
{
    /// <summary>The JSON body of the last request, for asserting on what actually went over.</summary>
    public string? SentBody { get; private set; }

    public HttpRequestMessage? SentRequest { get; private set; }

    public HttpClient Client(string baseAddress = "https://example.test/")
        => new(this) { BaseAddress = new Uri(baseAddress) };

    /// <summary>A client with no base address, for the absolute-endpoint cases.</summary>
    public HttpClient RootlessClient() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        SentRequest = request;
        SentBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
