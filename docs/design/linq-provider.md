# Feather.GraphQL.Linq — Design

A generic `IQueryable<T>` implementation for GraphQL over HTTP, backed by a Roslyn
source generator.

## 1. Scope

**v1 ships two things: a filter translator, and a request builder that uses it.**

1. `IQueryable<T>` → HotChocolate filter object. An extension on *any* `IQueryable`,
   independent of this library's provider (§4).
2. `IQueryable<T>` → a GraphQL document, plus a thin send (§5).

The first is the primitive; the second is its first consumer. That ordering is
deliberate — the filter translator is useful on its own, testable on its own, and is the
part most likely to be wrong.

The response comes back as an `HttpResponseMessage` and the caller pulls it apart
themselves — with `ReadGraphQLAsync<T>()` from `Feather.GraphQL.Http`, with raw
`Utf8JsonReader`, or however they like. This is the library's whole premise: use the
tools you need, shelf the magic you don't.

The consequence is that v1 is a **pure function** — expression tree in, query text and
variables out — with a thin send on the end. Every interesting behaviour is testable
without a server, without JSON, and without reflection over responses.

Deferred until the translator is proven, in rough order:

| Deferred | Why it can wait |
| --- | --- |
| Response materialization | `ReadGraphQLAsync<T>` already exists and covers it |
| Plan cache | Translation cost is irrelevant until it's on a hot path |
| Generated `JsonSerializerContext` | Only needed once the library owns deserialization |
| Client-side computation in projections | Needs a materializer to run in |
| Error policy for partial `data`+`errors` | Caller sees the raw response and decides |
| Interfaces / unions | Selection-set concern, but not needed to prove lowering |

### 1.1 Decisions that frame this design

| Decision | Choice |
| --- | --- |
| Schema source | Hand-written POCOs. No attributes, no SDL/introspection file. |
| Untranslatable LINQ | Compile error, always. No client-side fallback. |
| Codegen depth | Runtime `IQueryProvider`. No interceptors, no compile-time document emission. |
| Server dialect | HotChocolate 16 filtering/sorting/paging conventions, hardcoded for v1. |

The last one is a deliberate v1 shortcut. Predicate lowering cannot be derived from the
POCO alone — the shape of `where:` is a property of the server, not the client — so v1
targets one server's conventions rather than inventing a plugin seam before there is a
second implementation to validate it against. The lowering code is isolated behind an
internal `IFilterTranslationProvider` so that extracting it later is a refactor rather
than a redesign.

## 2. Package layout

```
src/
  Feather.GraphQL.Linq/                net10.0
      Attributes and metadata contracts, filter lowering + IQueryable
      extensions (§4), IR + printer, query translator, executing provider.
  Feather.GraphQL.Linq.Analyzers/      netstandard2.0   (ships inside the Linq package)
  Feather.GraphQL.Linq.Providers.HttpClient/   net10.0
      The HttpClient transport: the one assembly referencing both Linq and Http.
```

The attributes and metadata contracts once lived in a separate `Linq.Abstractions`, so
that the rule table could be written to the netstandard2.0 subset and `<Compile
Include=… Link=…>`'d into the analyzer — analyzers must ship all their dependencies
inside the analyzer package, and cannot reference a `net10.0` assembly. That never
happened: the analyzer needs its rules over `ISymbol`, not over `System.Type`, so
`GraphQLTypeFacts` is a deliberate second implementation (§7) and shares no source. With
the only reason for the split gone, the split went with it.

`netstandard2.0` on the analyzer remains a hard Roslyn requirement — it cannot use the
`net10.0` language and BCL surface the rest of this repo enjoys.

A model assembly that wants only the attributes now references the whole of
`Feather.GraphQL.Linq`. That is the cost, and it is small: any assembly defining a
queried type is already the one composing queries over it — and the whole of
`Feather.GraphQL.Linq` is now dependency-free, referencing no other assembly in the
repo. Translation needs nothing from the response types, which is the same separation
that keeps it independent of the transport.

## 3. The type model — POCOs + attributes

Assuming a basic schema:
```graphql
type Query {
  person(id: String!): Person
  people(where: PersonFilterInput, order: [PersonSortInput!]): [Person!]!
}

type Person {
  id: String!
  name: String!
}
```

And a partial definition with a marker attribute:
```csharp
// A plain POCO — nothing marks it as queryable.
public sealed partial class Person
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}
```

The following code should be able to be written:
```csharp
// Pure — no client, no I/O. This is the unit of test.
string query = GraphQLQueryable.For<Person>("people")
    .Where(p => p.Name == "John Smith")
    .ToGraphQLQuery();
// query { people(where: {name: {eq: "John Smith"}}) { … } }

// A queryable with a transport executes. The terminal is an ordinary LINQ terminal.
List<Person> people = await client.CreateQueryable<Person>("people")
    .Where(p => p.Name == "John Smith")
    .ToListAsync(ct);
```

Rules:

- Types must be `partial` only if the generator is to emit a metadata member for them.
- Nothing is required on the type itself: it stays a POCO.
- Nullability comes from the C# nullable annotation, which is what drives generated
  variable types (`String!` vs `String`).
- Field names are pulled from `[JsonPropertyName]`, `[DataMember]`, then the symbol in that order.
- `[JsonIgnore]` means "not a GraphQL field". Referencing an ignored property from a
  query is `FGQL005`. Without this opt-out the diagnostic could never fire, since the
  symbol-name fallback makes every property mappable.

Reusing `[JsonPropertyName]` means the GraphQL field name and the JSON response key are
the same string by construction, which is exactly what makes materialization free — the
selection set and the deserializer read the same map, so there is no mapping layer to
drift.

### 3.1 One root field per type

The string in `CreateQueryable<Person>("people")` names the field on `Query`, not the
type. Each query names exactly one root field, which means the sample schema's
`person(id:)` is reached by querying `people` with a filter rather than by a second
binding.

This is a deliberate limit, not an oversight. Under HotChocolate filtering, a singular
lookup is expressible through the filtered collection:

```csharp
// person(id: "1") — unreachable, and unnecessary
GraphQLQueryable.For<Person>().Where(p => p.Id == "1").Take(1).ToGraphQLQuery();
```

The alternative — a repeatable attribute plus a root-selector argument at every call
site — costs real API surface to reach fields that the filtered collection already
covers. If a schema ever exposes a root field with semantics the collection genuinely
cannot express, revisit then.

### 3.2 Server capabilities that must be declared

Two things about the root field cannot be inferred from the POCO and must be stated on
the attribute:

```csharp
// None | Cursor | Offset
client.CreateQueryable<Person>("people", o => o.Paging = PagingKind.None);
```

**`Paging` is the load-bearing one.** HotChocolate's `[UsePaging]`, `[UseOffsetPaging]`,
and un-paged fields wrap the selection set differently, and that wrapping is part of the
request text v1 emits:

| `PagingKind` | Emitted selection | `Skip` | `Count()` |
| --- | --- | --- | --- |
| `None` | `people { … }` | — | `FGQL009` |
| `Cursor` | `people { nodes { … } }` | `FGQL008` | `totalCount` |
| `Offset` | `people { items { … } }` | `skip: n` | `totalCount` |

The translator emits the wrapper and the materializer walks it, both from this one
declaration — which is why they are produced together as one internal plan rather than being
derived twice. A response that contradicts the declaration is `FGQL018`.

## 4. Filter translation

The lowering from a predicate to a HotChocolate filter input needs exactly two things:
an expression tree, and a way to map a CLR member to a field name. It needs nothing from
this library's provider, nothing about how the type is queried, and nothing about roots or
paging. So it is not a private step inside the query translator — it is a standalone
component the query translator happens to call.

That makes it an extension on **any** `IQueryable<T>`:

```csharp
using Feather.GraphQL.Linq.Filtering;

// A source this library knows nothing about.
IQueryable<Person> source = dbContext.People;          // or people.AsQueryable()

JsonObject filter = source
    .Where(p => p.Name == "John Smith" && p.Age > 30)
    .ToGraphQLFilter();

// → { "name": { "eq": "John Smith" }, "age": { "gt": 30 } }
```

Uses this unlocks beyond the query builder: forwarding a locally-composed or
EF-originated predicate to an upstream GraphQL service; building a `where` payload to
drop into a variables payload by hand; and testing predicates without touching a
queryable at all.

### 4.1 Surface

```csharp
extension<T>(IQueryable<T> source)
{
    JsonObject?            ToGraphQLFilter();     // merged Where clauses
    JsonArray?             ToGraphQLSort();       // merged OrderBy/ThenBy
    GraphQLFieldArguments  ToGraphQLArguments();  // where + order + skip/take together
}
```

`ToGraphQLFilter` reads only `Where`; `ToGraphQLSort` only the ordering operators. Both
**throw on operators they do not consume** rather than ignoring them — silently dropping
a `Take` from a chain someone believed was fully translated is the kind of bug that
surfaces as missing data in production. `ToGraphQLArguments` is the "consume everything"
form for callers proxying a whole query.

Two output forms, because they are not the same language:

- `JsonObject` for the **variables** payload — this is the normal path.
- `.ToGraphQLValueString()` for inlining into query text, which is GraphQL *value*
  syntax, not JSON: unquoted keys, bare enum identifiers. **Not implemented** — §5.3 lifts
  filters to variables, so nothing in v1 needs it, and building it correctly means tracking
  enum identity through the `JsonObject` (a JSON string and a GraphQL enum are
  indistinguishable once lowered). Deferred until a caller actually hand-writes a document.

**Naming:** the method is `ToGraphQLFilter`, not `ToHotChocolateFilter`, with an optional
dialect argument defaulting to HC. Baking the vendor into the public API contradicts the
`IFilterTranslationProvider` seam in §1.1 and guarantees a rename when a second dialect
lands.

**Namespace hygiene:** these live in `Feather.GraphQL.Linq.Filtering`, not a namespace
anything auto-imports. An extension on `IQueryable<T>` is otherwise visible on every
`DbSet` in the solution.

### 4.2 Field naming, and where reflection creeps in

Member → field name uses the §3 rule: `[JsonPropertyName]`, `[DataMember]`, then the
symbol name.

When a generator has emitted that table for a type, lowering is reflection-free. For
every other type — someone's EF entity, or any POCO nobody opted in — there is no
generated table, so the fallback reflects over the type once and caches the result.
**The no-reflection property therefore holds only for types the generator saw**, and the
doc should not claim otherwise.

Opting in is the generator's own concern now that nothing else needs an attribute: the
field table is all it produces, and everything about *how* a type is queried is options
at the call site.

### 4.3 Lowering table

| Expression | Lowered to |
| --- | --- |
| `p.Name == x` / `!=` | `{ name: { eq: … } }` / `{ neq: … }` |
| `p.Age > x` / `>=` / `<` / `<=` | `{ age: { gt / gte / lt / lte: … } }` |
| `a && b` | merged into one field object; `and: [ … ]` only when the *same operation* repeats |
| `a \|\| b` | `or: [ … ]` |
| `!expr` | negated operation (`neq`, `ncontains`, …), pushed to the leaf |
| `p.Name.Contains/StartsWith/EndsWith(x)` | `{ name: { contains / startsWith / endsWith: … } }` |
| `list.Contains(p.Id)` | `{ id: { in: … } }` |
| `p.Id == a \|\| p.Id == b` | `{ id: { in: [a, b] } }` — see below |
| `p.Address.City == x` | `{ address: { city: { eq: … } } }` |
| `p.Tags.Any(t => …)` / `.All(…)` | `{ tags: { some / all: { … } } }` |
| `!p.Tags.Any(t => …)` | `{ tags: { none: { … } } }` |

Multiple `Where` calls merge as `&&`. Because HC filter inputs carry native `and:`/`or:`,
disjunction translates cleanly — there is no "conjunctions only" restriction. Anything
else in a predicate (arbitrary method calls, unbound members, captured delegates) is
`FGQL002`.

Sorting lowers into `order:`, one entry per `OrderBy`/`ThenBy` in chain order, each
`{ field: ASC|DESC }`. Ordering keys must be member accesses; computed keys are `FGQL004`.

Expect EF-flavoured predicates to fail here: `EF.Functions.Like`, `Include`, and
provider-specific calls have no filter-input equivalent and are `FGQL002`. That is
correct behaviour, but it is worth documenting for anyone pointing this at a `DbSet`.

## 5. Query translation

Everything below builds on §4 and applies only to queryables rooted at
`GraphQLQueryable.For<T>()`.

### 5.1 Projection determines the selection set

`Select` exists in v1 for one reason: to produce the selection set.

```csharp
.Select(p => new { p.Id, p.Name })   // → { id name }
```

A projection has two jobs. It names the fields to request, and — once the response
arrives — it runs as an ordinary lambda over each materialized element. The second job
is why a projection may compute; the first is why it may not compute over *anything*.
The limit is not what the projection does, it is what the builder can read the required
fields out of. `p.Name.ToUpperInvariant()` is `FGQL013` because nothing in it says
whether `name` or something else is wanted.

Three shapes are readable:

- **Member trees.** `p.Size!.Minimum` walks onto the selection tree directly.
- **LINQ chains over a collection member.** `p.Parts.Primary.Select(x => new { x.Name })
  .ToArray()` — every operator in the chain runs client-side over what came back, so
  none of them changes which fields to request. The source names the collection and each
  lambda names fields of its elements; the materializing call asks for nothing at all,
  it is C# needing an array.
- **An object member named bare.** `p.Size` selects `size`'s scalar fields, because a
  GraphQL object field must carry a selection set and naming it without one can only
  mean "what is in it".

That last expansion is **one level, scalars only** — and nested fields inside the member
are *skipped*, not refused. The same rule is the no-`Select` default: `T`'s own scalars.

Skipping rather than refusing is the load-bearing choice. A scalar is there for the
asking, so taking every one costs nothing anybody would object to; a nested field is a
second trip through a resolver, and helping yourself to those is how a query quietly
grows past a depth limit. It also makes a self-referencing model harmless without any
cycle detection: `Country.Continent` holds `Continent.Countries`, which is `Country`
again, but expansion takes `code` and `name` and stops. There is nothing to detect
because nothing recurses.

The price is that **a nested field nobody asked for comes back unset**, which is the
right default for a transport where every extra field is billed to the server, but has to
be said out loud.

One case survives: a type whose every field is nested contributes an empty selection set,
and GraphQL has no such thing. That is `FGQL014`, and it is **decidable from the source**
— which is why §7's analyzer reports it at the call site rather than leaving it to the
first request.

Separately, an **unbounded** query is `FGQL012`: a chain with no `Where`, no `Take` and
no `Select` is a fetch-everything request and almost always a mistake. Any one of the
three satisfies the rule.

### 5.2 Operator table

| LINQ | GraphQL | Notes |
| --- | --- | --- |
| `Select` | selection set | Member trees, LINQ chains over a collection member, and bare object members (§5.1) |
| `Where` | `where:` filter input | Lowered per §4.3; must sit directly on a field |
| `Where((TFilter f) => …)` | `where:` filter input | Predicate over a model of the input (§5.5) |
| `Where(name, predicate)` | `name:` filter input | Names the filter argument inline (§5.5) |
| `OrderBy` / `ThenBy` / `…Descending` | `order:` sort input | Per §4.3 |
| `Take(n)` | `first: n` (`Cursor`) / `take: n` (`Offset`) | |
| `Skip(n)` | `skip: n` (`Offset` only) | `FGQL008` under `PagingKind.Cursor` — cursors, not offsets |
| `WithGraphQLArguments(names)` | — | Not an operator: renames the arguments above. Last call wins |
| `First` / `FirstOrDefault` | `first`/`take: 1` | Reduced from the page the server returned |
| `Single` / `SingleOrDefault` | `first`/`take: 2` | Two rows: enough to prove it was not two |
| `Any` | `take: 1` + one scalar field | The value is discarded, so the selection is minimal |
| `Count` / `LongCount` | `totalCount` | `FGQL009` on `PagingKind.None`, or after `Skip`/`Take` |
| `Last` / `LastOrDefault` | `last: 1` | `Cursor` only — `FGQL015` otherwise |
| `ToGraphQLQuery()` | — | Terminal. Pure; the document with its arguments written out |
| `ToList` / `ToArray` / `foreach` | — | Terminal. Executes, blocking as EF Core's do |
| `…Async(ct)` / `AsAsyncEnumerable()` | — | Terminal. Same translation, without blocking |

The §4 terminals (`ToGraphQLFilter`, `ToGraphQLSort`, `ToGraphQLArguments`) also work
here — a `GraphQLQueryable` is still an `IQueryable`.

Every operator above executes against the endpoint the queryable came from.
They describe a *result*, and v1 does not produce results — it produces requests. They
arrive with the materializer, at which point `First` → `first: 1`, `Single` → `first: 2`
plus a cardinality check, and `Count` → `totalCount`. Using one in v1 is `FGQL001`.

Rejected permanently, with `FGQL001`: `Join`, `GroupJoin`, `GroupBy`, `Distinct`,
`Concat`, `Union`, `Except`, `Zip`, `Aggregate`, `Sum`/`Min`/`Max`/`Average`,
`Last`/`LastOrDefault`, `Reverse`, `SelectMany` across roots, `ElementAt`. None of these
have server semantics that can be assumed, and silently doing them in memory is exactly
what decision #2 forbids.

Operator *order* is normalized before translation (`Where` after `Select` is hoisted when
the predicate only touches projected-through fields), so semantically equivalent chains
produce identical request text.

### 5.3 Variables

Captured locals and inline literals alike are lifted to GraphQL variables, typed from
the argument's input type. Consequences:

- Query text is constant per query *shape*.
- APQ is genuinely useful — the hash is stable across calls.
- Injection is structurally impossible; no value is ever concatenated into the document.

**Lift the whole filter, not its leaves.** The `where:` argument becomes a single
variable of the filter input type, carrying the §4 `JsonObject` as variable *data*:

```graphql
query($where: PersonFilterInput) { people(where: $where) { id name } }
```

This means `.Where(p => p.Name == x)` and `.Where(p => p.Age > y && p.Name != z)` emit
**identical query text**, differing only in the variables payload. Predicate shape moves
entirely out of the document, so one APQ hash covers every predicate over a given
selection set. Per-leaf variables would produce a distinct document per predicate shape
and throw away most of the benefit. `order:` lifts the same way.

This is also why §4 hands back a `JsonObject` rather than a string: it drops straight
into the variables payload with no re-parse.

### 5.4 Argument names belong to the schema, not the translator

`where`, `order`, `take`, `skip`, `first` and `last` are HotChocolate's names, not
GraphQL's. A schema is free to call its filter `filter`, and the countries API does.

The names ride in the expression tree, planted by `WithGraphQLArguments` the way EF
Core's `Include` plants its own marker. That choice buys three things a provider-level
setting would not: it composes anywhere in the chain, it survives every subsequent
operator, and it works over an `IQueryable<T>` this library never created — the same
property that makes the §4 extensions usable over an EF `DbSet`.

It is deliberately only the *names*. The shape inside the filter is
`IFilterTranslationProvider`'s job, and conflating the two would put half of a dialect in
each place.

### 5.5 The filter input is its own type

Field *names* are one half of a dialect; the other is that a filter input need not be
shaped like the thing it filters. `Country.continent` comes back as an object with a
`name`; `CountryFilterInput.continent` takes a string filter. No renaming reconciles
that — the paths differ in depth.

So `Where` gains an overload whose predicate is written against a model of the input:

```csharp
.Where((CountryFilter f) => f.Continent == "Europe")   // {"continent":{"eq":"Europe"}}
```

The chain stays `IQueryable<T>`: only the predicate changes shape, so selection and
materialization are untouched. `FilterTranslator` needed no change at all — it already
resolves fields from each member's declaring type, so a lambda over `CountryFilter`
lowers through `CountryFilter`'s metadata.

The argument's GraphQL type name still comes from the queried type, because it is a
property of the root field rather than of the CLR type used to express the predicate.

**Why the lambda parameter is typed rather than the method.** `.Where<CountryFilter>(…)`
reads naturally but cannot compile: C# binds an explicit type argument to the extension
block's own type parameter first, so it would mean `T = CountryFilter` and fail to match
the receiver. A classic two-parameter method cannot help either, since C# has no partial
type-argument inference. Two spellings survive — an explicitly-typed lambda parameter,
`(CountryFilter f) => …`, or both type arguments named, `Where<Country, CountryFilter>(f
=> …)` — and either keeps the chain strongly typed through `Select`.

**The argument name may ride with the predicate.** Renaming one argument is the common
case and the one a simple query hits first, so `Where` carries an overload that takes it:

```csharp
.Where("filter", (CountryFilter f) => f.Continent == "Europe")
.Where("filter", p => p.Age > 30)                    // over the queried type
```

It writes the same slot `WithGraphQLArguments` writes, so the later call in the chain
wins and there is one rule rather than a precedence table. `WithGraphQLArguments` remains
the way to rename several arguments at once; nothing about a simple query requires
knowing it exists.

Mixing the two shapes in one chain is `FGQL020`. They lower to different paths, and
merging them with `&&` would produce a filter matching neither.

Both `Where` overloads and `WithGraphQLArguments` live in `Feather.GraphQL.Linq.Filtering`
rather than `…Query`, next to the lowering they configure and behind the same deliberate
opt-in: an extension on `IQueryable<T>` that is auto-imported appears on every `DbSet` in
a solution.

**One sharp edge comes with that.** `Where<TFilter>(Expression<Func<TFilter, bool>>)` has
the same shape as LINQ's own `Where` once `TFilter` is inferable, so passing a *prebuilt*
`Expression<Func<T, bool>>` is ambiguous wherever this namespace is imported:

```csharp
using Feather.GraphQL.Linq.Filtering;

Expression<Func<Person, bool>> predicate = p => p.Age > 30;
people.Where(predicate);            // CS0121 — ambiguous
Queryable.Where(people, predicate); // qualify, or write the lambda inline
```

An inline lambda is fine, because `TFilter` cannot be inferred from an untyped parameter
and only LINQ's overload applies. The two-argument form — `Where(argumentName, predicate)`
— never collides. If this proves more than a curiosity, the single-argument filter-shape
overload wants a name of its own rather than an overload of `Where`.

## 6. Runtime

### 6.1 The provider executes

`CreateQuery` captures the tree; `Execute` runs it through the queryable's
`IGraphQLQueryExecutor` and materializes the answer. Enumerating executes too, so
`ToList`, `ToArray` and `foreach` are ordinary terminals rather than traps.

`IQueryable<T>` has no async contract, so the async terminals are extensions that reach
the provider directly — the shape EF Core uses, for the same reason. Sync terminals block
on the async path, also as EF Core's do: refusing to answer `ToList()` would reintroduce
the runtime surprise the executing provider exists to remove.

A provider without an executor still captures and still translates, but cannot run:
`GraphQLQueryable.For<T>()` is that case, and a terminal that would execute is `FGQL016`.
Pass an `IGraphQLQueryExecutor` for a queryable that runs.

### 6.2 Pipeline

The translator's output stops at an internal plan — document, variables, and the shape of
the answer. It names no transport type, which is what keeps `Feather.GraphQL.Linq` free of
any reference to `Feather.GraphQL.Http`. Running one is an `IGraphQLQueryExecutor`, and the
`HttpClient` implementation of it lives in `Feather.GraphQL.Linq.Providers.HttpClient` —
the one assembly that references both.

**The seam is narrower than the plan.** A transport is handed a document and its variables
and returns a `data` element; it never sees the element type, the paging kind, the
projection or the result operator, because all four are read by the materializer *after*
the transport has returned. Passing the whole plan across would have made every one of
them public to describe a contract that uses two of them. So the plan is internal and the
seam takes `(string query, IReadOnlyDictionary<string, object?> variables)` — which also
puts the APQ hash exactly where it can be computed, since the document that crosses the
seam is the one that goes on the wire.


```
client.CreateQueryable<T>(root) … .ToListAsync(ct)
   → read expression tree
   → translate → GqlDocument → canonical print
   → document text  +  variables                      ← what crosses the transport seam
                                          ← Feather.GraphQL.Linq ends here
   → IGraphQLQueryExecutor                            (transport seam)
   → internal wire request + POST                     (Providers.HttpClient)
   → data → root field → nodes/items                  (paging wrapper the translator emitted)
   → deserialize elements → apply Select → reduce by result operator
   → List<T>
```

**Two printings, one document.** The parameterized form is what is sent, and §5.3 is why:
it keeps the document constant per query shape, so one APQ hash covers every predicate and
injection is structurally impossible. But it also means the printed text says
`where: $v0` and nothing about what was asked — useless as a diagnostic. So the printer
has a second mode that substitutes each variable for its value, and `ToGraphQLQuery()`
uses it. Only the *names* of things differ between the two; the selection set, the paging
wrapper and the argument names are identical.

The one place the two languages genuinely disagree is enums. In a variables payload an
enum is JSON text — `"ASC"` — and indistinguishable from a string; inlined it is a bare
name, and quoting it would make it a String the server rejects. That is why an enum is
carried as `GqlEnumValue` rather than as a string from the moment it is lowered: the
converter writes the same JSON as before, and the inline printer can still tell them
apart. Sort direction alone makes this load-bearing — every `OrderBy` emits one.

**Printing is canonical** — deterministic field order, deterministic variable numbering,
normalized whitespace. Two structurally identical queries print byte-identical text, so
a hash of the printed text is stable across processes and APQ works with no extra
machinery — computed by whoever wants it, over an ordinary string. Canonical printing is
also what makes golden-file tests meaningful, so it is a v1 requirement rather than an
optimization.

### 6.3 Getting a queryable

A queryable becomes executable by being handed a transport, and over HTTP that is one
call on a client the app already owns:

```csharp
using var client = new HttpClient { BaseAddress = new Uri("https://…/graphql") };
IQueryable<Person> people = client.CreateQueryable<Person>("people");
```

**There is no registration API.** An `AddGraphQLQueryable` would have to own a naming
scheme for clients, a keying scheme for multiple endpoints, and a lifetime policy — three
decisions the app has already made for every other client it has, and none of which this
library is better placed to make. Handing back a queryable from a client leaves all three
where they were: two endpoints are two clients, and however the app already distinguishes
two of anything is how it distinguishes these.

That also drops `Microsoft.Extensions.Http` from the dependency graph. The transport
package references nothing but the two assemblies it joins.

**There is no source object either.** One briefly existed — an `IGraphQLQueryableSource`
holding an executor and a set of default options, so several query types could share one
endpoint. It never earned its place: outside its own definition it appeared almost only in
tests, and the thing it was sharing is an executor cheap enough to rebuild — a client
reference and a URI. Several queries against one endpoint are now several calls on the
same client, and nothing sits between the two.

What the source did carry that nothing else could is the seam for a transport other than
HTTP. That moved onto the entry point itself:

```csharp
IQueryable<Person> people = GraphQLQueryable.For<Person>(new MyExecutor(), "people");
```

**Everything the schema requires is options, not attributes.** The root field, the filter
and sort input names, the paging kind, the filter dialect and the endpoint path are all
properties of the server rather than of the CLR type, so they are stated where the query
is made:

```csharp
client.CreateQueryable<Person>("people", o =>
{
    o.EndpointPath = "api/v2/graphql";
    o.Paging = PagingKind.Cursor;
    o.FilterInput = "PersonWhereInput";
    o.FilterProvider = new MyDialect();
});
```

That keeps the POCO a POCO. One type can be an EF entity, a DTO and a GraphQL result
without carrying three sets of attributes, and two schemas that model the same thing
differently need one type rather than two.

The options object is mutable so a configure delegate reads the way the options pattern
does elsewhere, and `IOptions<GraphQLHttpQueryOptions>` overloads exist for apps that
configure it in a container. A container's instance is shared, so the overload that reads
one copies it and takes the root field from the call — a query never writes to the
options it was given.

Resolution of the endpoint path is `HttpClient`'s, not ours: a base address is a
document, not a directory, so `https://host/v1` plus `graphql` is `https://host/graphql`.
Reimplementing that rule to be friendlier would mean a client behaving differently here
than everywhere else in the app, which is the worse surprise.

There is no plan cache in v1. When one lands it must be registered **singleton**, not on
the scoped provider — a per-request cache is invisible in tests and correct in every
observable way except that it never actually caches.

## 7. Compile-time validation

The analyzer walks LINQ chains rooted at a `Queryable<T>()`/`For<T>()` call, and chains
terminating in a §4 extension from any root, replaying the shared rule table over the
syntax tree.

The §4 extensions weaken static analysis, unavoidably: a chain starting from a `DbSet` or
a method parameter usually cannot be followed to its source, so `FGQL006` fires and
validation falls to runtime. Predicates written inline at the call site still analyse
normally. This is a real reduction in the compile-error guarantee of decision #2, and the
price of the extensions working over sources this library does not own.

| ID | Severity | Meaning |
| --- | --- | --- |
| FGQL001 | Error | Unsupported LINQ operator |
| FGQL002 | Error | Unsupported expression in a predicate (arbitrary call, unbound member) |
| FGQL003 | Error | `Where` clause not lowerable to the filter input |
| FGQL004 | Error | Ordering key is not a member access |
| FGQL005 | Error | Projection references a `[JsonIgnore]` property |
| FGQL006 | Warning | Chain not statically analyzable — validated at runtime instead |
| FGQL007 | — | Retired. Nothing is required on the queried type |
| FGQL008 | Error | `Skip` under `PagingKind.Cursor` |
| FGQL009 | Error | `Count()` on a root field with `PagingKind.None` |
| FGQL010 | — | Retired. Expansion skips nested fields, so a cycle cannot arise |
| FGQL011 | Error | No root field was named for the query |
| FGQL012 | Error | Unbounded query — no `Where`, `Take` or `Select` |
| FGQL013 | Error | Computation in a projection whose required fields cannot be read from it |
| FGQL014 | Error | A type has no scalar fields, so there is nothing to select from it |
| FGQL015 | Error | `Last()` without `PagingKind.Cursor` — nothing to read backwards from |
| FGQL016 | Error | Terminal on a queryable with no endpoint (`GraphQLQueryable.For<T>()`) |
| FGQL017 | Error | Response carried neither data nor errors |
| FGQL018 | Error | Response shape contradicts the declared `PagingKind` |
| FGQL019 | Error | Async terminal used over a provider that is not this one |
| FGQL020 | Error | One chain filters over both the queried type and a filter shape |

FGQL006 is the honest one. When the analyzer loses the chain, it says so rather than
pretending, and the runtime raises `GraphQLTranslationException` carrying the same
diagnostic ID and message. Same rule table, same text, two enforcement points.

**What ships today.** `ProjectionAnalyzer` enforces the one projection rule that needs no
chain-walking at all — `FGQL014` — because it is decided from a single `Select` and the
shape of one type. It is a `DiagnosticAnalyzer` rather than a
generator: reporting at a call site is what analyzers are for, and it puts the squiggle
under the member as you type rather than at the next build. The generator that emits
per-type metadata is a separate concern and stays a generator.

The analyzer is deliberately conservative. It reports only over `IQueryable<T>` whose `T`
visibly starts at one of this library's entry points — somebody else's `Select` is none
of its business — and
says nothing when it cannot follow the projection. A false error costs more than a late
one, and the runtime is still there.

The rule is duplicated, once over `System.Type` and once over `ISymbol`, because the
translator cannot run at compile time and the compiler has no `Type`. That duplication is
the price of early diagnostics; keeping each copy in one file, under the same names
(`IsScalar`, `Fields`, `Unwrap`), is what keeps them honest.

Generator output is per-type metadata plus module-initializer registration — field table,
root field name, paging kind, filter/sort input type names. That keeps `System.Reflection`
out of translation. Materialization still reflects, through
`DefaultJsonTypeInfoResolver`; a generated `JsonSerializerContext` would close that gap.

Standard incremental-generator hygiene applies: no `ISymbol` in the pipeline model,
value-equatable record models, `ForAttributeWithMetadataName` as the entry predicate.

## 8. Proving it

v1's entire surface is `Expression → document + variables`, so the test suite needs no
server and no JSON.

**Filter lowering (§4) tests need no part of this library at all** — no provider, no
attributes, no queryable of ours:

```csharp
[Test]
public void And_merges_into_one_object() =>
    Assert.That(
        Array.Empty<Person>().AsQueryable()
             .Where(p => p.Name == "John" && p.Age > 30)
             .ToGraphQLFilter()!.ToJsonString(),
        Is.EqualTo("""{"name":{"eq":"John"},"age":{"gt":30}}"""));
```

That is the bulk of the suite, and it is where the risk actually lives: one case per row
of the §4.3 table, plus nesting, negation, collection quantifiers, and `FGQL002` cases
asserting that untranslatable predicates throw rather than emit something plausible.

Query translation (§5) adds:

- **Golden-file tests** on query text, one per selection set.
- **Determinism test**: the same logical query built by different chains prints
  identically and hashes identically.
- **Reflection-parity test**: an attributed type and an identical un-attributed type must
  lower to the same filter, proving the generated-metadata and reflection paths in §4.2
  agree.
- **Analyzer tests** via `Microsoft.CodeAnalysis.Testing`, one per diagnostic ID, plus
  negative cases proving valid queries stay clean.
- **Parity test**: every analyzer-rejected sample must make the runtime translator throw
  with the same ID. This is what keeps the two enforcement points honest.

The LINQ test project runs on **NUnitLite** (`dotnet run`) rather than `dotnet test`.
`NUnit3TestAdapter` 6 registers no Microsoft.Testing.Platform framework, and the .NET 10
SDK no longer runs the VSTest target, so neither `dotnet test` path discovers anything —
a condition the repo's existing test project shares.

The only test needing a network is a single smoke test against a HotChocolate server
confirming a generated request is actually accepted — which is the real question v1
exists to answer, and the one that settles both `TODO` markers.
