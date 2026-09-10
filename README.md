# Feather.GraphQL

A GraphQL Client for .NET Standard over HTTP.

## Specification
The Library will try to follow the following standards and documents:

* [GraphQL Specification](https://spec.graphql.org/June2018/)
* [GraphQL HomePage](https://graphql.org/learn)

## Packages

| Package | What it is | References |
| --- | --- | --- |
| `Feather.GraphQL.Abstractions` | Query, error and response primitives | — |
| `Feather.GraphQL.Http` | `HttpClient` extensions for sending query strings | Abstractions |
| `Feather.GraphQL.Linq` | Attributes, LINQ → GraphQL translation and materialization, plus the analyzer | — |
| `Feather.GraphQL.Linq.Providers.HttpClient` | The LINQ provider over `HttpClient`, plus DI | Linq, Http |

A toolkit, not a framework: **Linq and Http do not reference each other.** Take the translator
without a transport, the transport without a translator, or the provider package that joins them.

## Usage

This library provides various extensions and utilities to extend base .NET networking functionality with GraphQL concepts.

If you're familiar with the `System.Text.Json` extensions to `HttpClient`, this was designed to be familiar!

### Sending a query

There are exactly two ways to reach a server:

1. Hand a **precompiled GraphQL query string** to the `HttpClient` extension methods.
2. Compose a chain with the **LINQ integration** and let it translate and send.

The request body itself is an implementation detail — there is no request type to construct.

#### Simple query

```csharp
// Use an HTTP client from somewhere (DI, Factory, etc)
var httpResponse = await httpClient.SendGraphQLQueryAsync("""
    {
        hero {
            name
        }
    }
    """);
```

> [!NOTE]
> The string path sends the document exactly as written and carries no variables payload, so any
> values the query needs must already be present in its text. Reach for the LINQ integration when
> you want arguments bound to variables for you.

### Execute Query/Mutation

Assuming the schema:
```graphql
type Query
{
    person: Person
}

type Person
{
    name: String!
    filmConnection: FilmConnection!
}

type FilmConnection {
    films: [FilmContent!]
}

type FilmContent {
    title: String!
}
```

```csharp
// Use an HTTP client from somewhere (DI, Factory, etc)
var httpResponse = await httpClient.SendGraphQLQueryAsync("""
    query PersonAndFilms {
        person(id: "cGVvcGxlOjE=") {
            name
            filmConnection {
                films {
                    title
                }
            }
        }
    }
    """);

// Read the response headers/content similar to string or JSON data
var graphQLResponse = await httpResponse.Content.ReadAsGraphQLAsync<PersonResponse>();

// Get the information you need out of the response
var personName = graphQLResponse.Data.Person.Name;
```

Mutations go through `SendMutationAsync`, which takes a query string the same way.

### Query with LINQ

The LINQ provider translates; it does not transport. `Feather.GraphQL.Linq` knows nothing about
HTTP, and `Feather.GraphQL.Http` knows nothing about LINQ — the two meet only in
`Feather.GraphQL.Linq.Providers.HttpClient`, which is the package to install for the usual case.
Another transport (websocket, in-process schema, recorded fixtures) is one implementation of
`IGraphQLQueryExecutor`.

Turn an `HttpClient` into a query and finish it with an ordinary LINQ terminal:

```csharp
using var client = new HttpClient { BaseAddress = new Uri("https://countries.example/graphql") };

List<Country> european = await client.CreateQueryable<Country>()
    .Where(c => c.Continent.Name == "Europe")
    .OrderBy(c => c.Name)
    .Take(25)
    .ToListAsync(ct);
```

That is the whole setup. There is no registration call: headers, auth and retry are configured on
the client the way they would be for any other use of it, and how the client reaches the code
using it — a field, a constructor parameter, a DI registration of your own — is your app's
choice, not this library's. Talking to two schemas means two clients.

`CreateQueryable<T>()` builds a source per call. When several query types share one endpoint,
keep the source instead and the executor is built once:

```csharp
var countries = client.AsGraphQLQueryableSource();

var europe = await countries.Queryable<Country>().Where(…).ToListAsync(ct);
var africa = await countries.Queryable<Continent>().Where(…).ToListAsync(ct);
```

Both post to the client's `BaseAddress`. For a server that serves GraphQL from a path below it,
pass one — an absolute URI is used as-is and ignores the base address:

```csharp
client.CreateQueryable<Country>("api/v2/graphql");
client.AsGraphQLQueryableSource("api/v2/graphql");
```

> [!NOTE]
> Resolution is `HttpClient`'s own, which treats a base address as a document rather than a
> directory: `https://host/v1` plus `graphql` gives `https://host/graphql`, not
> `https://host/v1/graphql`. Give the base address a trailing slash to keep its path.

#### Naming the server's arguments

The translator emits HotChocolate's argument names by default — `where`, `order`, `take`, `skip`,
`first`, `last`. Schemas disagree: the public countries API calls its filter `filter`. Say so in
the chain, and the rest of it is unchanged:

```csharp
await client.CreateQueryable<Country>()
    .WithGraphQLArguments(new() { Filter = "filter" })
    .Where(c => c.Continent.Name == "Europe")
    .ToListAsync(ct);

// query($v0: CountryFilterInput) { countries(filter: $v0) { … } }
```

Unset members keep their defaults, so renaming one argument costs one line. It composes anywhere
in the chain and works over any `IQueryable<T>`, including one this library did not create.

This changes what the arguments are *called*. What goes **inside** the filter — the operator
names and nesting — is a separate concern, handled by `IFilterTranslationProvider`.

#### Filtering with the server's filter shape

A filter input is not always shaped like the thing it filters. The countries API returns
`Country.continent` as an object with a `name`, but its `CountryFilterInput.continent` takes a
string filter directly — so a predicate over the queried type lands a level too deep:

```csharp
.Where(c => c.Continent.Name == "Europe")   // {"continent":{"name":{"eq":"Europe"}}} — rejected
```

Model the input as its own type and filter against that. The chain stays over the queried type,
so `Select` and materialization are unaffected:

```csharp
public class CountryFilter
{
    public string? Continent { get; set; }
}

await client.CreateQueryable<Country>()
    .WithGraphQLArguments(new() { Filter = "filter" })
    .Where((CountryFilter f) => f.Continent == "Europe")   // {"continent":{"eq":"Europe"}}
    .Select(c => new { c.Name, Continent = c.Continent.Name })
    .ToArrayAsync(ct);
```

Note the explicitly-typed lambda parameter: `Where<CountryFilter>(…)` will not compile, because
C# binds an explicit type argument to the extension's own type parameter first and would read it
as the element type. `(CountryFilter f) => …` infers it instead — or name both types and leave
the lambda bare, `Where<Country, CountryFilter>(f => …)`.

For a simple query the argument name can ride along with the predicate, and there is no need to
meet `WithGraphQLArguments` at all:

```csharp
await client.CreateQueryable<Country>()
    .Where("filter", (CountryFilter f) => f.Continent == "Europe")
    .Select(c => new { c.Name, Continent = c.Continent.Name })
    .ToArrayAsync(ct);
```

That overload works over the queried type too, when only the argument's name differs from
HotChocolate's:

```csharp
client.CreateQueryable<Person>().Where("filter", p => p.Age > 30);
```

Both spellings set the same thing, so the later call in the chain wins. Reach for
`WithGraphQLArguments` when the sort or paging arguments are renamed as well.

Field names on the filter type resolve the usual way — `[JsonPropertyName]`, then `[DataMember]`,
then camel-cased — and the argument's GraphQL type name still comes from the queried type, since
it belongs to the root field. A chain filters one way or the other; mixing both is FGQL020.

Sync terminals (`ToList()`, `First()`, `foreach`) block on the request the way EF Core's do.
The async counterparts — `ToListAsync`, `ToArrayAsync`, `FirstAsync`, `SingleAsync`, `AnyAsync`,
`CountAsync`, `LastAsync` and `AsAsyncEnumerable` — take a `CancellationToken` and don't.

Projections say what to select, and nested fields come along:

```csharp
.Select(p => new
{
    p.Name,
    p.Size,                                                    // size { minimum maximum }
    p.Tags,                                                    // tags — a scalar list, a leaf
    Parts = p.Parts!.Primary.Select(x => new { x.Name }).ToArray(),   // parts { primary { name } }
})
```

**Automatic selection takes scalars only.** Naming an object member selects that member's own
scalar fields; anything nested inside it is skipped, and a `ToArray()` or `ToList()` around a
nested `Select` asks for exactly what the `Select` alone would. The same rule applies with no
`Select` at all — you get the type's scalars.

That is what keeps a projection from walking a whole object graph, and it makes a self-referencing
model harmless: `Country.Continent` holds `Continent.Countries`, which is `Country` again, but
`c.Continent` selects only `code` and `name` and stops.

The consequence is worth stating plainly: **a nested field you did not ask for comes back unset.**
Ask for it with a projection.

One case has no answer — a type whose every field is nested contributes an empty selection set,
which GraphQL does not allow. Referencing the package brings an analyzer that says so at build
time rather than on the first request:

```csharp
.Select(c => new { c.Parts })
//                 ~~~~~~~
// FGQL014: 'c.Parts' cannot be selected on its own: 'Parts' has no scalar fields, and its
// nested ones are not selected by default. Say what to take from it with a nested Select,
// or name the fields with Include() once it lands.
```

It is the same rule the translator enforces, moved earlier — the analyzer stays quiet when it
cannot follow a projection, and the runtime still catches those.

Result operators are translated, not applied after the fact: `First()` asks for a page of one,
`Any()` selects a single scalar, `Count()` reads the connection's `totalCount`, and `Last()`
reads backwards with cursor paging's `last:`. Where a paging kind cannot express one, it is a
translation error rather than a silent full fetch — `Count()` on an un-paged field is FGQL009.

`ToGraphQLQuery()` translates a chain without sending it, writing every argument into the
document so you can read what the query actually asks:

```csharp
GraphQLQueryable.For<Person>().Where(p => p.Age > 30).OrderBy(p => p.Name).ToGraphQLQuery();
// query { people(where: {age: {gt: 30}}, order: [{name: ASC}]) { name age emailAddress } }
```

That is the debugging form and pastes straight into a playground. What actually goes over the
wire binds every argument to a variable — `people(where: $v0)` — which is what keeps one document,
and therefore one APQ key, covering every predicate of a given shape. Reach for `ToQueryPlan()`
when you want that document alongside the variables payload.

Both are plain `string`s. The public API deals in strings, expressions and `IQueryable<T>`; there
is no query wrapper type to learn.

To run queries over something other than HTTP, implement `IGraphQLQueryExecutor` and hand it to
`GraphQLQueryableSource`. It receives a `GraphQLQueryPlan` — the printed document, its variables,
and the shape of the answer — and returns the response's `data` element:

```csharp
public sealed class MyExecutor : IGraphQLQueryExecutor
{
    public IFilterTranslationProvider FilterProvider => HotChocolateFilterProvider.Instance;

    public ValueTask<JsonElement> ExecuteAsync(GraphQLQueryPlan plan, CancellationToken ct) => …;
}

IGraphQLQueryableSource source = new GraphQLQueryableSource(new MyExecutor());
```

Sync terminals (`ToList()`, `First()`, `foreach`) block on the request the way EF Core's do.
The async counterparts — `ToListAsync`, `ToArrayAsync`, `FirstAsync`, `SingleAsync`, `AnyAsync`,
`CountAsync`, `LastAsync` and `AsAsyncEnumerable` — take a `CancellationToken` and don't.

Result operators are translated, not applied after the fact: `First()` asks for a page of one,
`Any()` selects a single scalar, `Count()` reads the connection's `totalCount`, and `Last()`
reads backwards with cursor paging's `last:`. Where a paging kind cannot express one, it is a
translation error rather than a silent full fetch — `Count()` on an un-paged field is FGQL009.



> [!IMPORTANT]
> Note that the field in the GraphQL response which gets deserialized into the response object is the `data` field.
>
> A common mistake is to try to directly use the `PersonType` class as response type (because thats the *thing* you actually want to query), but the returned response object contains a property `person` containing a `PersonType` object (like the `ResponseType` modelled above).

### Syntax Highlighting for GraphQL strings in IDEs

.NET 7.0 introduced the [StringSyntaxAttribute](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.codeanalysis.stringsyntaxattribute?view=net-8.0) to have a unified way of telling what data is expected in a given `string` or `ReadOnlySpan<char>`. IDEs like Visual Studio and Rider can then use this to provide syntax highlighting and checking.

From v6.0.4 on all GraphQL string parameters in this library are decorated with the `[StringSyntax("GraphQL")]` attribute.

Currently, there is no native support for GraphQL formatting and syntax highlighting in Visual Studio, but the [GraphQLTools Extension](https://marketplace.visualstudio.com/items?itemName=codearchitects-research.GraphQLTools) provides that for you.

For Rider, JetBrains provides a [Plugin](https://plugins.jetbrains.com/plugin/8097-graphql), too.

Declare query strings as `const string` and the IDE will highlight them at the call site, since the library's query parameters carry `[StringSyntax("GraphQL")]`.


## Useful Links

* [StarWars Example Server (GitHub)](https://github.com/graphql/swapi-graphql)
* [StarWars Example Server (EndPoint)](https://swapi.apis.guru/)

* [GitHub GraphQL API Docs](https://developer.github.com/v4/guides/forming-calls/)
* [GitHub GraphQL Explorer](https://developer.github.com/v4/explorer/)
* [GitHub GraphQL Endpoint](https://api.github.com/graphql)

## Blazor WebAssembly Limitations

Blazor WebAssembly differs from other platforms as it does not support all features of other .NET runtime implementations. For instance, the following WebSocket options properties are not supported and will not be set:
* [ClientCertificates](https://docs.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocketoptions.clientcertificates?view=netcore-3.1#System_Net_WebSockets_ClientWebSocketOptions_ClientCertificates)
* [UseDefaultCredentials](https://docs.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocketoptions.usedefaultcredentials?view=netcore-3.1)
