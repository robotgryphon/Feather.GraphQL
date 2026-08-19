namespace Feather.GraphQL.Linq.Filtering;

/// <summary>
/// HotChocolate's filter and sort vocabulary.
/// </summary>
/// <remarks>
/// TODO: these names are written from the documented conventions and must be confirmed against a
/// generated <c>PersonFilterInput</c> from the target server before this is trusted in anger. The
/// negated forms (<c>ngt</c>, <c>ncontains</c>, …) and the collection quantifiers are the ones
/// most likely to be wrong. Everything in this file is one table so that correcting it is a
/// single-file change.
/// </remarks>
public sealed class HotChocolateFilterProvider : IFilterTranslationProvider
{
    public static readonly HotChocolateFilterProvider Instance = new();

    public string Equal => "eq";
    public string NotEqual => "neq";
    public string GreaterThan => "gt";
    public string GreaterThanOrEqual => "gte";
    public string LessThan => "lt";
    public string LessThanOrEqual => "lte";
    public string In => "in";
    public string NotIn => "nin";
    public string Contains => "contains";
    public string NotContains => "ncontains";
    public string StartsWith => "startsWith";
    public string NotStartsWith => "nstartsWith";
    public string EndsWith => "endsWith";
    public string NotEndsWith => "nendsWith";
    public string And => "and";
    public string Or => "or";
    public string Some => "some";
    public string All => "all";
    public string None => "none";
    public string Ascending => "ASC";
    public string Descending => "DESC";
}
