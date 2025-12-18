# Feather.GraphQL

A GraphQL Client for .NET Standard over HTTP.

## Specification
The Library will try to follow the following standards and documents:

* [GraphQL Specification](https://spec.graphql.org/June2018/)
* [GraphQL HomePage](https://graphql.org/learn)

## Usage

This library provides various extensions and utilities to extend base .NET networking functionality with GraphQL concepts.

If you're familiar with the `System.Text.Json` extensions to `HttpClient`, this was designed to be familiar!

### Create a GraphQLRequest:
#### Simple Request:
```csharp
var heroRequest = new GraphQLRequest {
    Query = """
    {
        hero {
            name
        }
    }
    """
};
```

#### OperationName and Variables Request:

> [!WARNING]
> Be careful when using `byte[]` in your variables object, as most JSON serializers will treat that as binary data.
>
> If you really need to send a *list of bytes* with a `byte[]` as a  source, then convert it to a `List<byte>` first, which will tell the serializer to output a list of numbers instead of a base64-encoded string.

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
var personAndFilmsRequest = new GraphQLRequest {
    Query ="""
    query PersonAndFilms($id: ID) {
        person(id: $id) {
            name
            filmConnection {
                films {
                    title
                }
            }
        }
    }
    """,
    OperationName = "PersonAndFilms",
    Variables = new {
        id = "cGVvcGxlOjE="
    }
};

// Use an HTTP client from somewhere (DI, Factory, etc)
var httpResponse = await httpClient.SendGraphQLQueryAsync(personAndFilmsRequest);

// Read the response headers/content similar to string or JSON data
var graphQLResponse = httpResponse.Content.ReadAsGraphQLAsync<PersonResponse>();

// Get the information you need out of the response
var personName = graphQLResponse.Data.Person.Name;
```

> [!IMPORTANT]
> Note that the field in the GraphQL response which gets deserialized into the response object is the `data` field.
>
> A common mistake is to try to directly use the `PersonType` class as response type (because thats the *thing* you actually want to query), but the returned response object contains a property `person` containing a `PersonType` object (like the `ResponseType` modelled above).

### Syntax Highlighting for GraphQL strings in IDEs

.NET 7.0 introduced the [StringSyntaxAttribute](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.codeanalysis.stringsyntaxattribute?view=net-8.0) to have a unified way of telling what data is expected in a given `string` or `ReadOnlySpan<char>`. IDEs like Visual Studio and Rider can then use this to provide syntax highlighting and checking.

From v6.0.4 on all GraphQL string parameters in this library are decorated with the `[StringSyntax("GraphQL")]` attribute.

Currently, there is no native support for GraphQL formatting and syntax highlighting in Visual Studio, but the [GraphQLTools Extension](https://marketplace.visualstudio.com/items?itemName=codearchitects-research.GraphQLTools) provides that for you.

For Rider, JetBrains provides a [Plugin](https://plugins.jetbrains.com/plugin/8097-graphql), too.

To leverage syntax highlighting in variable declarations, use the `GraphQLQuery` class.


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
