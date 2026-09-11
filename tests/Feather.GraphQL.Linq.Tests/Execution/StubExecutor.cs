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
    private readonly JsonDocument _data;
    private readonly Exception? _failure;

    public IFilterTranslationProvider FilterProvider => HotChocolateFilterProvider.Instance;

    private string? _query;
    private IReadOnlyDictionary<string, object?>? _variables;

    /// <summary>The document of the last execution — what the transport was actually handed.</summary>
    public string Document => _query ?? throw new InvalidOperationException("Nothing was executed.");

    /// <summary>The variables payload of the last execution, as JSON.</summary>
    public string Variables => JsonSerializer.Serialize(
        _variables ?? throw new InvalidOperationException("Nothing was executed."));

    private StubExecutor(string dataJson, Exception? failure = null)
    {
        _data = JsonDocument.Parse(dataJson);
        _failure = failure;
    }

    /// <summary>A transport answering with the given <c>data</c> element.</summary>
    public static StubExecutor Returning(string dataJson) => new(dataJson);

    /// <summary>A transport that fails, standing in for any server-reported error.</summary>
    public static StubExecutor Failing(Exception failure) => new("{}", failure);

    /// <summary>Starts a query that runs through this stub.</summary>
    public IQueryable<T> Queryable<T>(string rootField, Action<GraphQLQueryOptions>? configure = null)
        => GraphQLQueryable.For<T>(this, rootField, configure);

    public ValueTask<JsonElement> ExecuteAsync(
        string query,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
        _query = query;
        _variables = variables;
        cancellationToken.ThrowIfCancellationRequested();

        return _failure is not null
            ? throw _failure
            : ValueTask.FromResult(_data.RootElement);
    }
}
