#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

namespace Feather.GraphQL.Tests.StarWars.Types;

public abstract class StarWarsCharacter
{
    public string Id { get; set; }
    public string Name { get; set; }
    public List<string> Friends { get; set; }
    public int[] AppearsIn { get; set; }
    public string Cursor { get; set; }
}

public class Human : StarWarsCharacter
{
    public string HomePlanet { get; set; }
}

public class Droid : StarWarsCharacter
{
    public string PrimaryFunction { get; set; }
}
