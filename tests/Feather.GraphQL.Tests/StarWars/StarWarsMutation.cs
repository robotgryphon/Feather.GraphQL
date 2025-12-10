using Feather.GraphQL.Tests.StarWars.Types;
using GraphQL;
using GraphQL.Types;

namespace Feather.GraphQL.Tests.StarWars;

/// <summary> Mutation graph type for StarWars schema. </summary>
/// <example>
///     This is an example JSON request for a mutation
///     {
///     "query": "mutation ($human:HumanInput!){ createHuman(human: $human) { id name } }",
///     "variables": {
///     "human": {
///     "name": "Boba Fett"
///     }
///     }
///     }
/// </example>
public class StarWarsMutation : ObjectGraphType<object>
{
    public StarWarsMutation(StarWarsData data)
    {
        Name = "Mutation";

        Field<HumanType>("createHuman")
                .Argument<NonNullGraphType<HumanInputType>>("human")
                .Resolve(context =>
                {
                    var human = context.GetArgument<Human>("human");
                    return data.AddCharacter(human);
                });
    }
}
