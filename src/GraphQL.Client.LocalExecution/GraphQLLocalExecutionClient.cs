using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using GraphQL.Client.Abstractions;
using GraphQL.Types;

namespace GraphQL.Client.LocalExecution;

public class GraphQLLocalExecutionClient<TSchema> : IGraphQLClient where TSchema : ISchema
{
    public TSchema Schema { get; }

    private readonly IDocumentExecuter _documentExecuter;
    private readonly IGraphQLTextSerializer _documentSerializer;
    private readonly JsonSerializerOptions _serializerOptions;

    public GraphQLLocalExecutionClient(TSchema schema, IDocumentExecuter documentExecuter, IGraphQLTextSerializer documentSerializer, JsonSerializerOptions serializerOptions)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema), "no schema configured");
        _documentExecuter = documentExecuter;
        _documentSerializer = documentSerializer;
        _serializerOptions = serializerOptions;

        if (!Schema.Initialized)
            Schema.Initialize();
    }

    public void Dispose() { }

    public Task<IGraphQLResponse> SendQueryAsync<TResponse>(GraphQLRequest request, CancellationToken cancellationToken = default)
        => ExecuteQueryAsync<TResponse>(request, cancellationToken);

    public Task<IGraphQLResponse> SendMutationAsync<TResponse>(GraphQLRequest request, CancellationToken cancellationToken = default)
        => ExecuteQueryAsync<TResponse>(request, cancellationToken);

    public IObservable<IGraphQLResponse> CreateSubscriptionStream<TResponse>(GraphQLRequest request) =>
        Observable.Defer(() => ExecuteSubscriptionAsync<TResponse>(request).ToObservable())
            .Concat()
            .Publish()
            .RefCount();

    public IObservable<IGraphQLResponse> CreateSubscriptionStream<TResponse>(GraphQLRequest request,
        Action<Exception> exceptionHandler)
        => CreateSubscriptionStream<TResponse>(request);

    #region Private Methods

    private async Task<IGraphQLResponse> ExecuteQueryAsync<TResponse>(GraphQLRequest request, CancellationToken cancellationToken)
    {
        var executionResult = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        return await ExecutionResultToGraphQLResponseAsync<TResponse>(executionResult, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IObservable<IGraphQLResponse>> ExecuteSubscriptionAsync<TResponse>(GraphQLRequest request, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        var stream = result.Streams?.Values.SingleOrDefault();

        return stream == null
            ? Observable.Throw<IGraphQLResponse>(new InvalidOperationException("the GraphQL execution did not return an observable"))
            : stream.SelectMany(executionResult => Observable.FromAsync(token => ExecutionResultToGraphQLResponseAsync<TResponse>(executionResult, token)));
    }

    private async Task<ExecutionResult> ExecuteAsync(GraphQLRequest clientRequest, CancellationToken cancellationToken = default)
    {
        var serverRequest = _documentSerializer.Deserialize<Transport.GraphQLRequest>(JsonSerializer.Serialize(clientRequest, _serializerOptions));

        var result = await _documentExecuter.ExecuteAsync(options =>
        {
            options.Schema = Schema;
            options.OperationName = serverRequest?.OperationName;
            options.Query = serverRequest?.Query;
            options.Variables = serverRequest?.Variables;
            options.Extensions = serverRequest?.Extensions;
            options.CancellationToken = cancellationToken;
        }).ConfigureAwait(false);

        return result;
    }

    private async Task<IGraphQLResponse> ExecutionResultToGraphQLResponseAsync<TResponse>(ExecutionResult executionResult, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        await _documentSerializer.WriteAsync(stream, executionResult, cancellationToken).ConfigureAwait(false);
        stream.Seek(0, SeekOrigin.Begin);
        return await JsonSerializer.DeserializeAsync<GraphQLDataResponse<TResponse>>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
    }

    #endregion
}
