using System.Reflection;
using System.Runtime.Serialization;

namespace Feather.GraphQL.Linq.Filtering;

/// <summary>
/// What an enum member is called on the wire.
/// </summary>
/// <remarks>
/// A schema spells its enum values in constant case — <c>NORTH_AMERICA</c> — which is not what
/// the CLR member is called. <see cref="EnumMemberAttribute"/> overrides the derivation for a
/// schema that spells one differently.
/// </remarks>
/// <remarks>
/// TODO: pin against a generated schema. HotChocolate's default enum value naming is the CLR
/// member name in CONSTANT_CASE; <c>[EnumMember(Value)]</c> overrides it. Servers that configure
/// a different naming convention will need this to become provider-driven.
/// </remarks>
internal static class GqlEnumNaming
{
    public static string Of(Enum value)
    {
        string name = value.ToString();
        var member = value.GetType().GetField(name, BindingFlags.Public | BindingFlags.Static);

        if (member?.GetCustomAttribute<EnumMemberAttribute>() is { Value.Length: > 0 } attribute)
            return attribute.Value!;

        return ConstantCase(name);
    }

    private static string ConstantCase(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);

        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])
                && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(name[i]));
        }

        return builder.ToString();
    }
}
