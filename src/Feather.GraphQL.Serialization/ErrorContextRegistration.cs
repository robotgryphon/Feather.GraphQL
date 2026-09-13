using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Feather.GraphQL.Metadata;

namespace Feather.GraphQL.Serialization;

/// <summary>
/// Puts this library's own error contract into the registry.
/// </summary>
/// <remarks>
/// The reply envelope's <c>errors</c> member is a type that belongs here rather than to the
/// caller, so the caller cannot be the one to declare it — and the envelope's contract resolves
/// it from the same options as everything else.
/// </remarks>
internal static class ErrorContextRegistration
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255",
        Justification = "A consumer cannot be asked to register the contract for a type they do "
            + "not own and never name. Loading is exactly the moment.")]
    internal static void Register() => GraphQLJsonContextRegistry.Register(GraphQLErrorContext.Default);
}
