using Feather.GraphQL.Linq;
using Feather.GraphQL.Linq.Expressions;

namespace Feather.GraphQL.Linq.Analyzers.Tests;

/// <summary>
/// A precompiled plan carries its paging kind and result operator as numbers, so the two
/// assemblies' enums have to mean the same thing by the same number.
/// </summary>
/// <remarks>
/// The generator cannot reference the runtime's enums — an analyzer targets netstandard2.0 and
/// carries none of the library with it — so it mirrors them. A mirror that drifts would not fail
/// to compile: it would print plans that page the wrong way or reduce with the wrong terminal,
/// which is the kind of wrong that reaches a server before it reaches a test.
/// </remarks>
[TestFixture]
public class PrecompiledPlanAgreementTests
{
    [Test]
    public void The_paging_kinds_agree()
        => Assert.That(Names<Paging>(), Is.EqualTo(Names<PagingKind>()));

    [Test]
    public void The_result_operators_agree()
        => Assert.That(Names<ResultKind>(), Is.EqualTo(Names<QueryResultOperator>()));

    /// <summary>Each member's name against its number, in number order.</summary>
    private static string[] Names<TEnum>() where TEnum : struct, Enum
        => [.. Enum.GetValues<TEnum>()
            .Select(value => (Number: Convert.ToInt32(value), Name: value.ToString()))
            .OrderBy(member => member.Number)
            .Select(member => $"{member.Number}={member.Name}")];
}
