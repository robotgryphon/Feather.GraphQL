using System.Diagnostics.CodeAnalysis;

namespace Feather.GraphQL.Linq.Metadata;

/// <summary>
/// Field mapping for one CLR type: which CLR member is which GraphQL field, and nothing more.
/// Everything else a query needs is a property of the schema rather than of the type, and lives
/// in <see cref="Query.GraphQLQueryOptions"/>.
/// </summary>
public interface IGraphQLTypeMetadata
{
    Type ClrType { get; }

    IReadOnlyList<GraphQLFieldMetadata> Fields { get; }

    /// <summary>Looks up a field by CLR member name.</summary>
    bool TryGetField(string clrName, [NotNullWhen(true)] out GraphQLFieldMetadata? field);
}
