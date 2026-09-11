using System.Runtime.CompilerServices;
using System.Text.Json;
using Feather.GraphQL.Linq.Execution;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Tests.Execution;

/// <summary>
/// A transport that answers from a string. Execution, translation and materialization are all
/// testable without a socket — which is the point of the executor seam.
/// </summary>
internal sealed class StubExecutor : IGraphQLQueryExecutor
{
    private readonly byte[] _reply;
    private readonly Exception? _failure;

    public IFilterTranslationProvider FilterProvider => HotChocolateFilterProvider.Instance;

    private string? _query;
    private IGraphQLVariables? _variables;

    /// <summary>The document of the last execution — what the transport was actually handed.</summary>
    public string Document => _query ?? throw new InvalidOperationException("Nothing was executed.");

    /// <summary>The variables payload of the last execution, as JSON.</summary>
    public string Variables
    {
        get
        {
            var payload = _variables ?? throw new InvalidOperationException("Nothing was executed.");

            var buffer = new System.Buffers.ArrayBufferWriter<byte>();

            using (var writer = new Utf8JsonWriter(buffer))
                payload.WriteTo(writer);

            return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    private StubExecutor(string dataJson, Exception? failure = null)
    {
        // Wrapped in an envelope, because the transport seam now hands back a reply read whole
        // rather than a data element picked out of one.
        _reply = System.Text.Encoding.UTF8.GetBytes($$"""{"data":{{dataJson}}}""");
        _failure = failure;
    }

    /// <summary>A transport answering with the given <c>data</c> element.</summary>
    public static StubExecutor Returning(string dataJson) => new(dataJson);

    /// <summary>A transport that fails, standing in for any server-reported error.</summary>
    public static StubExecutor Failing(Exception failure) => new("{}", failure);

    /// <summary>Starts a query that runs through this stub.</summary>
    public IQueryable<T> Queryable<T>(string rootField, Action<GraphQLQueryOptions>? configure = null)
        => GraphQLQueryable.For<T>(this, rootField, configure);

    public ValueTask<IReadOnlyList<TElement>> ExecuteAsync<TElement>(
        GraphQLOperation operation,
        CancellationToken cancellationToken)
    {
        Record(operation, cancellationToken);

        return ValueTask.FromResult(GraphQLReplyReader.ReadRows<TElement>(_reply, operation));
    }

    public async IAsyncEnumerable<TElement> StreamAsync<TElement>(
        GraphQLOperation operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Record(operation, cancellationToken);

        await foreach (var row in GraphQLReplyReader
            .StreamRows<TElement>(_reply, operation, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return row;
        }
    }

    public ValueTask<long> ExecuteCountAsync(
        GraphQLOperation operation,
        CancellationToken cancellationToken)
    {
        Record(operation, cancellationToken);

        return ValueTask.FromResult(GraphQLReplyReader.ReadCount(_reply, operation));
    }

    private void Record(GraphQLOperation operation, CancellationToken cancellationToken)
    {
        _query = operation.Query;
        _variables = operation.Variables;
        cancellationToken.ThrowIfCancellationRequested();

        if (_failure is not null)
            throw _failure;
    }
}
