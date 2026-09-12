# Feather.GraphQL

A GraphQL Client for .NET Standard over HTTP.

# Benchmarks

### Before
| Rows | Static Query (string) - HTTP | LINQ (Interpreted)   | GraphQL.Client      |
|-----:|------------------------------|----------------------|---------------------|
|    1 | **679 ms / 2.69 KB**         | 1,107 ns / 5.60 KB   | 989 ns / 4.49 KB    |
|  100 | **20,646 ns / 30.2 KB**      | 20,513 ns / 33.98 KB | 22,497 ns / 32.1 KB |
| 1000 | **203,773 ns / 277 KB**      | 198,670 ns / 288 KB  | 218,751 ns / 280 KB |

### After
| Rows | Static Query (string) - HTTP | LINQ (AOT Compiled)  | GraphQL.Client   |
|-----:|------------------------------|----------------------|------------------|
|    1 | **522 ns / 2.33 KB**         | **398 ns / 1.95 KB** | 941 ns / 4.40 KB |
|   25 | 3,904 ns                     | 2,562 ns             | 4,433 ns         |
|  100 | 14,663 ns                    | 9,898 ns             | 15,428 ns        |

## Specification
The Library will try to follow the following standards and documents:

* [GraphQL Specification](https://spec.graphql.org/June2018/)
* [GraphQL HomePage](https://graphql.org/learn)

## Packages

| Package | What it is | References |
| --- | --- | --- |
| `Feather.GraphQL.Abstractions` | The query-side contract: variables, the declaring attributes, and the abstract `GraphQLException` | — |
| `Feather.GraphQL.Serialization` | A reply's shape and how to read it: the error model, `GraphQLErrorsException`, the readers, the contract registry | Abstractions |
| `Feather.GraphQL.Http` | `HttpClient` extensions for sending query strings | Abstractions, Serialization |
| `Feather.GraphQL.Linq` | LINQ → GraphQL translation and materialization, plus the analyzer | Abstractions, Serialization |
| `Feather.GraphQL.Linq.Providers.HttpClient` | The LINQ provider over `HttpClient`, plus DI | Linq, Http |

A toolkit, not a framework: **Linq and Http do not reference each other.** Take the translator
without a transport, the transport without a translator, or the provider package that joins them.

`Serialization` is what they share. A reply looks the same whatever carried it — `data`, `errors`,
and a root field — so reading one belongs in neither the transport nor the translator. It holds
the error model (`GraphQLError`, `GraphQLLocation`, `ErrorPath`), the readers that build it, and
the registry those readers resolve contracts through; a websocket or gRPC transport reports
failures in the same vocabulary, and is caught the same way, without taking a dependency on HTTP.
`Abstractions` is the other half of that seam — what a *query* agrees with the code running it —
and it depends on nothing at all.

Failure comes in three widths, so catch at the one you mean:

```csharp
catch (GraphQLHttpException ex)   { ex.Response; }  // the reply: status, headers, body
catch (GraphQLErrorsException ex) { ex.Errors; }    // what the server said was wrong
catch (GraphQLException)          { }               // the query did not answer
```

The middle one is a type rather than a property on the base because a query can fail with nothing
to report — a reply carrying no `data`, one that was not a GraphQL answer at all — and an empty
`Errors` array on those would say the server approved when it never spoke.

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

// Read the data, or throw GraphQLHttpException carrying the server's errors and this response
var graphQLResponse = await httpResponse.ReadGraphQLAsync<PersonResponse>();

// Get the information you need out of the response
var personName = graphQLResponse.Person.Name;
```

A mutation goes through the same call — it is a document like any other.

### Query with LINQ

The LINQ provider translates; it does not transport. `Feather.GraphQL.Linq` knows nothing about
HTTP, and `Feather.GraphQL.Http` knows nothing about LINQ — the two meet only in
`Feather.GraphQL.Linq.Providers.HttpClient`, which is the package to install for the usual case.
Another transport (websocket, in-process schema, recorded fixtures) is one implementation of
`IGraphQLQueryExecutor`.

Turn an `HttpClient` into a query and finish it with an ordinary LINQ terminal. The type stays a
plain POCO — nothing is attributed; what the schema calls things is said at the call site:

```csharp
using var client = new HttpClient { BaseAddress = new Uri("https://countries.example/") };

List<Country> european = await client.CreateQueryable<Country>("countries")
    .Where(c => c.Continent.Name == "Europe")
    .OrderBy(c => c.Name)
    .Take(25)
    .ToListAsync(ct);
```

`"countries"` is the field on the schema's `Query` type. Everything else the schema requires goes
in the options delegate:

```csharp
client.CreateQueryable<Country>("countries", o =>
{
    o.EndpointPath = "api/v2/graphql";   // default: the client's BaseAddress
    o.Paging = PagingKind.Cursor;        // nodes { … } and first:/last:
    o.FilterInput = "CountryWhereInput"; // default: {Type}FilterInput
    o.SortInput = "CountryOrderInput";   // default: {Type}SortInput
    o.FilterProvider = new MyDialect();  // default: HotChocolate's
});
```

Options can also be built elsewhere and shared — as an instance, or from a container that
configured them:

```csharp
services.Configure<GraphQLHttpQueryOptions>(o => o.EndpointPath = "api/v2/graphql");

public sealed class Service(HttpClient client, IOptions<GraphQLHttpQueryOptions> options)
{
    public Task<List<Country>> AllAsync(CancellationToken ct) =>
        client.CreateQueryable<Country>("countries", options).ToListAsync(ct);
}
```

That is the whole setup. There is no registration call: headers, auth and retry are configured on
the client the way they would be for any other use of it, and how the client reaches the code
using it — a field, a constructor parameter, a DI registration of your own — is your app's
choice, not this library's. Talking to two schemas means two clients.

Several query types against one endpoint is several calls on the same client — there is nothing
in between to build or hold:

```csharp
var europe = await client.CreateQueryable<Country>("countries").Where(…).ToListAsync(ct);
var africa = await client.CreateQueryable<Continent>("continents").Where(…).ToListAsync(ct);
```

> [!NOTE]
> Endpoint resolution is `HttpClient`'s own, which treats a base address as a document rather than
> a directory: `https://host/v1` plus `graphql` gives `https://host/graphql`, not
> `https://host/v1/graphql`. Give the base address a trailing slash to keep its path.

#### Filtering

`Where` is LINQ's, and lowers to the server's filter input. Two extra overloads live in
`Feather.GraphQL.Linq.Filtering`, next to the lowering they configure:

```csharp
using Feather.GraphQL.Linq.Filtering;

// name the schema's filter argument inline — HotChocolate says "where", this schema says "filter"
.Where("filter", p => p.Age > 30)

// write the predicate against a model of the filter input, when it is not shaped like the result
.Where((CountryFilter f) => f.Continent == "Europe")

// or rename several arguments at once
.WithGraphQLArguments(new() { Filter = "filter", Order = "sort" })
```

That namespace is a deliberate opt-in: these are extensions on `IQueryable<T>`, and an
auto-imported one would appear on every `DbSet` in a solution.

> [!NOTE]
> With it imported, passing a *prebuilt* `Expression<Func<T, bool>>` to `Where` is ambiguous with
> LINQ's own overload. Write the lambda inline, or call `Queryable.Where(source, predicate)`.
> Inline lambdas and the two-argument `Where(argumentName, predicate)` form are unaffected.

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

#### Materializing without reflection

Referencing the package also brings a generator that emits each queried type's field table and
registers it at load, so translation does not reflect. Reading the response can skip reflection
too — declare a `JsonSerializerContext` covering the types you query:

```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Country))]
[JsonSerializable(typeof(Continent))]
internal sealed partial class CountrySerializerContext : JsonSerializerContext;
```

That is the whole of it — the generator finds the context and registers it for you. It has to
live in your source rather than be generated, because `System.Text.Json`'s generator cannot see
what another generator emits.

A type no context covers still materializes by reflection, so nothing breaks if you skip this or
miss a type.

Projections go the same way. When a chain is written as one expression, the generator compiles
its `Select` at build time instead of emitting IL at runtime:

```csharp
await client.CreateQueryable<Country>("countries")
    .Where(c => c.Continent.Name == "Europe")
    .Select(c => new { c.Name, Continent = c.Continent.Name })   // shaped at build time
    .ToListAsync(ct);
```

This applies to anonymous projections over member paths — the common case. A projection the
generator declines is compiled at runtime as before, so nothing breaks; it is only slower.

Result operators are translated, not applied after the fact: `First()` asks for a page of one,
`Any()` selects a single scalar, `Count()` reads the connection's `totalCount`, and `Last()`
reads backwards with cursor paging's `last:`. Where a paging kind cannot express one, it is a
translation error rather than a silent full fetch — `Count()` on an un-paged field is FGQL009.

`ToGraphQLQuery()` translates a chain without sending it, writing every argument into the
document so you can read what the query actually asks:

```csharp
GraphQLQueryable.For<Person>("people").Where(p => p.Age > 30).OrderBy(p => p.Name)
    .ToGraphQLQuery();
// query { people(where: {age: {gt: 30}}, order: [{name: ASC}]) { name age emailAddress } }
```

That is the debugging form and pastes straight into a playground. What actually goes over the
wire binds every argument to a variable — `people(where: $v0)` — which is what keeps one document,
and therefore one APQ key, covering every predicate of a given shape. That document reaches a
custom transport through `IGraphQLQueryExecutor`, which is where an APQ hash belongs.

Both are plain `string`s. The public API deals in strings, expressions and `IQueryable<T>`; there
is no query wrapper type to learn.

To run queries over something other than HTTP, implement `IGraphQLQueryExecutor` and hand it to
`GraphQLQueryable.For<T>`. It receives the parameterized document and its variables, and returns
the response's `data` element — nothing about how the answer is shaped crosses that seam:

```csharp
public sealed class MyExecutor : IGraphQLQueryExecutor
{
    public IFilterTranslationProvider FilterProvider => HotChocolateFilterProvider.Instance;

    public ValueTask<JsonElement> ExecuteAsync(
        string query, IReadOnlyDictionary<string, object?> variables, CancellationToken ct) => …;
}

IQueryable<Country> countries = GraphQLQueryable.For<Country>(new MyExecutor(), "countries");
```

Sync terminals (`ToList()`, `First()`, `foreach`) block on the request the way EF Core's do.
The async counterparts — `ToListAsync`, `ToArrayAsync`, `FirstAsync`, `SingleAsync`, `AnyAsync`,
`CountAsync`, `LastAsync` and `AsAsyncEnumerable` — take a `CancellationToken` and don't.

#### Compiling a query

`[GraphQLQuery]` declares a query the compiler implements. There is one attribute and two ways to
say what the query is, and the method says which by its own shape: a **document** on a `partial`
method, or a **chain** as the body.

```csharp
// Written as a document — the compiler writes the body.
[GraphQLQuery("query($code: String!) { countries(where: { code: { eq: $code } }) { name } }")]
public static partial Task<Country[]> ByCodeAsync(
    HttpClient client, string code, CancellationToken ct = default);

// Written as a chain — the compiler replaces every call with what it compiles to.
[GraphQLQuery]
private static Task<Country[]> ByCodeAsync(
    HttpClient client, string code, CancellationToken ct)
    => client.CreateQueryable<Country>("countries")
        .Where(c => c.Code == code)
        .ToArrayAsync(ct);
```

Both compile to the same thing: a document literal, a variables payload written from the method's
parameters, and a reader generated from the query's own reply. Which to write is a question of
what the query *is* — a document says exactly what goes over the wire, a chain keeps the query in
C# where the compiler checks it against the type it queries.

The rest of this section is about the chain form, since the document form is what it compiles to.

Composing a chain costs something before this library is entered at all: the compiler allocates
an expression tree at the call site on every execution — about a microsecond and three kilobytes
— which the provider then walks to recover values the caller already had. Isolate the chain to a
method and mark it, and none of that happens:

```csharp
[GraphQLQuery]
private static Task<Country[]> ByCodeAsync(
    HttpClient client, string code, CancellationToken ct)
    => client.CreateQueryable<Country>("countries")
        .Where(c => c.Code == code)
        .ToArrayAsync(ct);
```

The method is the query, written the way you would have written it anyway. What the attribute
adds is a boundary: a chain sitting in the middle of a method binds whatever is in scope, and
what is in scope is only known where it runs — but isolated to a method it binds that method's
parameters, which the caller supplies. So the compiler writes the document, the variables payload
and the read of the reply, and replaces every call to `ByCodeAsync` with them. The body never
runs. Nothing is composed, nothing is walked, and nothing is reflected over, which is also what
makes it run under NativeAOT unqualified.

Most of a chain compiles. `Where`, `OrderBy`/`ThenBy`, `Skip`/`Take`, the result operators
(`FirstAsync`, `SingleAsync`, `AnyAsync`, `CountAsync` and their variants), offset and cursor
paging, and a `Select` that narrows to a named type all reach the compiled path:

```csharp
[GraphQLQuery]
private static Task<Person> OldestNamedAsync(
    HttpClient client, string name, CancellationToken ct)
    => client.CreateQueryable<Person>("people")
        .Where(p => p.Name == name)
        .OrderByDescending(p => p.Age)
        .FirstAsync(ct);
```

An ordering binds no value at all — which member and which direction are both in the source — so
it is written into the payload as a constant. A page size binds one, and a parameter is where it
comes from. A result operator decides its own page (`First` asks for one row, `Single` for two)
and reduces what comes back, throwing exactly what the runtime throws when there is nothing or
too much.

Projections shape exactly as they do at runtime, because they *are* the projection you wrote.
The rows come back as the queried type and your `Select` runs over them — copied into the
generated code, compiled by your compiler, in your assembly — so a rename, a nested path, a
constructor and a bare member all behave the same compiled as composed:

```csharp
.Select(p => new Summary { Title = p.Name, Lead = p.Team.Lead.Name })   // shapes, not maps
```

Reading the reply *into* `Summary` would have been the obvious implementation and the wrong one:
`Title` is not `name`, and `Lead` is not a field at all, so both would come back null — a wrong
answer rather than a failure. Nothing is matched by name.

What is still bounded: one chain per method, values from that method's own parameters or
constants, the client among the parameters, and no endpoint or filter dialect chosen in the
options. A projection is declined only when copying it would change what it means — it reads the
declaring type's own state, or calls something private that the generated file cannot reach. A
chain outside all that keeps the runtime translation it would have had anyway, and `FGQL015` says
which one and why rather than letting the attribute do nothing silently.

The two forms fail differently, and deliberately. A chain the compiler declines still has a body
to run, so `FGQL015` is a warning and the query keeps working. A document it cannot read leaves a
`partial` method with nothing at all, so `FGQL016` is an error that says what to change.




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
