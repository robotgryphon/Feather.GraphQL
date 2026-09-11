using System.Net;
using System.Net.Http.Headers;

namespace Feather.GraphQL.Benchmarks;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers instantly from a fixed body.
/// </summary>
/// <remarks>
/// <para>
/// There is no socket, no DNS, no TLS and no server. That is the whole point: a real endpoint
/// costs hundreds of microseconds and varies by more than the thing being measured, so any
/// difference between two clients would be buried in it.
/// </para>
/// <para>
/// It is also careful not to add work of its own. The request body is never read — reading it
/// to a string, as the test double does, would cost more than some of the benchmarks — and the
/// response body is a cached array wrapped in a fresh <see cref="ByteArrayContent"/>, because
/// content can only be consumed once. That per-call allocation is the same for every client
/// measured, and <c>TransportFloor</c> reports what it costs.
/// </para>
/// </remarks>
public sealed class CannedTransport(byte[] body) : HttpMessageHandler
{
    private static readonly MediaTypeHeaderValue _json = new("application/json");

    /// <summary>A client pointed at a URL nothing will ever resolve.</summary>
    public HttpClient Client() => new(this, disposeHandler: false)
    {
        BaseAddress = new Uri("https://benchmark.invalid/graphql")
    };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => Task.FromResult(Respond());

    protected override HttpResponseMessage Send(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => Respond();

    private HttpResponseMessage Respond()
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = _json;

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}
