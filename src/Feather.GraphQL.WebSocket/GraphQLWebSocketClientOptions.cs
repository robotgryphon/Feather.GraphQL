using System.Net.WebSockets;
using GraphQL.Client.Http.Websocket;

namespace GraphQL.Client.Http;

public class GraphQLWebSocketClientOptions
{
    /// <summary>
    /// The GraphQL websocket protocol to be used. Defaults to the older "graphql-ws" protocol to not break old code.
    /// </summary>
    public string? WebSocketProtocol { get; set; } = WebSocketProtocols.AUTO_NEGOTIATE;

    /// <summary>
    /// If <see langword="true"/>, the websocket connection is also used for regular queries and mutations
    /// </summary>
    public bool UseWebSocketForQueriesAndMutations { get; set; } = false;

    /// <summary>
    /// This callback is called after successfully establishing a websocket connection but before any regular request is made.
    /// </summary>
    public Func<GraphQLHttpClient, Task> OnWebsocketConnected { get; set; } = client => Task.CompletedTask;

    /// <summary>
    /// Configure additional websocket options (i.e. headers). This will not be invoked on Windows 7 when targeting .NET Framework 4.x.
    /// </summary>
    public Action<ClientWebSocketOptions> ConfigureWebsocketOptions { get; set; } = options => { };

    /// <summary>
    /// Sets the `ConnectionParams` object sent with the GQL_CONNECTION_INIT message on establishing a GraphQL websocket connection.
    /// See https://github.com/apollographql/subscriptions-transport-ws/blob/master/PROTOCOL.md#gql_connection_init.
    /// </summary>
    public Func<GraphQLHttpClientOptions, object?> ConfigureWebSocketConnectionInitPayload { get; set; } =
        options => null;
}
