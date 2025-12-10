using System.Text.Json;
using Feather.GraphQL.Http;
using Feather.GraphQL.Example;
using GraphQL.Request;

// ReSharper disable UseConfigureAwaitFalse

using var graphQLClient = new HttpClient { BaseAddress = new Uri("https://countries.trevorblades.com/") };

var gqlRequest = new GraphQLRequest
{
        Query = "query Countries { countries { name continent { name } } }",
        OperationName = "Countries"
};

var httpResponse = await graphQLClient.SendGraphQLQueryAsync(gqlRequest);
if (!httpResponse.IsSuccessStatusCode)
{
    string body = await httpResponse.Content.ReadAsStringAsync();
    Console.WriteLine(body);
    return;
}

var graphQLResponse = await httpResponse.Content.ReadAsGraphQLAsync<CountryAndContinentsResponse>();

var response = graphQLResponse!.Data;

Console.WriteLine("raw response:");
Console.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }));

foreach (var c in response.Countries ?? [])
{
    Console.WriteLine();
    Console.WriteLine($"Name: {c.Name}");
    Console.WriteLine($"Continent: {c.Continent.Name}");
}

Console.WriteLine();
Console.WriteLine("Press any key to quit...");
Console.ReadKey();
