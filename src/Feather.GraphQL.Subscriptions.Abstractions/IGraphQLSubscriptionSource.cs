using System.Net.WebSockets;
using GraphQL;

namespace Feather.GraphQL.Subscriptions;

public interface IGraphQLSubscriptionSource
{
    /// <summary>
    /// Creates a subscription to a GraphQL server. The connection is not established until the first actual subscription is made.<br/>
    /// All subscriptions made to this stream share the same hot observable.<br/>
    /// The stream must be recreated completely after an error has occurred within its logic (i.e. a <see cref="WebSocketException"/>)
    /// </summary>
    /// <param name="request">the GraphQL request for this subscription</param>
    /// <returns>an observable stream for the specified subscription</returns>
    IObservable<IGraphQLResponse> CreateSubscriptionStream(GraphQLRequest request);

    /// <summary>
    /// Creates a subscription to a GraphQL server. The connection is not established until the first actual subscription is made.<br/>
    /// All subscriptions made to this stream share the same hot observable.<br/>
    /// All <see cref="Exception"/>s are passed to the <paramref name="exceptionHandler"/> to be handled externally.<br/>
    /// If the <paramref name="exceptionHandler"/> completes normally, the subscription is recreated with a new connection attempt.<br/>
    /// Any exception thrown by <paramref name="exceptionHandler"/> will cause the sequence to fail.
    /// </summary>
    /// <param name="request">the GraphQL request for this subscription</param>
    /// <param name="exceptionHandler">an external handler for all <see cref="Exception"/>s occurring within the sequence</param>
    /// <returns>an observable stream for the specified subscription</returns>
    IObservable<IGraphQLResponse> CreateSubscriptionStream(GraphQLRequest request, Action<Exception> exceptionHandler);
}
