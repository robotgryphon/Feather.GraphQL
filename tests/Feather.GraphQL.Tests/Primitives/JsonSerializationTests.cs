using System.Text.Json;

namespace Feather.GraphQL.Tests.Primitives;

public class JsonSerializationTests
{
    [Test]
    public void WebSocketResponseDeserialization()
    {
        var testObject = new ExtendedTestObject
        {
                Id = "test",
                OtherData = "this is some other stuff"
        };
        string json = JsonSerializer.Serialize(testObject);
        var deserialized = JsonSerializer.Deserialize<TestObject>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized.Id, Is.EqualTo("test"));

        var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
        var childObject = (JsonElement)dict!["ChildObject"];

        Assert.That(childObject.GetProperty("Id").GetString(), Is.EqualTo(testObject.ChildObject.Id));
    }

    public class TestObject
    {
        public string? Id { get; set; }
    }

    public class ExtendedTestObject : TestObject
    {
        public string? OtherData { get; set; }

        public TestObject ChildObject { get; set; } = new() { Id = "1337" };
    }
}
