using System.Diagnostics.CodeAnalysis;
using Feather.GraphQL.Http;
using Feather.GraphQL.Tests.StarWars.Client;

namespace Feather.GraphQL.Tests;

[SuppressMessage("ReSharper", "UseConfigureAwaitFalse")]
public class BasicQueryTest : IntegrationServerTest
{
    [TestCase(1, "Luke")]
    [TestCase(2, "Vader")]
    public async Task CanQueryViaProvidedTemplateString(int id, string name)
    {
        string query = $$"""
                        query Human {
                           human(id: "{{id}}") {
                                name
                            }
                        }
                        """;

        var httpClient = new HttpClient { BaseAddress = new Uri("https://localhost:5000/graphql") };

        var response = await httpClient.SendGraphQLQueryAsync(query, CancellationToken.None);

        Assert.That(response, Is.Not.Null.And.InstanceOf<HttpResponseMessage>());

        // Returns the data, or throws: there is no wrapper to unpack and no errors to check.
        var human = await response.ReadGraphQLAsync<HumanResponse>(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(human.Human, Is.Not.Null.And.InstanceOf<Human>());
            Assert.That(human.Human!.Name, Is.EqualTo(name));
        }
    }

    // [Theory]
    // [ClassData(typeof(StarWarsHumans))]
    // public async void After_querying_all_starwars_humans_using_websocket_transport_the_APQDisabledForSession_is_still_false_Async(int id, string name)
    // {
    //     var query = new GraphQLQuery("""
    //                                  query Human($id: String!){
    //                                  human(id: $id) {
    //                                          name
    //                                      }
    //                                  }
    //                                  """);
    //
    //     var graphQLRequest = new GraphQLRequest(query, new { id = id.ToString() });
    //
    //     var response = await StarWarsWebsocketClient.SendQueryAsync(graphQLRequest, () => new { Human = new { Name = string.Empty } });
    //
    //     Assert.Null(response.Errors);
    //     Assert.Equal(name, response.Data.Human.Name);
    //     StarWarsWebsocketClient.APQDisabledForSession.Should().BeFalse("if APQ has worked it won't get disabled");
    // }
    //
    // [Fact]
    // public void Verify_the_persisted_query_extension_object()
    // {
    //     var query = new GraphQLQuery("""
    //                                  query Human($id: String!){
    //                                  human(id: $id) {
    //                                          name
    //                                      }
    //                                  }
    //                                  """);
    //     query.Sha256Hash.Should().NotBeNullOrEmpty();
    //
    //     var request = new GraphQLRequest(query);
    //     request.Extensions.Should().BeNull();
    //     request.GeneratePersistedQueryExtension();
    //     request.Extensions.Should().NotBeNull();
    //
    //     string expectedKey = "persistedQuery";
    //     var expectedExtensionValue = new Dictionary<string, object>
    //     {
    //         ["version"] = 1,
    //         ["sha256Hash"] = query.Sha256Hash,
    //     };
    //
    //     request.Extensions.Should().ContainKey(expectedKey);
    //     request.Extensions![expectedKey].As<Dictionary<string, object>>()
    //         .Should().NotBeNull().And.BeEquivalentTo(expectedExtensionValue);
    // }
}
