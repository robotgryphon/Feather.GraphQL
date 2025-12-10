using GraphQL.Request;

namespace Feather.GraphQL.Tests.Primitives;

public class GraphQLRequestTest
{
    [Test]
    public void ConstructorTest()
    {
        var graphQLRequest = new GraphQLRequest("{hero{name}}");
        Assert.That(graphQLRequest.Query, Is.Not.Null);
        Assert.That(graphQLRequest.OperationName, Is.Null);
        Assert.That(graphQLRequest.Variables, Is.Null);
    }

    [Test]
    public void ConstructorExtendedTest()
    {
        var graphQLRequest = new GraphQLRequest("{hero{name}}", new { varName = "varValue" }, "operationName");
        Assert.That(graphQLRequest.Query, Is.Not.Null);
        Assert.That(graphQLRequest.OperationName, Is.Not.Null);
        Assert.That(graphQLRequest.Variables, Is.Not.Null);
    }

    [Test]
    public void Equality1Test()
    {
        var graphQLRequest = new GraphQLRequest("{hero{name}}");
        Assert.That(graphQLRequest.Equals(graphQLRequest), Is.True);
    }

    [Test]
    public void Equality2Test()
    {
        var graphQLRequest1 = new GraphQLRequest("{hero{name}}");
        var graphQLRequest2 = new GraphQLRequest("{hero{name}}");
        Assert.That(graphQLRequest1.Equals(graphQLRequest2), Is.True);
    }

    [Test]
    public void SameQuerySameVariablesAreEqual()
    {
        var graphQLRequest1 = new GraphQLRequest("{hero{name}}", new { varName = "varValue" }, "operationName");
        var graphQLRequest2 = new GraphQLRequest("{hero{name}}", new { varName = "varValue" }, "operationName");
        Assert.That(graphQLRequest1.Equals(graphQLRequest2), Is.True);
    }

    [Test]
    public void SameQueryDifferentVariablesAreNotEqual()
    {
        var graphQLRequest1 = new GraphQLRequest("{hero{name}}", new { varName = "varValue1" }, "operationName");
        var graphQLRequest2 = new GraphQLRequest("{hero{name}}", new { varName = "varValue2" }, "operationName");
        Assert.That(graphQLRequest1, Is.Not.EqualTo(graphQLRequest2));
    }

    [Test]
    public void EqualityOperatorTest()
    {
        var graphQLRequest1 = new GraphQLRequest("{hero{name}}", new { varName = "varValue" }, "operationName");
        var graphQLRequest2 = new GraphQLRequest("{hero{name}}", new { varName = "varValue" }, "operationName");
        Assert.That(graphQLRequest1, Is.EqualTo(graphQLRequest2));
    }

    [Test]
    public void InEquality1Test()
    {
        var graphQLRequest1 = new GraphQLRequest("{hero{name1}}");
        var graphQLRequest2 = new GraphQLRequest("{hero{name2}}");
        Assert.That(graphQLRequest1.Equals(graphQLRequest2), Is.False);
    }

    [Test]
    public void InEquality2Test()
    {
        var graphQLRequest1 = new GraphQLRequest("{hero{name}}", new { varName = "varValue1" }, "operationName");
        var graphQLRequest2 = new GraphQLRequest("{hero{name}}", new { varName = "varValue2" }, "operationName");
        Assert.That(graphQLRequest1.Equals(graphQLRequest2), Is.False);
    }

    [Test]
    public void InEqualityOperatorTest()
    {
        var graphQLRequest1 = new GraphQLRequest("{hero{name1}}");
        var graphQLRequest2 = new GraphQLRequest("{hero{name2}}");
        Assert.That(graphQLRequest1, Is.Not.EqualTo(graphQLRequest2));
    }

    [Test]
    public void GetHashCode1Test()
    {
        var graphQLRequest1 = new GraphQLRequest("{hero{name}}");
        var graphQLRequest2 = new GraphQLRequest("{hero{name}}");
        Assert.That(graphQLRequest1.GetHashCode(), Is.EqualTo(graphQLRequest2.GetHashCode()));
    }

    [Test]
    public void GetHashCode2Test()
    {
        var graphQLRequest1 = new GraphQLRequest("{hero{name}}", new { varName = "varValue" }, "operationName");
        var graphQLRequest2 = new GraphQLRequest("{hero{name}}", new { varName = "varValue" }, "operationName");
        Assert.That(graphQLRequest1.GetHashCode(), Is.EqualTo(graphQLRequest2.GetHashCode()));
    }

    [Test]
    public void GetHashCode3Test()
    {
        var graphQLRequest1 = new GraphQLRequest("{hero{name}}", new { varName = "varValue1" }, "operationName");
        var graphQLRequest2 = new GraphQLRequest("{hero{name}}", new { varName = "varValue2" }, "operationName");
        Assert.That(graphQLRequest1.GetHashCode(), Is.Not.EqualTo(graphQLRequest2.GetHashCode()));
    }

    [Test]
    public void PropertyQueryGetTest()
    {
        var graphQLRequest = new GraphQLRequest("{hero{name}}", new { varName = "varValue1" }, "operationName");
        Assert.That(graphQLRequest.Query, Is.EqualTo("{hero{name}}"));
    }

    [Test]
    public void PropertyQuerySetTest()
    {
        var graphQLRequest =
                new GraphQLRequest("{hero{name}}", new { varName = "varValue1" }, "operationName")
                {
                        Query = "{hero{name2}}"
                };
        Assert.That(graphQLRequest.Query, Is.EqualTo("{hero{name2}}"));
    }

    [Test]
    public void PropertyOperationNameGetTest()
    {
        var graphQLRequest = new GraphQLRequest("{hero{name}}", new { varName = "varValue" }, "operationName");
        Assert.That(graphQLRequest.OperationName, Is.EqualTo("operationName"));
    }

    [Test]
    public void PropertyOperationNameNullGetTest()
    {
        var graphQLRequest = new GraphQLRequest("{hero{name}}");
        Assert.That(graphQLRequest.OperationName, Is.Null);
    }

    [Test]
    public void PropertyOperationNameSetTest()
    {
        var graphQLRequest = new GraphQLRequest("{hero{name}}", new { varName = "varValue" }, "operationName1")
        {
                OperationName = "operationName2"
        };
        Assert.That(graphQLRequest.OperationName, Is.EqualTo("operationName2"));
    }

    [Test]
    public void PropertyVariableGetTest()
    {
        var graphQLRequest = new GraphQLRequest("{hero{name}}", new { varName = "varValue" }, "operationName");
        Assert.That(new { varName = "varValue" }, Is.EqualTo(graphQLRequest.Variables));
    }

    [Test]
    public void PropertyVariableNullGetTest()
    {
        var graphQLRequest = new GraphQLRequest("{hero{name}}");
        Assert.That(graphQLRequest.Variables, Is.Null);
    }

    [Test]
    public void PropertyVariableSetTest()
    {
        var graphQLRequest = new GraphQLRequest("{hero{name}}", new { varName = "varValue1" }, "operationName1")
        {
                Variables = new { varName = "varValue2" }
        };
        Assert.That(new { varName = "varValue2" }, Is.EqualTo(graphQLRequest.Variables));
    }
}
