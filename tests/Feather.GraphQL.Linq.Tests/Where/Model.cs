using System.Text.Json.Serialization;

namespace Feather.GraphQL.Linq.Tests.Where;

public enum Status { Active, PendingReview }

/// <summary>Deliberately un-attributed: exercises the reflection fallback in §4.2.</summary>
public class Person
{
    public string Name { get; init; } = "";
    public int Age { get; init; }
    public bool IsActive { get; init; }
    public Status Status { get; init; }
    public DateOnly Birthday { get; init; }

    [JsonPropertyName("emailAddress")]
    public string? Email { get; init; }

    [JsonIgnore]
    public string Secret { get; init; } = "";

    public Address? Address { get; init; }
    public List<Tag> Tags { get; init; } = [];
    public List<string> Nicknames { get; init; } = [];
}

public class Address
{
    public string City { get; init; } = "";
    public string Country { get; init; } = "";
}

public class Tag
{
    public string Name { get; init; } = "";
    public int Weight { get; init; }
}

/// <summary>Same shape as <see cref="Person"/>, attributed. Used for the reflection-parity test.</summary>
public partial class AttributedPerson
{
    public string Name { get; init; } = "";
    public int Age { get; init; }

    [JsonPropertyName("emailAddress")]
    public string? Email { get; init; }
}
