using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Feather.GraphQL.Linq.Metadata;

namespace Feather.GraphQL.Linq.Materialization;

/// <summary>
/// Deserialization named by the same metadata the selection set was built from.
/// </summary>
/// <remarks>
/// Reading the reply back through a <see cref="JsonNamingPolicy"/> would be a second naming rule
/// running alongside the first. It would agree with the translator on <c>[JsonPropertyName]</c>
/// and on camel-casing, and disagree on <c>[DataMember(Name)]</c> — which
/// <see cref="ReflectionTypeMetadata"/> honours and System.Text.Json does not. Renaming through
/// the type-info resolver keeps one rule: whatever name was requested is the name read back.
/// </remarks>
internal static class GraphQLJsonOptions
{
    /// <summary>Shared and immutable once first used; building it per call would re-reflect every type.</summary>
    public static JsonSerializerOptions Instance { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(ApplyFieldNames);

        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        options.MakeReadOnly();

        return options;
    }

    private static void ApplyFieldNames(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object)
            return;

        var metadata = ReflectionTypeMetadata.For(info.Type);

        foreach (var property in info.Properties)
        {
            // Matching on the CLR member rather than on the current JSON name: by the time a
            // modifier runs, the name has already been through the attributes and the policy.
            if (property.AttributeProvider is MemberInfo member
                && metadata.TryGetField(member.Name, out var field))
                property.Name = field.FieldName;
        }
    }
}
