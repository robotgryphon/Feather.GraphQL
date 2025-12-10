using GraphQL;

namespace Feather.GraphQL.Tests.Primitives;

public class GraphQLLocationTest
{
    [Test]
    public void Equality2Test()
    {
        var graphQLLocation1 = new GraphQLLocation
        {
                Column = 1,
                Line = 2
        };
        var graphQLLocation2 = new GraphQLLocation
        {
                Column = 1,
                Line = 2
        };
        Assert.That(graphQLLocation1, Is.EqualTo(graphQLLocation2));
    }

    [Test]
    public void EqualityOperatorTest()
    {
        var graphQLLocation1 = new GraphQLLocation
        {
                Column = 1,
                Line = 2
        };
        var graphQLLocation2 = new GraphQLLocation
        {
                Column = 1,
                Line = 2
        };
        Assert.That(graphQLLocation1 == graphQLLocation2, Is.True);
    }

    [Test]
    public void InEqualityTest()
    {
        var graphQLLocation1 = new GraphQLLocation
        {
                Column = 1,
                Line = 2
        };
        var graphQLLocation2 = new GraphQLLocation
        {
                Column = 2,
                Line = 1
        };
        Assert.That(graphQLLocation1, Is.Not.EqualTo(graphQLLocation2));
    }

    [Test]
    public void InEqualityOperatorTest()
    {
        var graphQLLocation1 = new GraphQLLocation
        {
                Column = 1,
                Line = 2
        };
        var graphQLLocation2 = new GraphQLLocation
        {
                Column = 2,
                Line = 1
        };
        Assert.That(graphQLLocation1 != graphQLLocation2, Is.True);
    }

    [Test]
    public void GetHashCodeTest()
    {
        var graphQLLocation1 = new GraphQLLocation
        {
                Column = 1,
                Line = 2
        };
        var graphQLLocation2 = new GraphQLLocation
        {
                Column = 1,
                Line = 2
        };
        Assert.That(graphQLLocation1.GetHashCode(), Is.EqualTo(graphQLLocation2.GetHashCode()));
    }
}
