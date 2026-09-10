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

    /// <summary>The plan the last execution was given, for asserting on what was asked.</summary>
    public GraphQLQueryPlan? LastPlan { get; private set; }

    /// <summary>The document text of the last execution.</summary>
    public string Document => LastPlan?.Query
        ?? throw new InvalidOperationException("Nothing was executed.");

    /// <summary>The variables payload of the last execution, as JSON.</summary>
    public string Variables => JsonSerializer.Serialize(
        LastPlan?.Variables ?? throw new InvalidOperationException("Nothing was executed."));

    private StubExecutor(string dataJson, Exception? failure = null)
    {
        _data = JsonDocument.Parse(dataJson);
        _failure = failure;
    }

    /// <summary>A source answering with the given <c>data</c> element.</summary>
    public static (IGraphQLQueryableSource Source, StubExecutor Executor) Returning(string dataJson)
    {
        var executor = new StubExecutor(dataJson);
        return (new GraphQLQueryableSource(executor), executor);
    }

    /// <summary>A source whose transport fails, standing in for any server-reported error.</summary>
    public static IGraphQLQueryableSource Failing(Exception failure)
        => new GraphQLQueryableSource(new StubExecutor("{}", failure));

    public ValueTask<JsonElement> ExecuteAsync(GraphQLQueryPlan plan, CancellationToken cancellationToken)
    {
        LastPlan = plan;
        cancellationToken.ThrowIfCancellationRequested();

        return _failure is not null
            ? throw _failure
            : ValueTask.FromResult(_data.RootElement);
    }
}
