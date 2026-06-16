using Feather.GraphQL.Response;
using JetBrains.Annotations;

namespace Feather.GraphQL.Http.Response;

[PublicAPI]
public interface IGraphQLHttpDataResponse<out TData> : IGraphQLHttpResponse, IGraphQLDataResponse<TData>;
