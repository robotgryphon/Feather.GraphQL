# Benchmarking

`benchmarks/Feather.GraphQL.Benchmarks` is a BenchmarkDotNet console app. Run it from the repo
root:

```
dotnet run --project benchmarks/Feather.GraphQL.Benchmarks -c Release -- --filter '*'
```

Add `--job short` for a quick read, `--job dry` only to check that a benchmark runs at all — dry
numbers are one cold-start iteration and mean nothing. A Debug build is refused, correctly.

## The problem this setup exists to solve

A GraphQL client's own work is small. A real endpoint costs hundreds of microseconds and varies
by more than the thing being measured, and even a loopback server drags in Kestrel, routing and
the server's own execution engine. Measured that way, every client looks identical and every
optimisation looks like noise.

So the network is removed entirely. `CannedTransport` is an `HttpMessageHandler` that answers
from a fixed byte array: no socket, no DNS, no TLS, no server. It never reads the request body —
the test double does, and that alone costs more than some of these benchmarks. The response is a
cached array wrapped in a fresh `ByteArrayContent` per call, because content can only be consumed
once; that allocation is identical for every client measured, and the floor benchmark reports it.

## The tiers

Each class answers a different question, and each number is only meaningful against the one below
it.

| Class | What is in it | What it answers |
| --- | --- | --- |
| `TransportFloor` | `HttpClient` + a canned reply, nothing interpreting it | What a request costs before any GraphQL client exists |
| `ClientComparison` | Feather vs GraphQL.Client, same handler, same contracts | Whether the HTTP layer is competitive |
| `QueryTranslation` | LINQ chain → document, no I/O at all | What the LINQ surface costs |
| `QueryPipeline` | Translate + execute + materialize, no HTTP | Where library time actually goes |

`TransportFloor` carries two rows on purpose. `HTTP round trip only` is the transport floor;
`Deserialize only` is the parsing floor — work no client can avoid. A client's total minus those
two is its own overhead, and without them a client that added nothing measurable would still look
expensive.

`QueryPipeline` is the tier to profile in. Its executor hands back an already-parsed `data`
element, so a profile taken there is Feather's own code — walking the chain, printing the
document, materializing rows — rather than `HttpClient`.

## Holding everything else equal

The comparison is only honest if the parts outside the libraries are the same on both sides:

- **One handler.** Both clients get a `CannedTransport` over the same payload.
- **The same serializer contracts.** `BenchmarkSerializerContext` is source-generated and given
  to both — Feather through its registry, GraphQL.Client through the `JsonSerializerOptions` its
  serializer is constructed with. GraphQL.Client's own envelope types (`GraphQLRequest`,
  `GraphQLResponse<T>`) are declared in that context too; leaving them to the reflection fallback
  would tax it for something Feather does not pay. Letting one side use reflection and the other
  source generation would measure System.Text.Json configuration, not either library.
- **The same rows.** `Payloads` builds its bodies with Bogus under a fixed seed, then serializes
  them *through the model*, so the canned reply is exactly what a server sending this shape would
  produce and no hand-escaped string can drift from the type being deserialized into. Real names
  vary in length and carry non-ASCII; a payload of `"Country 1"` would quietly measure the
  short-string fast paths instead.
- **Three sizes.** 1, 100 and 1000 rows. A client's fixed cost dominates a one-row reply and
  disappears into a thousand-row one, and a benchmark at a single size cannot tell the two apart.

## What the comparison deliberately excludes

GraphQL.Client takes a query string. It has no LINQ translation, no projection, no filter
lowering — so `ClientComparison` compares only the part that is actually comparable: post a
document, read typed data out of the reply. Feather's translation is measured on its own in
`QueryTranslation` so that what it costs is visible, rather than smuggled into a number labelled
as a like-for-like comparison.

## Precompilation, and how the benchmarks control for it

The analyzer intercepts recognised chains and supplies the document at build time, so a
benchmark written the obvious way would measure the precompiled path whether or not that was the
intent. Both classes that care about this control for it explicitly:

- `QueryTranslation` routes every chain through a `[MethodImpl(NoInlining)]` helper. A queryable
  handed out of a helper has no visible terminal, so the generator declines it and the runtime
  translator does the work — which is the thing that class measures. (`ToGraphQLQuery` prints the
  inline form, which is never precompiled, so it is unaffected either way.)
- `QueryPipeline` runs both: `Composed at runtime` behind that same helper, `Precompiled
  document` written inline at its terminal. The two chains are otherwise identical, so the gap
  between them is what precompilation is worth.

To check which chains were precompiled in a given build:

```
dotnet build benchmarks/Feather.GraphQL.Benchmarks -c Release --no-incremental \
  -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=/tmp/gen
grep -rn "query(" /tmp/gen
```

Write that output anywhere but the project directory — files emitted under it are compiled as
source on the next build and collide with themselves.

## Adding a benchmark

Keep each class to one question, give it a baseline so ratios mean something, and state in its
remarks what the number does *not* include. A benchmark whose floor is not written down will be
read as if it had none.
