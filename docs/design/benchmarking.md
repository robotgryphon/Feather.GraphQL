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
| `ClientComparison` | Every surface Feather has, plus GraphQL.Client, same handler and contracts | What each way of writing the same query costs |
| `QueryTranslation` | LINQ chain → document, no I/O at all | What the LINQ surface costs |
| `QueryPipeline` | Translate + execute + materialize, no HTTP | Where library time actually goes |
| `ShapingStrategies` | One compiled projection's loop, five ways | Whether the generated loop is the right loop |

`TransportFloor` carries two rows on purpose. `HTTP round trip only` is the transport floor;
`Deserialize only` is the parsing floor — work no client can avoid. A client's total minus those
two is its own overhead, and without them a client that added nothing measurable would still look
expensive.

### The five surfaces in `ClientComparison`

The same question is asked five ways, each in a plain and a filtered form, because what separates
them only appears once a value has to reach the server:

| Row | How the query is written | What is built at run time |
| --- | --- | --- |
| `static` | A document written by hand and posted | Nothing but the request |
| `declared` | `[GraphQLQuery]` — hand-written document, compiler-written method and payload | Nothing but the request |
| `LINQ` | A chain composed and translated on every call | An expression tree, walked to recover the values and print the document |
| `LINQ precompiled` | The same chain inline, so the compiler prints its document — and its whole plan when the chain binds nothing else | The tree, still walked for the values |
| `LINQ compiled` | `[GraphQLQuery]` — the chain isolated to a method whose calls are replaced outright | Nothing but the request |

Read down the filtered column: `static` and `declared` are the floor, `LINQ` is what composing
costs, `LINQ precompiled` is that minus the document, and `LINQ compiled` should be back at the
floor. The last is also the AOT-safe row — no `Expression.Compile`, no `MakeGenericType`, nothing
reflected over — which is what makes the floor reachable without giving up LINQ.

Two things keep those rows honest, and both are arrangements rather than assertions:

- The `LINQ` rows take their entry point from `Composed`, a `[MethodImpl(NoInlining)]` helper. A
  queryable handed out of a method has no visible terminal, so the generator declines it — the
  same device `QueryTranslation` uses, and the only way to measure runtime translation now that
  the generator exists. Written inline they would silently become the `precompiled` rows.
- The documents are not all identical, and the table should be read knowing it: the hand-written
  ones name the continent's fields, and the ones a chain prints select the element's own scalars.
  The canned reply is the same for every row, so the read side being compared is unchanged.

### What `ShapingStrategies` settled

A compiled projection shapes its rows with an indexed `for`, and the obvious question is whether
it should use spans, raw references, or threads instead. Measured, on an M5 Pro:

| Against the indexed loop | 100 rows | 1 000 rows | 10 000 rows |
| --- | --- | --- | --- |
| Spans | 0.96× | 1.03× | 1.05× |
| `ref` + `Unsafe.Add` | 0.96× | 1.01× | 1.04× |
| `Parallel.For` | 9.98× | 3.75× | 1.29× |
| LINQ `Select` | 1.16× | 1.22× | 1.22× |

Spans and references are a rounding error at 100 rows and *lose* at every larger size — the JIT
already drops the bounds checks the indexed form would pay, and `nint` arithmetic buys nothing
back. `Parallel.For` never wins: the per-row work is one small allocation, so threads only add
scheduling and allocator contention, and even at ten thousand rows it is still behind.

The reason is in the last two rows of the benchmark. A projection that allocates nothing runs at
0.21× the one that constructs an object per row — so roughly four fifths of the loop is the
allocation, which no loop shape removes. And the loop is a small part of a request anyway: at
1 000 rows it is about 4µs against a ~200µs round trip, so doubling its speed would buy one
percent of the request.

Two of the alternatives are not implementable regardless. The `unsafe` keyword cannot appear in
generated code, because it lands in the *consumer's* compilation and a generator cannot set
`AllowUnsafeBlocks` for them; and `Span<T>` cannot cross the `await` the shaping sits after.
`Parallel.For` would also change what a projection means — the lambda is the author's own, nothing
promises it is thread-safe, and parallelising it would wrap any exception it throws in an
`AggregateException`.

So the loop stays as it is. The class stays too, so that the next person to suggest this finds
the answer rather than the question.

`QueryPipeline` is the tier to profile in. Its executor answers from a fixed byte array, so a
profile taken there is Feather's own code — walking the chain, printing the document, reading
the reply, materializing rows — rather than `HttpClient`.

It does include deserializing the reply, and cannot exclude it: since the transport seam takes
the contract to read the reply through rather than returning a parsed element, reading *is* how
the rows come to exist. What the tier still excludes is the transport — no socket, no request
serialization, no buffering of a response that is already an array. Numbers from before that
change are not comparable with numbers after it: the earlier executor handed back an
already-parsed element, so the parse sat outside the measurement rather than inside it.

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
- **Two sizes, and which two matters.** `ClientComparison` runs at 1 and 25 rows. A client's own
  cost dominates a one-row reply and is swallowed by a large one, so a benchmark at a single size
  cannot tell a fixed cost from a per-row one — but past a couple of dozen rows the table stops
  saying anything new, because every client is by then mostly parsing the same JSON. The larger
  sizes moved to the serialization benchmarks, where reading is the thing being measured and
  scale is the point.

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
- `ClientComparison` runs the whole range, and the paragraph above says how each row is pinned to
  the surface it is named for.
- `QueryPipeline` runs both: `Composed at runtime` behind that same helper, `Precompiled
  document` written inline at its terminal. The two chains are otherwise identical, so the gap
  between them is what precompilation is worth.
- It runs a second pair for the same reason at one level deeper. Those two chains bind nothing,
  so the inline one gets its whole *plan* at build time and not just its document — root field,
  paging, terminal and projection included. The gap between `Projected: composed at runtime` and
  `Projected: precompiled plan` is what that is worth, and it is much the larger of the two
  because the chain is never walked at all.

To check which chains were precompiled in a given build:

```
dotnet build benchmarks/Feather.GraphQL.Benchmarks -c Release --no-incremental \
  -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=/tmp/gen
grep -rn "query(" /tmp/gen
```

The folder a document appears in says which surface produced it: `QueryInterceptorGenerator` for
a precompiled chain, `CompiledQueryGenerator` for a `[GraphQLQuery]` one, and
`GraphQLQueryMethodGenerator` for a declared one. A chain meant to be composed at run time
appearing in any of them is a benchmark measuring something other than its name.

Write that output anywhere but the project directory — files emitted under it are compiled as
source on the next build and collide with themselves.

## Adding a benchmark

Keep each class to one question, give it a baseline so ratios mean something, and state in its
remarks what the number does *not* include. A benchmark whose floor is not written down will be
read as if it had none.
