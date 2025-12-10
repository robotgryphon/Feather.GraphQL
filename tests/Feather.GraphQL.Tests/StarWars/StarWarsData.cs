using Feather.GraphQL.Tests.StarWars.Types;

namespace Feather.GraphQL.Tests.StarWars;

public class StarWarsData
{
    private readonly List<StarWarsCharacter> _characters = new();

    public StarWarsData()
    {
        _characters.Add(new Human
        {
                Id = "1",
                Name = "Luke",
                Friends = ["3", "4"],
                AppearsIn = [4, 5, 6],
                HomePlanet = "Tatooine",
                Cursor = "MQ=="
        });
        _characters.Add(new Human
        {
                Id = "2",
                Name = "Vader",
                AppearsIn = [4, 5, 6],
                HomePlanet = "Tatooine",
                Cursor = "Mg=="
        });

        _characters.Add(new Droid
        {
                Id = "3",
                Name = "R2-D2",
                Friends = ["1", "4"],
                AppearsIn = [4, 5, 6],
                PrimaryFunction = "Astromech",
                Cursor = "Mw=="
        });
        _characters.Add(new Droid
        {
                Id = "4",
                Name = "C-3PO",
                AppearsIn = [4, 5, 6],
                PrimaryFunction = "Protocol",
                Cursor = "NA=="
        });
    }

    public IEnumerable<StarWarsCharacter>? GetFriends(StarWarsCharacter character)
        => _characters.Where(h => character.Friends.Contains(h.Id)).ToList();

    public StarWarsCharacter AddCharacter(StarWarsCharacter character)
    {
        character.Id = _characters.Count.ToString();
        _characters.Add(character);
        return character;
    }

    public Task<Human?> GetHumanByIdAsync(string id)
        => Task.FromResult(_characters.FirstOrDefault(h => h.Id == id && h is Human) as Human);

    public Task<Droid?> GetDroidByIdAsync(string id)
        => Task.FromResult(_characters.FirstOrDefault(h => h.Id == id && h is Droid) as Droid);

    public Task<List<StarWarsCharacter>> GetCharactersAsync(List<string> guids)
        => Task.FromResult(_characters.Where(c => guids.Contains(c.Id)).ToList());
}
