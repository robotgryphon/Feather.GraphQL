# Eliminating the variable contract

**Status:** complete — all five phases landed 2026-09-12/13.
**Decisions taken:** runtime templates compile to a constant body prefix plus typed variable writes
(the document text is never rewritten); the compiled and runtime paths get separate transport entry
points rather than a shared variables abstraction.

**Superseded:** `GraphQLTemplate` — the runtime template of §2.2 and Phase 3 — was deleted in
[one-compiled-linq-path](one-compiled-linq-path.md). It shipped with unit tests and four
diagnostics pointing at it, and with no consumer, no end-to-end test and no benchmark row, so the
claim that it matched the compiled path was never measured. What survives from this plan is the
machinery underneath it: `PooledBody`, the constant-prefix emission, and the deleted variable
contract.

---

## 1. What exists today

`IGraphQLVariables` is a two-member interface — `IsEmpty`, `WriteTo(Utf8JsonWriter)` — with six
implementations across three tiers:

| Tier | Implementations | Where |
| --- | --- | --- |
| Generated | `Variables{N}` × 3 generators | `CompiledQueryGenerator:1278`, `QueryInterceptorGenerator:452`, `GraphQLQueryMethodGenerator:636` |
| Runtime | `Empty`, `PageOnly`, `Lowered_` | `Feather.GraphQL.Linq/Execution/GraphQLVariables.cs` |
| Shared | `GraphQLNoVariables` | `Feather.GraphQL.Abstractions` |

`GraphQLNoVariables` and the runtime `Empty` are the same class written twice.

It is consumed in exactly one place — `GraphQLRequestWriter.ToUtf8`:

```csharp
writer.WriteStartObject();
writer.WriteString("query"u8, request.Query);          // escapes the document, every request
if (request.Variables is { IsEmpty: false } variables)
{
    writer.WritePropertyName("variables"u8);
    variables.WriteTo(writer);
}
writer.WriteEndObject();
return buffer.WrittenSpan.ToArray();                   // copies the body, every request
```

### The cost the interface hides

The interface is not itself expensive — it is one virtual call per request. The expensive part is
what its shape *forces*: because the body is assembled from a `string` query plus a callback, the
document must be JSON-escaped on every request and the finished bytes copied out of the pooled
buffer. For a compiled query the document is a compile-time constant, so both are avoidable.

That is the real prize here, and it is larger than deleting the interface.

---

## 2. Target shape

### 2.1 Compiled queries — the body is (almost) a constant

For a query whose document is known at build time, everything up to the first variable value is a
constant byte sequence. The generator emits it as a UTF-8 literal:

```csharp
// {"query":"query($v0:CountryFilterInput){countries(filter:$v0){name}}","variables":{"v0":{"continent":{"eq":
private static ReadOnlySpan<byte> Prefix0 =>
    "{\"query\":\"query($v0:CountryFilterInput){countries(filter:$v0){name}}\",\"variables\":{\"v0\":{\"continent\":{\"eq\":"u8;

private static ReadOnlySpan<byte> Suffix0 => "}}}}"u8;
```

and the body writer becomes a copy, a value, and a copy:

```csharp
private static PooledBody Body0(string continent)
{
    var body = PooledBody.Rent(Prefix0.Length + Suffix0.Length + 64);

    body.Write(Prefix0);
    body.WriteJsonString(continent);   // the only runtime work
    body.Write(Suffix0);

    return body;
}
```

No `Utf8JsonWriter`, no document escaping, no interface, no `Variables{N}` type. For a query with
no variables the body is a single `ReadOnlySpan<byte>` constant with nothing written at all.

Multi-hole queries interleave: `Segment0`, value, `Segment1`, value, … `SegmentN`. The generator
already knows each hole's type and order — that is what `Compiled.Payload` and `Compiled.Holes`
hold today.

### 2.2 Runtime templates — the same machinery, parsed instead of emitted

The user-facing half. A template is parsed once and reused:

```csharp
private static readonly GraphQLTemplate _countries =
    GraphQLTemplate.Parse("query($code:String!){ countries(filter:{continent:{eq:$code}}) { name } }");

using var body = _countries.Bind(("code", code));
using var response = await client.PostGraphQLAsync(body, cancellationToken);
```

`Parse` does the same work the generator does, at startup instead of at build time: escape the
document once, split into segments around the variable holes, keep the segments as `byte[]`. `Bind`
copies segments and writes values — the identical per-request path as §2.1.

**The document is never rewritten.** Values go into the `variables` object exactly as they do now,
so server-side query-plan caching and APQ keep working, no GraphQL literal escaping is needed, and
no schema knowledge is required to tell an enum from a string.

### 2.3 What replaces the interface at the transport

Two entry points, no shared abstraction:

```csharp
// Compiled queries and runtime templates: the body is already bytes.
ValueTask<HttpResponseMessage> PostGraphQLAsync(PooledBody body, CancellationToken ct);

// The runtime LINQ translator: it holds definitions, not bytes.
internal ValueTask<HttpResponseMessage> SendTranslatedAsync(
    string query, IReadOnlyList<GqlVariableDefinition> variables, Uri? endpoint, CancellationToken ct);
```

The translator keeps writing its `JsonNode` values as it does now, inside `SendTranslatedAsync`,
with the loop from `Lowered_.WriteTo` moved there verbatim. It is the slow path already; it does not
need to be fast, it needs to stop defining a contract the fast path has to satisfy.

---

## 3. Work plan

Ordered so the tree builds and tests pass at every step.

### Phase 1 — pooled body type (no behaviour change)

1. Add `PooledBody` (Serialization or Http): a rented `byte[]` with `Write(ReadOnlySpan<byte>)`,
   `WriteJsonString(string)`, `WriteJsonNumber(...)` typed writers, `ReadOnlyMemory<byte> Written`,
   and `Dispose` returning the buffer.
   - `WriteJsonString` needs correct JSON escaping — reuse `JsonEncodedText` or
     `Utf8JsonWriter` over the single value rather than hand-rolling it.
2. Add `HttpContent` support so a `PooledBody` posts without `ToArray()`, and the buffer is
   returned when the request completes.
3. Unit tests: escaping (quotes, backslash, control chars, non-ASCII), growth, double-dispose.

**Checkpoint:** nothing uses it yet; everything still passes.

### Phase 2 — compiled path emits bodies

4. `CompiledQueryGenerator`: replace `Payload()` with `Body()` — emit `Prefix`/`Segment` literals
   and a static body-writing method. Delete the `Variables{N}` class emission.
5. Same for `GraphQLQueryMethodGenerator` (declared `[GraphQLQuery]` methods) and
   `QueryInterceptorGenerator`.
6. Point all three at `PostGraphQLAsync(PooledBody, …)`.
7. Update analyzer tests: `GraphQLNoVariables.Instance` and `GraphQLVariableWriter.Write(writer, _0)`
   assertions become assertions about emitted `u8` literals and body writes.

**Checkpoint:** the compiled path no longer touches `IGraphQLVariables`. Runtime path still does.

### Phase 3 — runtime templates

8. `GraphQLTemplate.Parse` — scan the document for `$name` outside strings and comments, split into
   segments, record hole names in order. Decline (throw at parse time, not request time) on a
   variable declared but never used, or used but never declared.
9. `Bind` overloads: typed for the common arities, plus a params form.
10. Tests: round-trip against the same bytes the compiled path produces for an equivalent query.

**Checkpoint:** a non-LINQ user has the fast path. This is the phase that delivers the stated goal.

### Phase 4 — runtime LINQ path

11. Move `Lowered_.WriteTo`'s loop into `SendTranslatedAsync`.
12. Change `GraphQLOperation.Variables` from `IGraphQLVariables` to
    `IReadOnlyList<GqlVariableDefinition>`. **This is a public API break** — `GraphQLOperation` is
    `[PublicAPI]`.
13. Update `HttpGraphQLQueryExecutor:198` and `GraphQLQueryPlan`/`GraphQLPrecompiled` signatures.

### Phase 5 — deletion

14. Delete `IGraphQLVariables`, `GraphQLNoVariables`, `GraphQLVariables.cs`, and
    `GraphQLVariableWriter`.
15. Delete `GraphQLRequest.Variables`; `GraphQLRequest`/`GraphQLRequestWriter` likely collapse
    entirely, since their remaining job is the constant envelope the generators now emit.
16. `Feather.GraphQL.Abstractions` loses four of its six files — worth asking whether the package
    still earns its existence.

---

## 4. What this costs

**A public API break.** `IGraphQLVariables`, `GraphQLNoVariables` and `GraphQLVariableWriter` are all
`[PublicAPI]`, and `GraphQLOperation.Variables` changes type. Anyone implementing the interface —
the `ClientComparison` benchmark does, at `ClientComparison.cs:101` — has to change. Pre-1.0 this is
cheap; it will not stay cheap.

**Generated code grows.** A `u8` document literal per query replaces a shared writer. For a large
document across many queries this is more IL than today, traded for less work per request. Worth
measuring on the trimming benchmark, not assuming.

**Two body-writing paths instead of one.** The compiled path and the translator will each assemble a
body. That is the deliberate cost of removing the shared contract, and it is the thing to revisit if
they start drifting.

**`WriteJsonString` becomes load-bearing.** It replaces `Utf8JsonWriter`'s escaping on the hot path.
Getting it wrong is a correctness and injection bug, not a perf regression — so it gets property
tests against `Utf8JsonWriter`'s own output rather than hand-picked cases.

---

## 5. What to measure

Against today's numbers in `EndOfDayReport-2026-09-12.md`:

| Question | Benchmark |
| --- | --- |
| Does removing per-request document escaping show up? | `ClientComparison`, filtered rows especially — they carry the largest documents |
| Is the runtime template close to the compiled path? | New row: template vs `AOT SourceGen` vs `Static` |
| Does the `u8` literal per query cost size? | Trimming/size benchmark |
| Does `PooledBody` beat `WrittenSpan.ToArray()`? | Allocation column; expect one fewer copy per request |

Expected direction: **allocation down** (no body copy, no `Variables{N}` instance), **time down on
filtered rows** (no document escaping). Per §2 of the report, treat any timing delta under ~8% on
generated rows as noise and judge on `Allocated`.

---

## 6. What actually happened

All five phases landed. Differences from the plan as written, and why:

- **`PooledBody` lives in `Feather.GraphQL.Serialization`, not `Http`.** It had to: the
  precompiled plan's generated filter writes a body too, and that generator ships with
  `Feather.GraphQL.Linq`, which does not reference `Http`. Leaving it in `Http` would have emitted
  code a LINQ-only consumer could not compile.
- **Phase 2 converted `GraphQLQueryMethodGenerator` first**, not `CompiledQueryGenerator`. Its
  variables are a flat named list and it had the strongest body assertions, so it proved the
  approach before the filter, ordering and paging machinery.
- **`FilterSkeleton` rendered its shape two ways for one phase.** The precompiled filter reaches
  the wire through the runtime executor, so it could not write its own body until Phase 4 moved
  that path to bytes. Both renderings came from one walk of the clauses so they could not
  disagree; the second was deleted in Phase 5.
- **`GraphQLRequest` and `GraphQLRequestWriter` are gone entirely**, as §3 guessed they might be.
  Nothing used `OperationName` or `Extensions`. The runtime path calls `SendTranslatedAsync`,
  which writes its own body; the compiled path calls `PostGraphQLBodyAsync` with bytes.
- **`Feather.GraphQL.Abstractions` is down to two files** — `GraphQLException` and
  `GraphQLQueryAttribute`. Whether it is still a package is an open question below.

The comma risk §4 did not mention turned out to be the real hazard: `Utf8JsonWriter` inserts
separators and raw bytes do not. It was caught by tests that already existed — a two-clause filter,
a two-key ordering, and `The_compiled_request_is_what_composing_the_chain_would_have_sent`, which
asserts the compiled body is byte-identical to the translated one.

## 7. Open questions

1. **Does `Bind` need to be allocation-free for the no-variable case?** A query with no holes is a
   constant `ReadOnlySpan<byte>` — it should post without renting anything.
2. **Should `GraphQLTemplate` validate against a schema?** No, for now — but it changes whether
   `Parse` can reject a misspelled variable name.
3. **What happens to `GraphQLRequest.Extensions`?** Nothing uses it today except its own writer. It
   may be deletable in Phase 5; check before assuming.
4. **Is `Feather.GraphQL.Abstractions` still a package after this?** It now holds exactly
   `GraphQLException` and `GraphQLQueryAttribute`. Two files, one of which is an attribute the
   analyzer looks for by metadata name — so it could move anywhere the consumer already
   references. Unresolved.
5. **Should the runtime path stop rendering variables into an intermediate array?**
   `SendTranslatedAsync` copies them into the body it builds, so they are written twice. Fixing it
   means the translator writing into a `PooledBody` directly, which means `Feather.GraphQL.Linq`
   referencing `Serialization` for more than types — measurable first, and this is the slow path.
