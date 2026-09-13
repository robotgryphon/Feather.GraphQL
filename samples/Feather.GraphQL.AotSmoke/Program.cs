using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Feather.GraphQL;
using Feather.GraphQL.Http;
using Feather.GraphQL.Linq.Providers;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.AotSmoke;

/// <summary>
/// Runs one of each surface under NativeAOT, against a canned reply.
/// </summary>
/// <remarks>
/// <para>
/// Not a benchmark and not a test — a published binary that either works or does not. Every AOT
/// claim this library makes was architectural until this ran: no project set <c>PublishAot</c>,
/// so nothing had ever proved that a compiled query survives trimming and native compilation.
/// </para>
/// <para>
/// The transport is canned so the program needs no network and no server. What is being proved is
/// that the generated readers, the generated bodies and the reply path all work once the runtime
/// can no longer reflect over anything.
/// </para>
/// </remarks>
internal static partial class Program
{
    private const string Reply =
        """{"data":{"countries":[{"name":"Andorra","continent":{"name":"Europe"}},"""
        + """{"name":"Japan","continent":{"name":"Asia"}}]}}""";

    private static async Task<int> Main()
    {
        using var handler = new CannedHandler(Reply);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        int failures = 0;

        failures += Check("compiled chain", (await ChainAsync(client)).Length == 2);
        failures += Check("declared document", (await DeclaredAsync(client)).Length == 2);
        failures += Check("hand-written string", (await StringAsync(client)).Countries.Length == 2);

        Console.WriteLine(failures == 0 ? "AOT smoke: OK" : $"AOT smoke: {failures} FAILED");

        return failures;
    }

    /// <summary>A chain the compiler writes out in full.</summary>
    [GraphQLQuery]
    private static Task<Country[]> ChainAsync(HttpClient client)
        => client.CreateQueryable<Country>("countries")
            .Select(c => new Country { Name = c.Name, Continent = new Continent { Name = c.Continent.Name } })
            .ToArrayAsync();

    /// <summary>A document written by hand, implemented by the compiler.</summary>
    [GraphQLQuery("query { countries { name continent { name } } }")]
    private static partial Task<Country[]> DeclaredAsync(HttpClient client);

    /// <summary>The string path: a document sent as-is, read through a declared contract.</summary>
    private static async Task<CountriesData> StringAsync(HttpClient client)
    {
        using var response = await client.SendGraphQLQueryAsync(
            "query { countries { name continent { name } } }");

        return await response.ReadGraphQLAsync<CountriesData>();
    }

    private static int Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");

        return ok ? 0 : 1;
    }
}

/// <summary>Answers every request from one canned reply.</summary>
internal sealed class CannedHandler(string json) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
}

public class Country
{
    public string Name { get; set; } = "";
    public Continent Continent { get; set; } = new();
}

public class Continent
{
    public string Name { get; set; } = "";
}

/// <summary>What the string path reads into, and the contract that describes it.</summary>
public class CountriesData
{
    public Country[] Countries { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CountriesData))]
public partial class SmokeContext : JsonSerializerContext;
