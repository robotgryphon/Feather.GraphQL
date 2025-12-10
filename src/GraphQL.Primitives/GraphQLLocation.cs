namespace GraphQL;

/// <summary>
/// Represents a GraphQL Location of a GraphQL Query
/// </summary>
public record struct GraphQLLocation(uint Column, uint Line);
