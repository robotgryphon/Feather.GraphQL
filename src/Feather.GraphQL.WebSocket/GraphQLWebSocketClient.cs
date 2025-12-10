using GraphQL.Client.Abstractions.Websocket;
using GraphQL.Client.Http.Websocket;

namespace GraphQL.Client.Http;

public class GraphQLWebSocketClient : IGraphQLWebSocketClient, IDisposable
{
    private readonly Lazy<GraphQLHttpWebSocket> _lazyHttpWebSocket;
    private GraphQLHttpWebSocket GraphQlHttpWebSocket => _lazyHttpWebSocket.Value;

    /// <inheritdoc />
    public IObservable<Exception> WebSocketReceiveErrors => GraphQlHttpWebSocket.ReceiveErrors;

    /// <inheritdoc />
    public string? WebSocketSubProtocol => GraphQlHttpWebSocket.WebsocketProtocol;

    /// <inheritdoc />
    public IObservable<GraphQLWebsocketConnectionState> WebsocketConnectionState => GraphQlHttpWebSocket.ConnectionState;

    /// <inheritdoc />
    public IObservable<object?> PongStream => GraphQlHttpWebSocket.GetPongStream();

    public GraphQLWebSocketClient(GraphQLHttpClientOptions options, IGraphQLWebsocketJsonSerializer serializer, HttpClient httpClient)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        JsonSerializer = serializer ?? throw new ArgumentNullException(nameof(serializer), "please configure the JSON serializer you want to use");
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _lazyHttpWebSocket = new Lazy<GraphQLHttpWebSocket>(CreateGraphQLHttpWebSocket);
    }

    private GraphQLHttpWebSocket CreateGraphQLHttpWebSocket()
    {
        if (Options.WebSocketEndPoint is null && Options.EndPoint is null)
            throw new InvalidOperationException("no endpoint configured");

        var webSocketEndpoint = Options.WebSocketEndPoint ?? Options.EndPoint.GetWebSocketUri();
        return !webSocketEndpoint.HasWebSocketScheme()
                ? throw new InvalidOperationException($"uri \"{webSocketEndpoint}\" is not a websocket endpoint")
                : new GraphQLHttpWebSocket(webSocketEndpoint, this);
    }

    /// <inheritdoc />
    public Task InitializeWebsocketConnection() => GraphQlHttpWebSocket.InitializeWebSocket();

    /// <inheritdoc />
    public Task SendPingAsync(object? payload) => GraphQlHttpWebSocket.SendPingAsync(payload);

    /// <inheritdoc />
    public Task SendPongAsync(object? payload) => GraphQlHttpWebSocket.SendPongAsync(payload);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (_lazyHttpWebSocket.IsValueCreated)
            _lazyHttpWebSocket.Value.Dispose();
    }
}
