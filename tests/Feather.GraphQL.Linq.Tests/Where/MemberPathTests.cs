namespace Feather.GraphQL.Linq.Tests.Where;

/// <summary>How a CLR member chain becomes a GraphQL field path.</summary>
[TestFixture]
public class MemberPathTests
{
    [Test]
    public void Nested_member_path_nests_the_filter()
        => Assert.That(Lower.Where(p => p.Address!.City == "Leeds"),
            Is.EqualTo("""{"address":{"city":{"eq":"Leeds"}}}"""));

    [Test]
    public void Nested_paths_on_the_same_object_merge()
        => Assert.That(Lower.Where(p => p.Address!.City == "Leeds" && p.Address.Country == "UK"),
            Is.EqualTo("""{"address":{"city":{"eq":"Leeds"},"country":{"eq":"UK"}}}"""));

    [Test]
    public void JsonPropertyName_wins_over_the_symbol_name()
        => Assert.That(Lower.Where(p => p.Email == "a@b.c"),
            Is.EqualTo("""{"emailAddress":{"eq":"a@b.c"}}"""));

    [Test]
    public void Unattributed_members_camel_case_the_symbol_name()
        => Assert.That(Lower.Where(p => p.IsActive), Does.Contain("isActive"));
}
