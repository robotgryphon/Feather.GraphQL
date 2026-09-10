namespace Feather.GraphQL.Linq;

/// <summary>
/// Opts a type into generated field-name metadata, so predicate lowering over it is
/// reflection-free. Does not make the type queryable — see <see cref="GenerateQueryableAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public class GenerateFilterAttribute : Attribute
{
    /// <summary>
    /// The GraphQL filter input type name. Defaults to <c>{TypeName}FilterInput</c>,
    /// matching HotChocolate's convention.
    /// </summary>
    public string? FilterInput { get; set; }

    /// <summary>
    /// The GraphQL sort input type name. Defaults to <c>{TypeName}SortInput</c>.
    /// </summary>
    public string? SortInput { get; set; }
}
