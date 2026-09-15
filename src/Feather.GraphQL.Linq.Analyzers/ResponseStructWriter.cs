using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// What a query's root field answers with, and therefore which reader its reply needs.
/// </summary>
/// <remarks>
/// The document and the reader are printed by the same compilation from the same chain, so this
/// is not a guess about the reply — it is the same decision that chose what to select, carried to
/// the code that reads what comes back. One shape is written and the others are not emitted at
/// all, which is why a reader never asks a reply which of them it is.
/// </remarks>
public enum ReplyShape
{
    /// <summary>Un-paged: <c>{ data: { people: [ … ] } }</c>.</summary>
    List,

    /// <summary>Cursor-paged, as <c>[UsePaging]</c> answers: the rows are in <c>nodes</c>.</summary>
    Cursor,

    /// <summary>Offset-paged, as <c>[UseOffsetPaging]</c> answers: the rows are in <c>items</c>.</summary>
    Offset,

    /// <summary>A count: the connection's <c>totalCount</c>, and no rows selected at all.</summary>
    Count,

    /// <summary>One object rather than a list of them: <c>{ data: { person: { … } } }</c>.</summary>
    Single
}

/// <summary>
/// Writes a query's reply as C# types: one struct per object the payload carries, and the code
/// that reads them.
/// </summary>
/// <remarks>
/// <para>
/// A <c>JsonSerializerContext</c> describes types, and a type is more than any one query asks
/// for. This describes a <em>reply</em>. The selection set is known at build time, so the shape of
/// the JSON is known exactly — which fields, in which order, nested how — and that shape can be a
/// set of structs with a field per selected field and nothing else.
/// </para>
/// <para>
/// What that buys is everything a general reader has to do and this does not: no property map to
/// look a name up in, no contract to resolve, no members for fields the document never asked for,
/// and no object per row — the rows are structs, so an array of them is one allocation rather than
/// one per row plus the array.
/// </para>
/// <para>
/// The emitted code calls nothing of this library's. It is <c>Utf8JsonReader</c> and the BCL:
/// <c>Read</c>, <c>TokenType</c>, <c>ValueTextEquals</c> against a UTF-8 literal, and the getter
/// for the field's own type. There is no layer to step through when reading it and nothing to
/// resolve when running it — which is the whole of what compiling a reader is for.
/// </para>
/// </remarks>
internal static class ResponseStructWriter
{
    /// <summary>
    /// One object the reply carries, and what reading it produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="Name"/> is what the reader is called and what it returns. When
    /// <paramref name="Target"/> is null that is a generated struct, declared here with a field
    /// per selected field; when it is set, the reader builds the caller's own type instead and no
    /// struct is generated at all.
    /// </para>
    /// <para>
    /// Which of the two is decided one level up, by whether the chain projects. A chain that does
    /// not is asking for the queried type, so reading into anything else would mean building a row
    /// to copy out of and throw away. A chain that does needs the payload as it arrived, because
    /// the projection is written against those fields.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <c>Reader</c> is what the class holding this model's <c>Read</c> is called: the struct
    /// itself when one is generated, and a name of its own when the caller's type is built
    /// instead — one no other query sharing the file will take.
    /// </remarks>
    internal sealed record Model(
        string Name,
        IReadOnlyList<Member> Members,
        string? Target = null,
        string Reader = "");

    /// <summary>
    /// One field of a generated struct, as the payload carries it.
    /// </summary>
    /// <param name="Field">The name on the wire, which the reader compares against.</param>
    /// <param name="Property">The name in C#, which is the queried type's own.</param>
    /// <param name="Type">How the member is declared.</param>
    /// <param name="Read">The expression that reads one of it, given a reader on its value.</param>
    /// <param name="Nested">The struct it holds, when it holds one.</param>
    /// <param name="IsList">Whether the payload carries many of it.</param>
    /// <param name="Element">
    /// The C# spelling of one row of it, when it carries many — which is the member's own element
    /// type where the rows are read as that, and a generated struct's name where they are mirrored.
    /// </param>
    /// <param name="Fill">
    /// How the accumulated rows become what the member is declared as: the list itself where the
    /// declaration is already satisfied by one, and a constructor or a collection expression
    /// otherwise.
    /// </param>
    /// <param name="Converter">
    /// The converter the model declared for it, when it declared one. A member with a converter is
    /// read through it rather than by a getter, whatever its type would otherwise have allowed.
    /// </param>
    /// <param name="Factory">Whether that converter is a factory, and so has to make one first.</param>
    internal sealed record Member(
        string Field,
        string Property,
        string Type,
        string Read,
        Model? Nested,
        bool IsList,
        string? Converter = null,
        bool Factory = false,
        string Element = "",
        string Fill = "");

    /// <summary>
    /// Describes the structs one reply needs, or null when its shape cannot be modelled.
    /// </summary>
    /// <remarks>
    /// Declines rather than guesses, as everything else in this assembly does: a field whose type
    /// has no read that is certainly right is a field this cannot model, and a query carrying one
    /// keeps the reading it already had.
    /// </remarks>
    /// <param name="prefix">What the generated names begin with — the method's own name.</param>
    /// <param name="element">The type the selection set was built against.</param>
    /// <param name="selection">The fields the document asked for.</param>
    /// <param name="direct">
    /// True when the reader should build <paramref name="element"/> itself rather than a struct
    /// mirroring the payload — which is what a chain with no projection is asking for, and saves
    /// the copy a mirror would only exist to be copied out of.
    /// </param>
    public static Model? Describe(
        string prefix,
        ITypeSymbol element,
        SelectionSetWriter.Node selection,
        bool direct)
        => Describe(prefix, element, selection, direct, out _);

    /// <inheritdoc cref="Describe(string, ITypeSymbol, SelectionSetWriter.Node, bool)"/>
    /// <remarks>
    /// <c>refusal</c> is which field the reply could not be modelled from, and why. Set only
    /// where null is returned, and pointing at where that field was named whenever the source
    /// named it — a document in an attribute names nothing this file can point at, and leaves the
    /// location null rather than the reason unsaid.
    /// </remarks>
    public static Model? Describe(
        string prefix,
        ITypeSymbol element,
        SelectionSetWriter.Node selection,
        bool direct,
        out Refusal? refusal)
    {
        var refusals = new Refusals();

        var model = Describe(
            prefix, "Row", element, selection, direct, new HashSet<string>(StringComparer.Ordinal), refusals);

        refusal = model is null
            ? refusals.First ?? new Refusal($"its reply holds a field of '{element.Name}' with no certain read", null)
            : null;

        return model;
    }

    private static Model? Describe(
        string prefix,
        string suffix,
        ITypeSymbol element,
        SelectionSetWriter.Node selection,
        bool direct,
        HashSet<string> taken,
        Refusals refusals)
    {
        // Records why one field of this reply could not be modelled, against wherever the
        // selection says that field was named.
        Model? Refuse(string field, string reason)
        {
            refusals.Note(new Refusal(reason, selection.Named(field)));
            return null;
        }

        // Reading into the caller's type means naming it, but the reader that builds it still
        // needs a name of its own — and one no other query sharing the file will take.
        string? target = direct ? Declared(element) : null;
        string name = direct ? target! : Unique(prefix + "_" + suffix, taken);
        string reader = direct ? Unique(prefix + "_" + suffix + "Reader", taken) : name;

        // A type that cannot be built and filled is one only a mirror can be read into.
        if (direct && !Constructible(element))
        {
            refusals.Note(new Refusal(
                $"'{element.Name}' cannot be built by the generated reader — it needs a public "
                + "constructor taking nothing, and a settable property for every field the query asks for",
                null));

            return null;
        }

        var members = new List<Member>();
        var properties = GraphQLTypeFacts.Properties(element)
            .ToDictionary(GraphQLTypeFacts.FieldName, x => x, StringComparer.Ordinal);

        foreach (string field in selection.Order)
        {
            if (!properties.TryGetValue(field, out var property))
            {
                return Refuse(field,
                    $"the reply carries '{field}', which '{element.Name}' has no property for, so there is "
                    + "nothing to read it into");
            }

            var child = selection[field];
            var type = property.Type;

            // A converter the model declared is how that member is read, whatever its type is and
            // whatever the query asked for beneath it: the model said this is what reading it
            // means, and a reader that decided otherwise would be reading something else.
            if (Converter(property) is var (converter, factory) && converter is not null)
            {
                members.Add(new Member(
                    field,
                    property.Name,
                    Declared(type),
                    Converted(Local(property.Name), Declared(type)),
                    null,
                    false,
                    converter,
                    factory));

                continue;
            }

            var item = GraphQLTypeFacts.ElementType(type);
            bool list = item is not null && !GraphQLTypeFacts.IsScalar(type);

            if (list)
                type = item!;

            if (child.Order.Count > 0)
            {
                // A nested object is built as the caller's own type wherever it can be, even when
                // the row around it is a mirror. The mirror exists so a row has exactly the fields
                // the query selected; a member does not need that, and building it as declared is
                // what lets a projection pass the whole object through rather than only its
                // scalars. Where the type cannot be built — no accessible way to set it — the
                // mirror is still there to fall back on.
                // The mirror is the fallback, so the caller's own type failing to be built is not
                // the reason for anything — only a refusal that survived the mirror too is.
                var nested = Describe(prefix, property.Name, type, child, direct: true, taken, new Refusals())
                    ?? Describe(prefix, property.Name, type, child, direct, taken, refusals);

                if (nested is null)
                {
                    return Refuse(field,
                        $"nothing under '{property.Name}' could be read into a '{Readable(type)}'");
                }

                if (!list)
                {
                    members.Add(new Member(field, property.Name, nested.Name, "", nested, false));
                    continue;
                }

                if (Many(property, nested.Name, direct, out string held, out string fill) is { } refused)
                    return Refuse(field, refused);

                members.Add(new Member(
                    field, property.Name, held, "", nested, true, Element: nested.Name, Fill: fill));

                continue;
            }

            if (Read(type) is not { } read)
            {
                return Refuse(field,
                    $"'{property.Name}' is a '{Readable(type)}', which the generated reader has no read for "
                    + "that is certainly right — give the member a [JsonConverter] of its own, or leave it "
                    + "out of the projection");
            }

            if (!list)
            {
                members.Add(new Member(field, property.Name, Declared(property.Type), read, null, false));
                continue;
            }

            // Declared as the member is where the reader fills the member itself, and as a
            // read-only list where it fills a row of its own — while the read is of one element,
            // which is what the loop over the values calls.
            string one = Declared(type);

            if (Many(property, one, direct, out string carried, out string filled) is { } why)
                return Refuse(field, why);

            members.Add(new Member(
                field, property.Name, carried, read, null, true, Element: one, Fill: filled));
        }

        if (members.Count == 0)
        {
            refusals.Note(new Refusal(
                $"nothing was selected from '{element.Name}', and a reply with no fields has nothing to read",
                null));

            return null;
        }

        return new Model(name, members, target, reader);
    }

    /// <summary>
    /// How a member carrying many of something is declared, and how the rows reach it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two different questions wearing one shape. Where the reader fills the caller's own type,
    /// the member is declared as the caller declared it and the rows have to be turned into that
    /// — by a constructor or a collection expression, and where neither is recognised the query
    /// is refused rather than a conversion guessed at.
    /// </para>
    /// <para>
    /// Where it fills a row of its own, the declaration is this file's to choose, and it chooses
    /// a read-only list: a row is a payload and nothing may add to it. The rows reach it as they
    /// are, since a list is one already.
    /// </para>
    /// </remarks>
    /// <returns>Null when the member can be filled, and why it cannot when it cannot.</returns>
    private static string? Many(
        IPropertySymbol property,
        string element,
        bool direct,
        out string held,
        out string fill)
    {
        string items = Local(property.Name) + "Items";

        // A row of this file's own holds a read-only list wherever the member it mirrors is
        // declared as something one already is, and the rows reach it without being copied.
        if (!direct && CollectionShapes.ReadOnlyHolds(property.Type))
        {
            held = CollectionShapes.ReadOnly(element);
            fill = items;

            return null;
        }

        string? spelled = direct ? Declared(property.Type) : CollectionShapes.Spelled(property.Type, element);

        if (spelled is not null && CollectionShapes.Convert(property.Type, element, items) is { } written)
        {
            held = spelled;
            fill = written;

            return null;
        }

        held = "";
        fill = "";

        string row = Readable(GraphQLTypeFacts.ElementType(property.Type)!);

        return $"'{property.Name}' is declared as '{Readable(property.Type)}', and the reader has no way "
            + "to make one: it reads the rows into a read-only list, and nothing turns one of those into "
            + $"a '{Readable(property.Type)}' — neither a public constructor taking a collection nor a "
            + $"collection expression. Declare it 'IReadOnlyCollection<{row}>', which the rows satisfy as "
            + "they are, or give it a constructor taking 'IEnumerable<" + row + ">'";
    }

    /// <summary>A type as a message names it: the C# spelling, with no <c>global::</c> on it.</summary>
    private static string Readable(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    /// <summary>
    /// The converter a model declared for a member, on the member or on its type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what lets a generated reader read anything the serializer could: a date in another
    /// format, an enum by its name, a money type with a converter of its own. The model said how
    /// that member is written and read, and copying the instruction over is the only way a second
    /// reader can agree with the first about it.
    /// </para>
    /// <para>
    /// A converter registered globally instead — on the options rather than on the model — cannot
    /// be seen from here, and keeping those in step with what a generated reader expects is the
    /// consumer's to do.
    /// </para>
    /// </remarks>
    private static (string? Converter, bool Factory) Converter(IPropertySymbol property)
    {
        var declared = Attribute(property.GetAttributes())
            ?? Attribute(GraphQLTypeFacts.UnwrapNullable(property.Type).GetAttributes());

        if (declared is not INamedTypeSymbol converter)
            return (null, false);

        // A converter has to be one this can build: a factory is asked for one, and anything
        // without a constructor taking nothing cannot be had at all.
        if (!converter.InstanceConstructors.Any(x => x.Parameters.Length == 0
            && x.DeclaredAccessibility == Accessibility.Public))
            return (null, false);

        for (var type = converter; type is not null; type = type.BaseType)
        {
            switch (type.ToDisplayString())
            {
                case "System.Text.Json.Serialization.JsonConverterFactory":
                    return (converter.ToDisplayString(_qualified), true);

                case string name when name.StartsWith(
                    "System.Text.Json.Serialization.JsonConverter<", StringComparison.Ordinal):
                    return (converter.ToDisplayString(_qualified), false);
            }
        }

        return (null, false);
    }

    /// <summary>The type named by a <c>[JsonConverter]</c> among these attributes, if any.</summary>
    private static ITypeSymbol? Attribute(System.Collections.Immutable.ImmutableArray<AttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeClass?.ToDisplayString()
                    == "System.Text.Json.Serialization.JsonConverterAttribute"
                && attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is ITypeSymbol converter)
                return converter;
        }

        return null;
    }

    /// <summary>Reading one value through the converter held for that member.</summary>
    private static string Converted(string local, string type)
        => local + "Converter.Read(ref reader, typeof(" + type + "), "
            + "global::Feather.GraphQL.Metadata.GraphQLJsonContextRegistry.Options)";

    private static readonly SymbolDisplayFormat _qualified =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            & ~SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>
    /// Whether the caller's own type can be built and filled by a generated reader.
    /// </summary>
    /// <remarks>
    /// It needs a constructor taking nothing and a setter for every field the query asked for.
    /// A type without them is not refused outright — the reply is mirrored in a struct instead,
    /// and whatever the chain does next does the building.
    /// </remarks>
    private static bool Constructible(ITypeSymbol element)
        => element is INamedTypeSymbol named
            && named.InstanceConstructors.Any(x => x.Parameters.Length == 0
                && x.DeclaredAccessibility == Accessibility.Public);

    /// <summary>
    /// The same model under a different prefix, for when two queries wanted the same one.
    /// </summary>
    /// <remarks>
    /// A model is described one query at a time, so it cannot know that another method somewhere
    /// else in the assembly is called what this one is called. Where their generated types would
    /// land in the same file, the second is renamed here rather than by giving every query a name
    /// nobody asked for — the method's own name is what makes generated code findable from the
    /// code that caused it, and it is worth keeping for all but the query that clashes.
    /// </remarks>
    public static Model Rename(Model model, string from, string to)
    {
        string Renamed(string name)
            => name.StartsWith(from + "_", StringComparison.Ordinal)
                ? to + name.Substring(from.Length)
                : name;

        var members = new List<Member>(model.Members.Count);

        foreach (var member in model.Members)
        {
            members.Add(member.Nested is { } nested
                ? member with { Type = Renamed(member.Type), Nested = Rename(nested, from, to) }
                : member);
        }

        return model with { Name = Renamed(model.Name), Reader = Renamed(model.Reader), Members = members };
    }

    /// <summary>A name nothing else generated for this query has taken.</summary>
    private static string Unique(string name, HashSet<string> taken)
    {
        if (taken.Add(name))
            return name;

        for (int i = 2; ; i++)
        {
            string candidate = name + i;

            if (taken.Add(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// The expression that reads one value of this type, or null when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written against the reader standing on the value. A string takes the getter as it is —
    /// <c>GetString</c> answers null for a null, which is what deserializing would have produced.
    /// A number cannot: its getter throws on a null, so the token is checked first and the type's
    /// own empty stands in, again matching what deserializing would have left behind.
    /// </para>
    /// <para>
    /// The set is deliberately small and deliberately checked. A date in another format, an enum
    /// by its name, a type with a converter of the caller's own are all things a bespoke reader
    /// gets quietly wrong, so they are declined instead of guessed at.
    /// </para>
    /// </remarks>
    private static string? Read(ITypeSymbol type)
    {
        bool nullable = type.NullableAnnotation == NullableAnnotation.Annotated
            || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

        var underlying = GraphQLTypeFacts.UnwrapNullable(type);

        string? getter = underlying.SpecialType switch
        {
            SpecialType.System_String => "GetString()",
            SpecialType.System_Boolean => "GetBoolean()",
            SpecialType.System_Byte => "GetByte()",
            SpecialType.System_SByte => "GetSByte()",
            SpecialType.System_Int16 => "GetInt16()",
            SpecialType.System_Int32 => "GetInt32()",
            SpecialType.System_Int64 => "GetInt64()",
            SpecialType.System_UInt16 => "GetUInt16()",
            SpecialType.System_UInt32 => "GetUInt32()",
            SpecialType.System_UInt64 => "GetUInt64()",
            SpecialType.System_Single => "GetSingle()",
            SpecialType.System_Double => "GetDouble()",
            SpecialType.System_Decimal => "GetDecimal()",

            // Written as text, and the reader's own getters parse exactly the formats the
            // serializer writes: ISO 8601 for the three date shapes, "D" for a Guid.
            SpecialType.System_DateTime => "GetDateTime()",
            _ => underlying.ToDisplayString() switch
            {
                "System.DateTimeOffset" => "GetDateTimeOffset()",
                "System.Guid" => "GetGuid()",
                _ => null
            }
        };

        if (getter is null)
            return null;

        // A string's getter already answers null for a null; nothing else's does. Where the
        // member was declared as not-null the null is forgiven rather than guarded, because
        // deserializing into the same member would have assigned it too — the compiled path has
        // to mean what the path it replaced meant, including about this.
        if (getter == "GetString()")
            return nullable ? "reader.GetString()" : "reader.GetString()!";

        string empty = nullable ? "null" : "default";

        // Everything but a string throws on a null rather than answering one, so the token is
        // checked first and the type's own empty stands in — which is what deserializing into the
        // same member would have left behind.
        const string nullToken = "global::System.Text.Json.JsonTokenType.Null";

        return $"reader.TokenType == {nullToken} ? {empty} : reader.{getter}";
    }

    /// <summary>How a leaf member is declared: as the queried type declares it.</summary>
    private static string Declared(ITypeSymbol type)
        => type.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                | SymbolDisplayMiscellaneousOptions.UseSpecialTypes));

    /// <summary>
    /// Writes every struct the reply needs, innermost first, and the reply itself.
    /// </summary>
    /// <remarks>
    /// A null model is a reply with no rows to read — a counting query, which asks the connection
    /// how many there are and selects none of them. It still has an envelope, and that is all this
    /// writes for it.
    /// </remarks>
    /// <remarks>
    /// <paramref name="shape"/> says what the root field answers with, which cannot be told from
    /// the reply — a connection is an object, and so is a single row — but is known to the
    /// compilation that printed the document, and is settled before any reply exists.
    /// </remarks>
    public static void Write(
        StringBuilder builder, Model? model, string prefix, ReplyShape shape = ReplyShape.List)
    {
        if (model is not null)
            Structs(builder, model);

        Reply(builder, model, prefix, shape);
    }

    private static void Structs(StringBuilder builder, Model model)
    {
        foreach (var member in model.Members)
        {
            if (member.Nested is { } nested)
                Structs(builder, nested);
        }

        // Reading into the caller's own type declares nothing; only the reader is generated.
        if (model.Target is not null)
        {
            builder.Append("    /// <summary>Reads one <c>").Append(model.Name)
                .Append("</c> out of the reply, field by field.</summary>\n")
                .Append("    file static class ").Append(model.Reader).Append('\n')
                .Append("    {\n");

            Converters(builder, model);
            Reader(builder, model);

            builder.Append("    }\n\n");

            return;
        }

        builder.Append("    /// <summary>One <c>").Append(model.Name)
            .Append("</c>, with a field for each the query asked for and none besides.</summary>\n")
            .Append("    file readonly struct ").Append(model.Name).Append('\n')
            .Append("    {\n");

        foreach (var member in model.Members)
        {
            builder.Append("        public readonly ").Append(member.Type).Append(' ')
                .Append(member.Property).Append(";\n");
        }

        Constructor(builder, model);
        Converters(builder, model);
        Reader(builder, model);

        builder.Append("    }\n\n");
    }

    private static void Constructor(StringBuilder builder, Model model)
    {
        builder.Append('\n').Append("        public ").Append(model.Name).Append('(')
            .Append(string.Join(", ", model.Members.Select(x => x.Type + " " + Local(x))))
            .Append(")\n        {\n");

        foreach (var member in model.Members)
            builder.Append("            ").Append(member.Property).Append(" = ").Append(Local(member)).Append(";\n");

        builder.Append("        }\n");
    }

    /// <summary>
    /// Declares the converters this model reads through, built once rather than per row.
    /// </summary>
    /// <remarks>
    /// A factory is asked for its converter here too, for the same reason: whatever it costs to
    /// make one, it costs once for the assembly rather than once for every row of every reply.
    /// </remarks>
    private static void Converters(StringBuilder builder, Model model)
    {
        foreach (var member in model.Members)
        {
            if (member.Converter is not { } converter)
                continue;

            builder.Append("\n        private static readonly global::System.Text.Json.Serialization")
                .Append(".JsonConverter<").Append(member.Type).Append("> ").Append(Local(member))
                .Append("Converter =\n            ");

            if (member.Factory)
            {
                builder.Append("(global::System.Text.Json.Serialization.JsonConverter<")
                    .Append(member.Type).Append(">)new ").Append(converter)
                    .Append("().CreateConverter(\n                typeof(").Append(member.Type)
                    .Append("), global::Feather.GraphQL.Metadata.GraphQLJsonContextRegistry.Options)!;\n");
            }
            else
            {
                builder.Append("new ").Append(converter).Append("();\n");
            }
        }
    }

    /// <summary>
    /// Reads one of these, from its <c>{</c> to its <c>}</c>.
    /// </summary>
    /// <remarks>
    /// Values land in locals and the struct is built at the end, rather than being assigned into
    /// as they arrive: a server writes a reply's fields in whatever order it likes, and a struct
    /// with readonly fields cannot be filled piecemeal anyway.
    /// </remarks>
    private static void Reader(StringBuilder builder, Model model)
    {
        builder.Append('\n')
            .Append("        /// <summary>Reads one, from its opening brace to its closing one.</summary>\n")
            .Append("        public static ").Append(model.Name)
            .Append(" Read(ref global::System.Text.Json.Utf8JsonReader reader)\n")
            .Append("        {\n");

        foreach (var member in model.Members)
        {
            builder.Append("            ").Append(member.Type).Append(' ').Append(Local(member))
                .Append(" = default").Append(member.Type.EndsWith("?", StringComparison.Ordinal)
                    || !member.Type.EndsWith("[]", StringComparison.Ordinal) ? "" : "").Append("!;\n");
        }

        builder.Append('\n')
            .Append("            while (reader.Read() && reader.TokenType == ")
            .Append("global::System.Text.Json.JsonTokenType.PropertyName)\n")
            .Append("            {\n");

        bool first = true;

        foreach (var member in model.Members)
        {
            builder.Append("                ").Append(first ? "if" : "else if")
                .Append(" (reader.ValueTextEquals(\"").Append(member.Field).Append("\"u8))\n")
                .Append("                {\n")
                .Append("                    reader.Read();\n");

            first = false;

            if (member.Nested is { } nested)
                Nested(builder, member, nested);
            else if (member.IsList)
                Scalars(builder, member);
            else
                builder.Append("                    ").Append(Local(member)).Append(" = ").Append(member.Read).Append(";\n");

            builder.Append("                }\n");
        }

        builder.Append("                else\n")
            .Append("                {\n")
            .Append("                    reader.Read();\n")
            .Append("                    reader.Skip();\n")
            .Append("                }\n")
            .Append("            }\n\n");

        if (model.Target is null)
        {
            builder.Append("            return new ").Append(model.Name).Append('(')
                .Append(string.Join(", ", model.Members.Select(Local))).Append(");\n");
        }
        else
        {
            // The caller's type, built and filled: an initializer rather than a constructor,
            // because what it takes is its own business and its setters are what we know.
            builder.Append("            return new ").Append(model.Target).Append("\n")
                .Append("            {\n");

            for (int i = 0; i < model.Members.Count; i++)
            {
                builder.Append("                ").Append(model.Members[i].Property).Append(" = ")
                    .Append(Local(model.Members[i]))
                    .Append(i == model.Members.Count - 1 ? "" : ",").Append("\n");
            }

            builder.Append("            };\n");
        }

        builder.Append("        }\n");
    }



    /// <summary>
    /// Reads a list of scalars — a leaf on the wire, but an array all the same.
    /// </summary>
    /// <remarks>
    /// The loop advances onto each element itself, so the read is called with the reader already
    /// standing on the value rather than on a name.
    /// </remarks>
    private static void Scalars(StringBuilder builder, Member member)
    {
        builder.Append("                    var ").Append(Local(member)).Append("Items = ")
            .Append("new global::System.Collections.Generic.List<").Append(member.Element).Append(">();\n\n")
            .Append("                    while (reader.Read() && reader.TokenType != ")
            .Append("global::System.Text.Json.JsonTokenType.EndArray)\n")
            .Append("                        ").Append(Local(member)).Append("Items.Add(")
            .Append(member.Read).Append(");\n\n")
            .Append("                    ").Append(Local(member)).Append(" = ")
            .Append(member.Fill).Append(";\n");
    }

    /// <summary>Reads a nested object, or a list of them, into its own struct.</summary>
    private static void Nested(StringBuilder builder, Member member, Model nested)
    {
        if (!member.IsList)
        {
            builder.Append("                    if (reader.TokenType == ")
                .Append("global::System.Text.Json.JsonTokenType.StartObject)\n")
                .Append("                        ").Append(Local(member)).Append(" = ")
                .Append(nested.Reader).Append(".Read(ref reader);\n")
                .Append("                    else\n")
                .Append("                        reader.Skip();\n");

            return;
        }

        builder.Append("                    var ").Append(Local(member)).Append("Items = ")
            .Append("new global::System.Collections.Generic.List<").Append(nested.Name).Append(">();\n\n")
            .Append("                    while (reader.Read() && reader.TokenType != ")
            .Append("global::System.Text.Json.JsonTokenType.EndArray)\n")
            .Append("                    {\n")
            .Append("                        if (reader.TokenType == ")
            .Append("global::System.Text.Json.JsonTokenType.StartObject)\n")
            .Append("                            ").Append(Local(member)).Append("Items.Add(")
            .Append(nested.Reader).Append(".Read(ref reader));\n")
            .Append("                        else\n")
            .Append("                            reader.Skip();\n")
            .Append("                    }\n\n")
            .Append("                    ").Append(Local(member)).Append(" = ")
            .Append(member.Fill).Append(";\n");
    }

    /// <summary>
    /// Writes the reply itself: the rows, and what the envelope said about them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The envelope is written out here rather than called into, so a generated reader depends on
    /// nothing at run time. It is the same walk every reply needs — <c>data</c>, its single field,
    /// and then whatever <paramref name="shape"/> says that field answers with.
    /// </para>
    /// <para>
    /// Only that one shape is written. The document was printed by the same compilation that
    /// writes this reader, so whether the rows arrive bare, inside <c>nodes</c>, or inside
    /// <c>items</c> is settled knowledge here — a reader that asked the reply which of them it was
    /// would be re-deciding at run time what was already decided at build time, and paying a
    /// comparison per property of the connection to arrive back at the known answer.
    /// </para>
    /// </remarks>
    private static void Reply(StringBuilder builder, Model? model, string prefix, ReplyShape shape)
    {
        string name = prefix + "_Reply";
        string rowType = model?.Name ?? "";

        if (shape == ReplyShape.Single && model is not null)
        {
            Single(builder, model, name, prefix);

            return;
        }

        // A counting query selects `totalCount` and no rows; every other shape selects rows and no
        // count. No document this generator prints asks for both, so no reply carries both.
        bool counts = shape == ReplyShape.Count;

        // What the rows are wrapped in, when they are wrapped at all.
        string? wrapper = shape switch
        {
            ReplyShape.Cursor => "nodes",
            ReplyShape.Offset => "items",
            ReplyShape.Count => "totalCount",
            _ => null
        };

        builder.Append("    /// <summary>One reply to <c>").Append(prefix)
            .Append("</c>: ").Append(counts ? "what the envelope said." : "its rows, and what the envelope said.")
            .Append("</summary>\n")
            .Append("    file readonly struct ").Append(name).Append('\n')
            .Append("    {\n");

        if (!counts)
            builder.Append("        public readonly ").Append(rowType).Append("[] Rows;\n");

        builder.Append("        public readonly bool HasData;\n")
            .Append("        public readonly bool HasErrors;\n");

        if (counts)
            builder.Append("        public readonly long? TotalCount;\n");

        builder.Append('\n')
            .Append("        public ").Append(name).Append('(')
            .Append(counts ? "" : rowType + "[] rows, ")
            .Append("bool hasData, bool hasErrors")
            .Append(counts ? ", long? totalCount)\n" : ")\n")
            .Append("        {\n");

        if (!counts)
            builder.Append("            Rows = rows;\n");

        builder
            .Append("            HasData = hasData;\n")
            .Append("            HasErrors = hasErrors;\n");

        if (counts)
            builder.Append("            TotalCount = totalCount;\n");

        builder.Append("        }\n\n")
            .Append("        /// <summary>Reads a whole reply, in one pass over its bytes.</summary>\n")
            .Append("        public static ").Append(name)
            .Append(" Read(global::System.ReadOnlySpan<byte> json)\n")
            .Append("        {\n")
            .Append("            var reader = new global::System.Text.Json.Utf8JsonReader(json);\n");

        if (counts)
            builder.Append("            long? totalCount = null;\n");
        else
        {
            builder.Append("            var rows = global::System.Array.Empty<")
                .Append(rowType).Append(">();\n");
        }

        builder.Append("            bool hasData = false;\n")
            .Append("            bool hasErrors = false;\n\n")
            .Append("            if (!reader.Read() || reader.TokenType != ")
            .Append("global::System.Text.Json.JsonTokenType.StartObject)\n")
            .Append("                return new ").Append(name)
            .Append(counts ? "(false, false, null);\n\n" : "([], false, false);\n\n")
            .Append("            while (reader.Read() && reader.TokenType == ")
            .Append("global::System.Text.Json.JsonTokenType.PropertyName)\n")
            .Append("            {\n")
            .Append("                bool isData = reader.ValueTextEquals(\"data\"u8);\n")
            .Append("                bool isErrors = !isData && reader.ValueTextEquals(\"errors\"u8);\n\n")
            .Append("                reader.Read();\n\n")
            .Append("                if (isErrors)\n")
            .Append("                {\n")
            .Append("                    // Present but empty is not a failure, which is what the spec says it means.\n")
            .Append("                    if (reader.TokenType == global::System.Text.Json.JsonTokenType.StartArray)\n")
            .Append("                    {\n")
            .Append("                        var peek = reader;\n")
            .Append("                        hasErrors = peek.Read() && peek.TokenType != ")
            .Append("global::System.Text.Json.JsonTokenType.EndArray;\n")
            .Append("                    }\n\n")
            .Append("                    reader.Skip();\n")
            .Append("                    continue;\n")
            .Append("                }\n\n")
            .Append("                if (!isData || reader.TokenType != ")
            .Append("global::System.Text.Json.JsonTokenType.StartObject)\n")
            .Append("                {\n")
            .Append("                    reader.Skip();\n")
            .Append("                    continue;\n")
            .Append("                }\n\n")
            .Append("                hasData = true;\n\n")
            .Append("                // data's single member, whatever the field was named.\n")
            .Append("                if (!reader.Read() || reader.TokenType != ")
            .Append("global::System.Text.Json.JsonTokenType.PropertyName)\n")
            .Append("                    continue;\n\n")
            .Append("                reader.Read();\n\n");

        if (wrapper is null)
        {
            // Un-paged: the field is the array, and there is no connection to step into.
            builder.Append("                if (reader.TokenType == ")
                .Append("global::System.Text.Json.JsonTokenType.StartArray)\n")
                .Append("                    rows = ReadRows(ref reader);\n")
                .Append("                else\n")
                .Append("                    reader.Skip();\n");
        }
        else
        {
            builder.Append("                // The connection the document asked for, whose <c>")
                .Append(wrapper).Append("</c> is what it asked for inside it.\n")
                .Append("                if (reader.TokenType != ")
                .Append("global::System.Text.Json.JsonTokenType.StartObject)\n")
                .Append("                {\n")
                .Append("                    reader.Skip();\n")
                .Append("                    continue;\n")
                .Append("                }\n\n")
                .Append("                while (reader.Read() && reader.TokenType == ")
                .Append("global::System.Text.Json.JsonTokenType.PropertyName)\n")
                .Append("                {\n")
                .Append("                    bool wanted = reader.ValueTextEquals(\"")
                .Append(wrapper).Append("\"u8);\n\n")
                .Append("                    reader.Read();\n\n")
                .Append("                    if (wanted && reader.TokenType == ")
                .Append(counts
                    ? "global::System.Text.Json.JsonTokenType.Number)\n"
                    : "global::System.Text.Json.JsonTokenType.StartArray)\n")
                .Append(counts
                    ? "                        totalCount = reader.GetInt64();\n"
                    : "                        rows = ReadRows(ref reader);\n")
                .Append("                    else\n")
                .Append("                        reader.Skip();\n")
                .Append("                }\n");
        }

        builder.Append("            }\n\n")
            .Append("            return new ").Append(name)
            .Append(counts
                ? "(hasData, hasErrors, totalCount);\n"
                : "(rows, hasData, hasErrors);\n")
            .Append("        }\n\n");

        if (counts)
        {
            builder.Append("    }\n\n");

            return;
        }

        string row = model!.Name;
        string pool = "global::System.Buffers.ArrayPool<" + row + ">.Shared";

        builder.Append("        /// <summary>\n")
            .Append("        /// Reads the rows of an array the reader is standing on.\n")
            .Append("        /// </summary>\n")
            .Append("        /// <remarks>\n")
            .Append("        /// How many there are is not known until the array ends, so they are gathered into a\n")
            .Append("        /// buffer that doubles as it fills and copied once, at the end, into an array of the\n")
            .Append("        /// size that turned out to be right. The buffer is rented and given back, so the only\n")
            .Append("        /// thing left on the heap is the array handed to the caller.\n")
            .Append("        /// </remarks>\n")
            .Append("        private static ").Append(row)
            .Append("[] ReadRows(ref global::System.Text.Json.Utf8JsonReader reader)\n")
            .Append("        {\n")
            .Append("            // Empty to begin with, so the capacity check below is what rents the buffer —\n")
            .Append("            // and a reply carrying no rows never reaches it, and rents nothing.\n")
            .Append("            var buffer = global::System.Array.Empty<").Append(row).Append(">();\n")
            .Append("            int at = 0;\n\n")
            .Append("            try\n")
            .Append("            {\n")
            .Append("                while (reader.Read() && reader.TokenType != ")
            .Append("global::System.Text.Json.JsonTokenType.EndArray)\n")
            .Append("                {\n")
            .Append("                    // A row a server could not resolve arrives as a null.\n")
            .Append("                    if (reader.TokenType != ")
            .Append("global::System.Text.Json.JsonTokenType.StartObject)\n")
            .Append("                    {\n")
            .Append("                        reader.Skip();\n")
            .Append("                        continue;\n")
            .Append("                    }\n\n")
            .Append("                    if (at == buffer.Length)\n")
            .Append("                    {\n")
            .Append("                        var grown = ").Append(pool)
            .Append(".Rent(at == 0 ? 16 : at * 2);\n\n")
            .Append("                        if (at > 0)\n")
            .Append("                        {\n")
            .Append("                            global::System.Array.Copy(buffer, grown, at);\n")
            .Append("                            ").Append(pool).Append(".Return(buffer, true);\n")
            .Append("                        }\n\n")
            .Append("                        buffer = grown;\n")
            .Append("                    }\n\n")
            .Append("                    buffer[at++] = ").Append(model.Reader).Append(".Read(ref reader);\n")
            .Append("                }\n\n")
            .Append("                if (at == 0)\n")
            .Append("                    return [];\n\n")
            .Append("                var rows = new ").Append(row).Append("[at];\n\n")
            .Append("                global::System.Array.Copy(buffer, rows, at);\n\n")
            .Append("                return rows;\n")
            .Append("            }\n")
            .Append("            finally\n")
            .Append("            {\n")
            .Append("                // Cleared on the way back: a row holds what it read, and an uncleared buffer\n")
            .Append("                // would keep this reply's values reachable until something else rents it.\n")
            .Append("                // The empty array a reply with no rows left behind is not the pool's to take.\n")
            .Append("                if (buffer.Length > 0)\n")
            .Append("                    ").Append(pool).Append(".Return(buffer, true);\n")
            .Append("            }\n")
            .Append("        }\n")
            .Append("    }\n\n");
    }

    /// <summary>
    /// Writes the reply of a field that answers with one object rather than a list.
    /// </summary>
    /// <remarks>
    /// The same envelope, minus everything about arrays: no connection to step into, since a field
    /// that pages answers with a list, and no rows to accumulate. What <c>data</c>'s single member
    /// holds is the row.
    /// </remarks>
    private static void Single(StringBuilder builder, Model model, string name, string prefix)
    {
        builder.Append("    /// <summary>One reply to <c>").Append(prefix)
            .Append("</c>: its row, and what the envelope said.</summary>\n")
            .Append("    file readonly struct ").Append(name).Append('\n')
            .Append("    {\n")
            .Append("        public readonly ").Append(model.Name).Append(" Row;\n")
            .Append("        public readonly bool HasData;\n")
            .Append("        public readonly bool HasErrors;\n\n")
            .Append("        public ").Append(name).Append('(').Append(model.Name)
            .Append(" row, bool hasData, bool hasErrors)\n")
            .Append("        {\n")
            .Append("            Row = row;\n")
            .Append("            HasData = hasData;\n")
            .Append("            HasErrors = hasErrors;\n")
            .Append("        }\n\n")
            .Append("        /// <summary>Reads a whole reply, in one pass over its bytes.</summary>\n")
            .Append("        public static ").Append(name)
            .Append(" Read(global::System.ReadOnlySpan<byte> json)\n")
            .Append("        {\n")
            .Append("            var reader = new global::System.Text.Json.Utf8JsonReader(json);\n")
            .Append("            ").Append(model.Name).Append(" row = default!;\n")
            .Append("            bool hasData = false;\n")
            .Append("            bool hasErrors = false;\n\n")
            .Append("            if (!reader.Read() || reader.TokenType != ")
            .Append("global::System.Text.Json.JsonTokenType.StartObject)\n")
            .Append("                return new ").Append(name).Append("(row, false, false);\n\n")
            .Append("            while (reader.Read() && reader.TokenType == ")
            .Append("global::System.Text.Json.JsonTokenType.PropertyName)\n")
            .Append("            {\n")
            .Append("                bool isData = reader.ValueTextEquals(\"data\"u8);\n")
            .Append("                bool isErrors = !isData && reader.ValueTextEquals(\"errors\"u8);\n\n")
            .Append("                reader.Read();\n\n")
            .Append("                if (isErrors)\n")
            .Append("                {\n")
            .Append("                    // Present but empty is not a failure, which is what the spec says it means.\n")
            .Append("                    if (reader.TokenType == global::System.Text.Json.JsonTokenType.StartArray)\n")
            .Append("                    {\n")
            .Append("                        var peek = reader;\n")
            .Append("                        hasErrors = peek.Read() && peek.TokenType != ")
            .Append("global::System.Text.Json.JsonTokenType.EndArray;\n")
            .Append("                    }\n\n")
            .Append("                    reader.Skip();\n")
            .Append("                    continue;\n")
            .Append("                }\n\n")
            .Append("                if (!isData || reader.TokenType != ")
            .Append("global::System.Text.Json.JsonTokenType.StartObject)\n")
            .Append("                {\n")
            .Append("                    reader.Skip();\n")
            .Append("                    continue;\n")
            .Append("                }\n\n")
            .Append("                hasData = true;\n\n")
            .Append("                // data's single member, whatever the field was named.\n")
            .Append("                if (!reader.Read() || reader.TokenType != ")
            .Append("global::System.Text.Json.JsonTokenType.PropertyName)\n")
            .Append("                    continue;\n\n")
            .Append("                reader.Read();\n\n")
            .Append("                // A field with nothing to answer with sends a null.\n")
            .Append("                if (reader.TokenType == global::System.Text.Json.JsonTokenType.StartObject)\n")
            .Append("                    row = ").Append(model.Reader).Append(".Read(ref reader);\n")
            .Append("                else\n")
            .Append("                    reader.Skip();\n")
            .Append("            }\n\n")
            .Append("            return new ").Append(name).Append("(row, hasData, hasErrors);\n")
            .Append("        }\n")
            .Append("    }\n\n");
    }

    /// <summary>The local a member's value is read into before the struct is built.</summary>
    private static string Local(Member member) => Local(member.Property);

    /// <inheritdoc cref="Local(Member)"/>
    private static string Local(string property)
        => "_" + char.ToLowerInvariant(property[0]) + property.Substring(1);
}
