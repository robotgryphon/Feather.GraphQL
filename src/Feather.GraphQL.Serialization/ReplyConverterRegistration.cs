using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Feather.GraphQL.Metadata;

namespace Feather.GraphQL.Serialization;

/// <summary>
/// Teaches the contract registry how to read a reply.
/// </summary>
/// <remarks>
/// A module initializer, so it happens before anything can read a reply and without a consumer
/// having to know it needs to. The registry lives below this assembly and knows nothing about
/// replies; this is the one direction the knowledge travels.
///
/// One shape is left to register: the rows a chain translated at run time reads into. Everything
/// compiled reads through code generated for its own reply, which needs no contract and so needs
/// nothing registered.
/// </remarks>
internal static class ReplyConverterRegistration
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255",
        Justification = "A consumer cannot be asked to register the converter that makes this "
            + "library able to read a reply at all, and the registry it goes into is in an "
            + "assembly that must not know what a query is. Loading is exactly the moment.")]
    internal static void Register()
        => GraphQLJsonContextRegistry.Register(new ParsedReplyConverterFactory());
}
