namespace Feather.GraphQL.Linq.Filtering;

/// <summary>
/// The vocabulary of a server's filter input types. The seam that keeps predicate lowering
/// independent of any one server.
/// </summary>
/// <remarks>
/// v1 ships only <see cref="HotChocolateFilterProvider"/>. The interface exists so that adding a
/// second dialect is a new implementation rather than a redesign — and so the public API can
/// name a dialect rather than a vendor.
/// </remarks>
public interface IFilterTranslationProvider
{
    string Equal { get; }
    string NotEqual { get; }
    string GreaterThan { get; }
    string GreaterThanOrEqual { get; }
    string LessThan { get; }
    string LessThanOrEqual { get; }
    string In { get; }
    string NotIn { get; }
    string Contains { get; }
    string NotContains { get; }
    string StartsWith { get; }
    string NotStartsWith { get; }
    string EndsWith { get; }
    string NotEndsWith { get; }
    string And { get; }
    string Or { get; }
    string Some { get; }
    string All { get; }
    string None { get; }
    string Ascending { get; }
    string Descending { get; }
}
