namespace Feather.GraphQL.Linq.Document;

/// <summary>An argument bound to a variable. v1 never inlines values — see §5.3.</summary>
internal sealed record GqlArgument(string Name, string VariableName);