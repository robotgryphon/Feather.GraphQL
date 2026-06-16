using System.Text.Json.Serialization;
using Feather.GraphQL.Converters;
using JetBrains.Annotations;

namespace Feather.GraphQL.Primitives;

[UsedImplicitly]
[JsonConverter(typeof(ErrorPathConverter))]
public class ErrorPath(IEnumerable<object> collection) : List<object>(collection);
