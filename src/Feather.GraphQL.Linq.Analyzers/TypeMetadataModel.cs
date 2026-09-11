using System.Collections.Immutable;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>One CLR member and the GraphQL field it maps to, as the generator sees it.</summary>
/// <remarks>
/// Value-equatable on purpose: an incremental generator re-runs whenever its input model changes,
/// so a model that compares by reference would rebuild every keystroke.
/// </remarks>
internal sealed class FieldModel(string clrName, string fieldName, string clrType, bool isIgnored)
    : IEquatable<FieldModel>
{
    public string ClrName { get; } = clrName;
    public string FieldName { get; } = fieldName;
    public string ClrType { get; } = clrType;
    public bool IsIgnored { get; } = isIgnored;

    public bool Equals(FieldModel? other)
        => other is not null
            && ClrName == other.ClrName
            && FieldName == other.FieldName
            && ClrType == other.ClrType
            && IsIgnored == other.IsIgnored;

    public override bool Equals(object? obj) => Equals(obj as FieldModel);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = ClrName.GetHashCode();
            hash = (hash * 397) ^ FieldName.GetHashCode();
            hash = (hash * 397) ^ ClrType.GetHashCode();
            return (hash * 397) ^ IsIgnored.GetHashCode();
        }
    }
}

/// <summary>A type's whole field table, ready to emit.</summary>
internal sealed class TypeModel(string qualifiedName, string hintName, ImmutableArray<FieldModel> fields)
    : IEquatable<TypeModel>
{
    /// <summary>The type, fully qualified, as it will appear in <c>typeof(...)</c>.</summary>
    public string QualifiedName { get; } = qualifiedName;

    /// <summary>A C# identifier derived from the type, naming its generated class.</summary>
    public string HintName { get; } = hintName;

    public ImmutableArray<FieldModel> Fields { get; } = fields;

    public bool Equals(TypeModel? other)
        => other is not null
            && QualifiedName == other.QualifiedName
            && Fields.SequenceEqual(other.Fields);

    public override bool Equals(object? obj) => Equals(obj as TypeModel);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = QualifiedName.GetHashCode();
            foreach (var field in Fields)
                hash = (hash * 397) ^ field.GetHashCode();

            return hash;
        }
    }
}
