using System.Diagnostics;
using Feather.GraphQL.Example;
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

IQueryable<GeographyIsFun> queryable = client.CreateQueryable<Country>("countries")
    .Where("filter", (CountryFilter c) => c.Continent == "EU")
    .Select(c => new GeographyIsFun(c.Name, c.Continent));

var rawQuery = queryable.ToGraphQLQuery();

Console.WriteLine();
Console.WriteLine("query:");
Console.WriteLine(rawQuery);

Console.WriteLine();
var countries = await queryable.ToArrayAsync();
foreach (var country in countries)
{
    Console.WriteLine();
    Console.WriteLine($"Name: {country.Country}");
    Console.WriteLine($"Continent: {country.Continent}");
}

sw.Stop();

Console.WriteLine($"Elapsed: {sw.ElapsedMilliseconds} ms");

/// <summary>
/// Models <c>CountryFilterInput</c>. Its <c>continent</c> takes a string filter directly, unlike
/// <see cref="Country.Continent"/>, which is an object in the response.
/// </summary>
public class CountryFilter
{
    public string? Continent { get; set; }
}

public record struct GeographyIsFun(string Country, Continent Continent);
