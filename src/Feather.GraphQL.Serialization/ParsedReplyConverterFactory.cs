using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feather.GraphQL.Serialization;

/// <summary>Supplies the converter for whatever element type a query turned out to have.</summary>
internal sealed class ParsedReplyConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType
           && typeToConvert.GetGenericTypeDefinition() == typeof(ParsedReply<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(
            typeof(ParsedReplyConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}