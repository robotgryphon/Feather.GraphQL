using System.Diagnostics.CodeAnalysis;

namespace Feather.GraphQL.Linq.Metadata;

/// <summary>
/// Field mapping for one CLR type. Emitted by the source generator for attributed types;
/// built by reflection as a fallback for types the generator never saw.
/// </summary>
public interface IGraphQLTypeMetadata
{
    Type ClrType { get; }

    /// <summary>The field on the schema's <c>Query</c> type, or null when the type is filter-only.</summary>
    string? RootField { get; }

    PagingKind Paging { get; }

    string FilterInputName { get; }

    string SortInputName { get; }

    IReadOnlyList<GraphQLFieldMetadata> Fields { get; }

    /// <summary>Looks up a field by CLR member name.</summary>
    bool TryGetField(string clrName, [NotNullWhen(true)] out GraphQLFieldMetadata? field);
}
