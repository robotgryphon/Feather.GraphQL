namespace Feather.GraphQL;

public class PersistedQueryOptions
{
    /// <summary>
    /// Whether to enable use of <see href="https://www.apollographql.com/docs/react/api/link/persisted-queries/">Automatic Persisted Queries (APQ)</see>.
    /// By default, returns <see langword="false" /> for all requests. Note that GraphQL server should support APQ. Otherwise, the client disables APQ completely
    /// after an unsuccessful attempt to send an APQ request and then send only regular requests.
    /// </summary>
    public bool EnableAutomaticPersistedQueries { get; set; } = false;
}
