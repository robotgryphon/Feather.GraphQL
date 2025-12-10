using GraphQL;

namespace Feather.GraphQL.Subscriptions;

public class WebsocketGraphQLSubscriptionSource : IGraphQLSubscriptionSource, IDisposable
{
    /// <inheritdoc />
    public IObservable<IGraphQLResponse> CreateSubscriptionStream(GraphQLRequest request)
        => CreateSubscriptionStream(request, null);

    /// <inheritdoc />
    public IObservable<IGraphQLResponse> CreateSubscriptionStream(GraphQLRequest request,
            Action<Exception>? exceptionHandler)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WebsocketGraphQLSubscriptionSource));

        var observable = GraphQlHttpWebSocket.CreateSubscriptionStream(request, exceptionHandler);
        return observable;
    }

    public void Dispose()
    {
        // TODO release managed resources here
    }
}
