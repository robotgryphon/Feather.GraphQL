using Feather.GraphQL.Linq.Expressions;

namespace Feather.GraphQL.Linq.Tests.Where;

/// <summary>
/// Captured values are read, not compiled. Every shape here used to reach
/// <c>LambdaExpression.Compile()</c> or would have; none may now.
/// </summary>
/// <remarks>
/// The assertion is on <see cref="PartialEvaluator.CompiledSubtrees"/> rather than on the lowered
/// JSON, because the JSON is identical either way — compiling produces the right answer, just by
/// generating IL to get it. Only the counter distinguishes them.
/// </remarks>
[TestFixture]
public class ClosureEvaluationTests
{
    private sealed class Options
    {
        public string Name { get; init; } = "";
        public Options? Nested { get; init; }
    }

    private static string LowersWithoutCompiling(Func<string> lower)
    {
        int before = PartialEvaluator.CompiledSubtrees;
        string json = lower();

        Assert.That(PartialEvaluator.CompiledSubtrees, Is.EqualTo(before),
            "a subtree was compiled — this predicate still generates IL at runtime");

        return json;
    }

    [Test]
    public void A_captured_local_is_read()
    {
        string name = "Ada";

        Assert.That(LowersWithoutCompiling(() => Lower.Where(p => p.Name == name)),
            Is.EqualTo("""{"name":{"eq":"Ada"}}"""));
    }

    /// <summary>
    /// A field of a field. The nominator marks the outermost node, so only a walk that descends
    /// reaches the closure underneath — this is the case a single-level read missed.
    /// </summary>
    [Test]
    public void A_chained_capture_is_read()
    {
        var options = new Options { Name = "Ada" };

        Assert.That(LowersWithoutCompiling(() => Lower.Where(p => p.Name == options.Name)),
            Is.EqualTo("""{"name":{"eq":"Ada"}}"""));
    }

    [Test]
    public void A_capture_two_levels_deep_is_read()
    {
        var options = new Options { Nested = new Options { Name = "Grace" } };

        Assert.That(LowersWithoutCompiling(() => Lower.Where(p => p.Name == options.Nested!.Name)),
            Is.EqualTo("""{"name":{"eq":"Grace"}}"""));
    }

    /// <summary>An array literal is one parameter-free subtree, built rather than compiled.</summary>
    [Test]
    public void An_array_literal_is_built()
        => Assert.That(
            LowersWithoutCompiling(() => Lower.Where(p => new[] { "Ada", "Grace" }.Contains(p.Name))),
            Is.EqualTo("""{"name":{"in":["Ada","Grace"]}}"""));

    [Test]
    public void A_captured_collection_is_read()
    {
        var names = new List<string> { "Ada", "Grace" };

        Assert.That(LowersWithoutCompiling(() => Lower.Where(p => names.Contains(p.Name))),
            Is.EqualTo("""{"name":{"in":["Ada","Grace"]}}"""));
    }

    /// <summary>A call on a captured value — invoked through its method, not through new IL.</summary>
    [Test]
    public void A_method_call_on_a_capture_is_invoked()
    {
        string name = "  Ada  ";

        Assert.That(LowersWithoutCompiling(() => Lower.Where(p => p.Name == name.Trim())),
            Is.EqualTo("""{"name":{"eq":"Ada"}}"""));
    }

    /// <summary>
    /// The counter has to be able to move, or every assertion above is vacuous. A conditional is
    /// a shape the reader does not recognise, so it still compiles — and says so.
    /// </summary>
    [Test]
    public void An_unrecognised_shape_still_compiles_and_is_counted()
    {
        bool flag = true;
        int before = PartialEvaluator.CompiledSubtrees;

        string json = Lower.Where(p => p.Name == (flag ? "Ada" : "Grace"));

        Assert.Multiple(() =>
        {
            Assert.That(json, Is.EqualTo("""{"name":{"eq":"Ada"}}"""));
            Assert.That(PartialEvaluator.CompiledSubtrees, Is.GreaterThan(before),
                "the fallback did not run, so the counter proves nothing");
        });
    }

    [Test]
    public void A_static_member_is_read()
        => Assert.That(LowersWithoutCompiling(() => Lower.Where(p => p.Name == string.Empty)),
            Is.EqualTo("""{"name":{"eq":""}}"""));

    /// <summary>A conversion on the way to the wire, read rather than compiled.</summary>
    [Test]
    public void A_converted_capture_is_read()
    {
        int age = 30;

        Assert.That(LowersWithoutCompiling(() => Lower.Where(p => p.Age > age)),
            Is.EqualTo("""{"age":{"gt":30}}"""));
    }
}
