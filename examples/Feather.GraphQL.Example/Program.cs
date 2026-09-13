using System.Diagnostics;
using Feather.GraphQL.Example;
using Feather.GraphQL;
using Feather.GraphQL.Linq.Providers;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Query;
using Microsoft.Extensions.DependencyInjection;
// ReSharper disable SuggestVarOrType_Elsewhere
// ReSharper disable UseConfigureAwaitFalse

var sw = new Stopwatch();
sw.Start();

var services = new ServiceCollection();
services.AddHttpClient("countries", cl =>
{
    cl.BaseAddress = new Uri("https://countries.trevorblades.com/");
});

var serviceProvider = services.BuildServiceProvider();

// An ordinary HttpClient. Headers and auth go on it as usual.
var client = serviceProvider
    .GetRequiredService<IHttpClientFactory>()
    .CreateClient("countries");

// The chain lives in a method the compiler writes out in full — see Queries below. What goes on
// the wire is decided at build time, so there is nothing here to compose, translate or print.
var countries = await Queries.InEuropeAsync(client);

foreach (var country in countries)
{
    Console.WriteLine();
    Console.WriteLine($"Name: {country.Country}");
    Console.WriteLine($"Continent: {country.Continent}");
}

sw.Stop();

Console.WriteLine($"Elapsed: {sw.ElapsedMilliseconds} ms");

/// <summary>
/// The queries this program sends.
/// </summary>
/// <remarks>
/// A chain isolated to a method is one the compiler can replace every call to: the document is
/// printed at build time, the body below never runs, and the request is written from the
/// arguments. To read the GraphQL it compiled to, open the generated file — your IDE lists it
/// under the project's analyzers.
/// </remarks>
public static class Queries
{
    [GraphQLQuery]
    public static Task<GeographyIsFun[]> InEuropeAsync(HttpClient client)
        => client.CreateQueryable<Country>("countries")
            .Where("filter", (CountryFilter c) => c.Continent == "EU")
            .Select(c => new GeographyIsFun(c.Name, c.Continent))
            .ToArrayAsync();
}

/// <summary>
/// Models <c>CountryFilterInput</c>. Its <c>continent</c> takes a string filter directly, unlike
/// <see cref="Country.Continent"/>, which is an object in the response.
/// </summary>
public class CountryFilter
{
    public string? Continent { get; set; }
}

public record struct GeographyIsFun(string Country, Continent Continent);
