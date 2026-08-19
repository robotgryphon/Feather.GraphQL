namespace Feather.GraphQL.Linq;

/// <summary>
/// Binds a type to a single field on the schema's <c>Query</c> type and generates the
/// metadata needed to translate LINQ over it.
/// </summary>
/// <remarks>
/// One root field per type is a deliberate limit. Under HotChocolate filtering a singular
/// lookup (<c>person(id:)</c>) is expressible through the filtered collection, so the
/// second binding would buy little for the API surface it costs.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class GenerateQueryableAttribute : GenerateFilterAttribute
{
    /// <summary>The field on the schema's <c>Query</c> type — not the name of the GraphQL type.</summary>
    public string RootField { get; }

    /// <summary>
    /// How the server wraps this field's result. Cannot be inferred from the CLR type and
    /// must match the server, since it changes the emitted selection set.
    /// </summary>
    public PagingKind Paging { get; set; } = PagingKind.None;

    public GenerateQueryableAttribute(string rootField)
    {
        if (string.IsNullOrWhiteSpace(rootField))
            throw new ArgumentException("A root field name is required.", nameof(rootField));

        RootField = rootField;
    }
}
