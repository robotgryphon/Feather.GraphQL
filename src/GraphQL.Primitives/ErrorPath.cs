using System.Text.Json.Serialization;
using GraphQL.Converters;

namespace GraphQL;

[JsonConverter(typeof(ErrorPathConverter))]
public class ErrorPath : List<object>
{
    public ErrorPath()
    {
    }

    public ErrorPath(IEnumerable<object> collection) : base(collection)
    {
    }
}
