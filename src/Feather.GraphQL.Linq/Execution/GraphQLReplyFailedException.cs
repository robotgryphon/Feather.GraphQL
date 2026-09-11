namespace Feather.GraphQL.Linq.Execution;

/// <summary>
/// Raised by the reply reader when a reply carries no rows to read: the server reported errors,
/// or sent no <c>data</c> at all.
/// </summary>
/// <remarks>
/// <para>
/// It says which of the two happened and nothing else, because nothing else is knowable here.
/// Reading an error means naming the type an error is, and <c>Feather.GraphQL.Linq</c> does not
/// reference the assembly that defines one — deliberately, so the LINQ surface stays free of any
/// transport. The transport catches this and raises its own, with the errors read through its
/// own types.
/// </para>
/// <para>
/// A failed reply is genuinely exceptional, so it travels as an exception rather than as a
/// result that every successful read would have to unwrap. The cost falls entirely on the path
/// that was already going to throw.
/// </para>
/// <para>
/// Internal, with <see cref="GraphQLReplyReader"/>: it is how that reader tells the transport
/// beside it that there is nothing to read, and neither is a contract anything outside this
/// repository sees.
/// </para>
/// </remarks>
internal sealed class GraphQLReplyFailedException(bool carriedErrors)
    : Exception(carriedErrors
        ? "The server reported errors."
        : "The reply carried neither data nor errors.")
{
    /// <summary>
    /// True when the reply carried an <c>errors</c> array; false when it carried no <c>data</c>.
    /// </summary>
    public bool CarriedErrors { get; } = carriedErrors;
}
