using System.Linq.Expressions;
using Feather.GraphQL.Request;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// A translated chain, plus the shape needed to read its response back.
/// </summary>
/// <remarks>
/// The response is not self-describing: which root field to open, whether the server wrapped the
/// result in <c>items</c> or <c>nodes</c>, and how the projected type is assembled are all facts
/// about the request. Rediscovering them from the reply would mean guessing, so translation hands
/// them forward instead.
/// </remarks>
/// <param name="Request">The request to send.</param>
/// <param name="RootField">The field on the schema's <c>Query</c> type the result sits under.</param>
/// <param name="Paging">The wrapper the server puts around the result, if any.</param>
/// <param name="ElementType">The queried element type — the source of the chain, not its tip.</param>
/// <param name="Projection">The chain's <c>Select</c>, when it had one.</param>
internal sealed record TranslatedQuery(
    GraphQLRequest Request,
    string RootField,
    PagingKind Paging,
    Type ElementType,
    LambdaExpression? Projection);
