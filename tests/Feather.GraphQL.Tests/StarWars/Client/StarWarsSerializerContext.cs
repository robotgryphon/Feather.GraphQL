using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Feather.GraphQL.Metadata;

namespace Feather.GraphQL.Tests.StarWars.Client;

/// <summary>
/// The contract the reply to a hand-written document is read through.
/// </summary>
/// <remarks>
/// <para>
/// Required, not an optimisation. Reading a document sent as a string goes through
/// <c>System.Text.Json</c>, and the library registers no reflection resolver to fall back on — so
/// a type nothing has described fails at the point of reading.
/// </para>
/// <para>
/// Registered by hand here because this project references only <c>Feather.GraphQL.Http</c>. The
/// generator that finds a context and registers it ships with <c>Feather.GraphQL.Linq</c>, so a
/// caller who sends documents and never touches LINQ has to do this themselves.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HumanResponse))]
public partial class StarWarsSerializerContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255",
        Justification = "A reply cannot be read before the contract for it is registered, and the "
            + "first read happens in a test whose setup this would otherwise have to reach into.")]
    internal static void Register() => GraphQLJsonContextRegistry.Register(Default);
}
