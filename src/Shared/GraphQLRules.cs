namespace Feather.GraphQL.Linq.Rules;

/// <summary>
/// What one type looks like, in the few terms the rules actually need.
/// </summary>
/// <remarks>
/// The point of the indirection: the runtime knows types as <see cref="System.Type"/> and the
/// generator knows them as Roslyn symbols, and neither can see the other's model. Each maps to
/// this, and the rules below are written once.
/// </remarks>
internal readonly struct TypeShape(
    string fullName,
    bool isPrimitive,
    bool isEnum,
    bool isValueType,
    bool isEnumerable,
    bool isFromCoreLibrary)
{
    /// <summary>Namespace-qualified name, without generic arguments or nullability.</summary>
    public string FullName { get; } = fullName;

    public bool IsPrimitive { get; } = isPrimitive;
    public bool IsEnum { get; } = isEnum;
    public bool IsValueType { get; } = isValueType;
    public bool IsEnumerable { get; } = isEnumerable;

    /// <summary>True when the type comes from the framework rather than from user code.</summary>
    public bool IsFromCoreLibrary { get; } = isFromCoreLibrary;
}

/// <summary>
/// The translation rules that both the runtime translator and the generator have to apply
/// identically.
/// </summary>
/// <remarks>
/// <para>
/// Compiled into both by source link rather than referenced as an assembly: an analyzer has to
/// carry its dependencies into the compiler, and linked source costs nothing. That also means
/// this file must stay inside the netstandard2.0 subset — no <c>Lock</c>, and nothing from
/// <c>System.Text.Json</c>, which the analyzer must not drag in.
/// </para>
/// <para>
/// What is shared is the <em>rule</em>, not the walk. Each side still maps its own type model to
/// <see cref="TypeShape"/>, and that mapping is mechanical and visible. A rule, by contrast,
/// drifts silently — adding a scalar type on one side and not the other changes which fields a
/// query asks for, and nothing fails.
/// </para>
/// </remarks>
internal static class GraphQLRules
{
    /// <summary>
    /// Framework types that are GraphQL scalars despite not being primitives.
    /// </summary>
    /// <remarks>
    /// The single most drift-prone thing here: adding <c>Half</c> or <c>Int128</c> to one
    /// implementation and not the other would silently change selection sets.
    /// </remarks>
    private static readonly string[] _scalarTypes =
    [
        "System.String",
        "System.Decimal",
        "System.Guid",
        "System.DateTime",
        "System.DateTimeOffset",
        "System.DateOnly",
        "System.TimeOnly",
        "System.TimeSpan",
        "System.Uri"
    ];

    /// <summary>A value that needs no selection set of its own.</summary>
    public static bool IsScalar(TypeShape shape)
    {
        if (shape.IsPrimitive || shape.IsEnum)
            return true;

        foreach (string name in _scalarTypes)
        {
            if (name == shape.FullName)
                return true;
        }

        // A framework value type nobody listed — a tuple, say — is still a leaf as far as a
        // selection set is concerned. A user's struct is not.
        return !shape.IsEnumerable && shape.IsValueType && shape.IsFromCoreLibrary;
    }

    /// <summary>
    /// The GraphQL field a member maps to: <c>[JsonPropertyName]</c>, then
    /// <c>[DataMember(Name)]</c>, then the member name camel-cased.
    /// </summary>
    /// <param name="memberName">The CLR member's name.</param>
    /// <param name="jsonPropertyName">The <c>[JsonPropertyName]</c> value, if any.</param>
    /// <param name="dataMemberName">The <c>[DataMember(Name)]</c> value, if any.</param>
    public static string FieldName(string memberName, string? jsonPropertyName, string? dataMemberName)
    {
        if (!string.IsNullOrEmpty(jsonPropertyName))
            return jsonPropertyName!;

        if (!string.IsNullOrEmpty(dataMemberName))
            return dataMemberName!;

        return CamelCase(memberName);
    }

    /// <summary>Lower-cases the first character, and only that.</summary>
    public static string CamelCase(string name)
        => name.Length == 0 || !char.IsUpper(name[0])
            ? name
            : char.ToLowerInvariant(name[0]) + name.Substring(1);
}
