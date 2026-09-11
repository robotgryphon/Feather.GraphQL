using System.Buffers;
using System.Text;
using System.Text.Json;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Tests.Query;

/// <summary>
/// Renders a plan's variables the way the transport would.
/// </summary>
/// <remarks>
/// Written through the payload's own <c>WriteTo</c> rather than handed to the serializer. A
/// payload is no longer a dictionary the serializer can describe — it is a thing that writes
/// itself — and serializing it reflectively reports its <c>IsEmpty</c> instead of its contents.
/// </remarks>
internal static class VariablePayload
{
    public static string Of(GraphQLQueryPlan plan)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
            plan.Variables.WriteTo(writer);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
