namespace GraphQL.Response;

public interface IGraphQLDataResponse<out TData> : IGraphQLResponse
{
    public TData? Data { get; }
}
