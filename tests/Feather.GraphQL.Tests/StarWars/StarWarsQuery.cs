using Feather.GraphQL.Tests.StarWars.Types;
using GraphQL;
using GraphQL.Types;

namespace Feather.GraphQL.Tests.StarWars;

public class StarWarsQuery
{
    public static Task<Droid?> Hero([FromServices] StarWarsData data) => data.GetDroidByIdAsync("3");

    public static Task<Human?> Human(string id, [FromServices] StarWarsData data) => data.GetHumanByIdAsync(id);

    public static Task<Droid?> Droid(string id, [FromServices] StarWarsData data) => data.GetDroidByIdAsync(id);
}
