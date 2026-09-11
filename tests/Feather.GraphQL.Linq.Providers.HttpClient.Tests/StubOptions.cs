using Microsoft.Extensions.Options;

namespace Feather.GraphQL.Linq.Providers.Tests;

/// <summary>Stands in for a container's <see cref="IOptions{TOptions}"/>.</summary>
internal sealed class StubOptions(GraphQLHttpQueryOptions value) : IOptions<GraphQLHttpQueryOptions>
{
    public GraphQLHttpQueryOptions Value { get; } = value;
}
