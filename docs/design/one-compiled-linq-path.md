# One compiled LINQ path

**Status:** plan, not yet started
**Decisions taken:** a chain must be the body of a `[GraphQLQuery]` method (inline chains at call
sites may be added later, without changing what is legal now); `GraphQLQueryable<T>` keeps
implementing `IQueryable<T>`.

---

## 1. The end state

Two surfaces, and nothing between them:

| Surface | What it is | What it costs at run time |
| --- | --- | --- |
| `Feather.GraphQL.Http` | Strings. A document you wrote, sent and read as-is. | Post the bytes, read a reply. |
| `Feather.GraphQL.Linq` | A chain in a `[GraphQLQuery]` method, compiled in full. | The same, with the document and the body printed at build time. |

There is no third thing. A chain the compiler cannot translate is a **compile error**, and the
answer is to stop using a chain for that query — write it out, or generate it some other way.

That is deliberately not a graceful degradation. A query either compiles or it is not this
library's to send.

## 2. What exists now

Three LINQ paths, two of which are going:

| Path | How it works | Fate |
| --- | --- | --- |
| `[GraphQLQuery]` method | Call sites intercepted; document and body printed at build time. | **Keeps.** Becomes the only one. |
| Precompiled plan | `QueryInterceptorGenerator` prints the document, but filter values are read out of an expression tree on each call. | Delete. |
| Runtime translation | `Expression` → `QueryChain` → `FilterTranslator` → printed document, per call. | Delete. |

Roughly 2,500 lines sit behind the latter two, and they hold the last real AOT blockers:

```csharp
// GraphQLQueryProvider.cs:29 and :202
Activator.CreateInstance(typeof(GraphQLQueryable<>).MakeGenericType(elementType), this, expression)
```

### The one change that carries the rest

`FGQL015` ("this query was not compiled") is a **Warning** today, and its own remarks explain why:
*"The method still works as written; it just composes its chain on every call."* That sentence stops
being true. Turning it into an **Error** is what makes every other deletion safe, and it is the
change users will feel — where the compilable subset ends becomes a wall rather than a slowdown.

Everything else in this plan is bookkeeping around that one line.

---

## 3. Known gaps to close or declare

These are cases the runtime path covers today and the compiled path does not. Each needs a decision
before FGQL015 becomes an error, because each is currently a silent fallback and would become a
hard stop.

**1. Streaming — resolved: dropped.** `AsAsyncEnumerable`, `IGraphQLQueryExecutor.StreamAsync`,
`GraphQLReplyReader.StreamRows` and `GraphQLReplyStream` are removed, along with the six tests that
covered them and the terminal the analyzer recognised. There was no compiled implementation and no
consumer outside the executor's own tests.

What is lost is real and should be recorded: bounded memory over a large result set, and stopping
the work when a caller stops reading. Both remain available to anyone reading a response
themselves. Re-adding it means a generated iterator that carries a byte offset and rebuilds its
reader per row — a `Utf8JsonReader` cannot cross a `yield` — and pinning the pooled reply buffer
for as long as the enumeration lives.

**2. A `[GraphQLQuery]` method that is not called directly is not intercepted.** Interceptors
replace call sites. A method group (`Enumerable.Select(ids, ByIdAsync)`), a delegate, or a
reflective call reaches the real body — which, after this, is a body whose provider throws. This
needs its own diagnostic: referencing a `[GraphQLQuery]` method other than by direct invocation
should be an error, not a runtime surprise.

**3. Whatever else the corpus declines.** Before flipping the severity, run the analyzer over the
example project, the tests and the benchmarks with FGQL015 escalated, and count. Every hit is
either a gap to close or a case to document as string-only.

---

## 4. Work plan

### Phase 1 — find out what breaks

1. Escalate `FGQL015` to Error behind a build flag, build everything, and record every hit.
2. Add the missing diagnostics as **warnings** first, so their volume can be measured:
   - `CreateQueryable` outside a `[GraphQLQuery]` method body.
   - A `[GraphQLQuery]` method referenced other than by direct invocation.
3. Decide the streaming question from §3.1 with the count in hand.

**Checkpoint:** a list of every chain in the repo that would stop compiling, and why.

### Phase 2 — close the gaps worth closing

4. Widen `CompiledQueryGenerator` to cover whatever Phase 1 showed is both common and tractable.
5. Implement the `IAsyncEnumerable<T>` terminal, or record the decision not to.

### Phase 3 — make it the only path — **done**

6. `FGQL015` and `FGQL018` → Error. Both measured zero in Phase 1, and the solution still builds,
   which is the measurement confirmed.
   - **`FGQL017` stays a Warning until Phase 4.** `NoWarn` suppresses warnings, not errors, so
     escalating it would break the four projects that deliberately exercise the runtime path —
     and those do not go away until Phase 4. It is flipped there, once nothing trips it.
7. Deleted `QueryInterceptorGenerator`, `ProjectionShaper`, `GraphQLPrecompiled`, `FilterHoles`,
   `ProjectionKey`, `GraphQLProjectionRegistry`, the shaper plumbing in `TypeMetadataGenerator`,
   and the precompiled hooks in `GraphQLQueryProvider.Plan` and `ResultMaterializer.Shaper`.
8. **Replaced the interception oracle.** Twenty-six assertions proved a compiled call had been
   replaced by checking `GraphQLPrecompiled.Attachments` had not moved. Deleting the precompiled
   path would have left those assertions passing vacuously — true whether or not interception
   still worked. They now read `GraphQLQueryTranslator.Translations`, a count of chains translated
   at run time, which is the stronger question: the old counter only saw chains the generator had
   recognised, and this one sees every chain that ran. It is replaced again in Phase 4 by the
   provider throwing, at which point a counter is not needed.

### Phase 4 — delete the runtime executor — **done**

8. Delete the translation half: `GraphQLQueryTranslator`, `FilterTranslator`, `PartialEvaluator`,
   `SelectionSetBuilder`, `GraphQLDocumentPrinter`, `QueryChain`, `ResultMaterializer`, the
   `GqlValue`/`GqlField` document model, `GraphQLVariables`, `ReflectionTypeMetadata`.
9. Delete the execution half: `IGraphQLQueryExecutor`, `HttpGraphQLQueryExecutor`,
   `GraphQLReplyReader`, `GraphQLReplyStream` (subject to §3.1).
10. Reduce `GraphQLQueryProvider` to throwing stubs. `IQueryable<T>` stays, so the members stay;
    what goes is every implementation behind them — including the two `MakeGenericType` calls.
    The message matters: *"this chain was not compiled — a `[GraphQLQuery]` method is replaced at
    its call sites, so reaching this means the call was not intercepted."*

**What it came to.** `Feather.GraphQL.Linq` is now fifteen files and holds no engine: the
queryable, a provider of throwing stubs, the chain operators, the filter dialect, options and
enums. Generated compiled code references nothing from it at run time — the package exists so a
chain has something to compile against.

Two things went differently from the plan:

- **`TypeMetadataGenerator` did two jobs.** It built field tables for the runtime translator, and
  it registered the consumer's `JsonSerializerContext`s. Only the first was dead. Deleting the
  whole generator silently broke automatic context registration for the string-reading path, so
  the second half came back as `JsonContextGenerator`.
- **`GraphQLQueryable.For` lost its executor.** A chain never runs one, and the compiler finds the
  client from the method the chain is the body of — so taking an executor was asking for something
  nothing would use. `CreateQueryable` keeps its `HttpClient` parameter for the same reason it
  always had it: the compiler reads it.

`FGQL017` became an error here rather than in Phase 3, once the projects that tripped it were
gone, and the four `NoWarn` suppressions added as scaffolding were removed with it.

**The oracle handed over as designed.** `GraphQLQueryTranslator.Translations` went with the
translator; a compiled test that returns rows now proves interception by the fact that it returned
at all, because the body it would otherwise have run throws.

**Coverage went with the code.** The `Feather.GraphQL.Linq.Tests` project — translation, filters,
execution, metadata — is deleted in full, along with `QueryCreationTests`, `HttpExecutorTests`,
the `Interpreted` benchmark project and `QueryPipeline`. Test count is 203 against 409 before, and
the difference is all coverage of machinery that no longer exists. One loss worth naming:
`TypeFactsAgreementTests` checked that the analyzer's symbol-space view agreed with the runtime's
reflection-space view. There is one implementation now, so there is nothing to agree with — but
that also means nothing independently checks the analyzer's view of a type any more.

### Phase 5 — HTTP becomes string-only — **done**

11. Deleted `SendTranslatedAsync`, which existed only for the runtime executor.
12. `Feather.GraphQL.Http` is three files: constants, its exception, and `HttpExtensions`.
    Nothing in it knows what a chain is.

    **`GraphQLTemplate` was deleted with it.** It was built to give a non-LINQ caller the compiled
    path's performance from a parsed document, and it had 21 unit tests and four diagnostics
    recommending it — but no consumer, no end-to-end test that actually posted one, and no
    benchmark row, so the claim that it matched the compiled path was never measured. The
    diagnostics now point at `SendGraphQLQueryAsync`, which sends a document the caller wrote.

13. **`src/Shared/GraphQLRules.cs` is gone too.** It existed for one reason — the runtime
    translator and the generator both had to decide what counts as a scalar, and a rule that
    drifted between them would have silently changed selection sets. So the rule lived once and
    each side mapped its own type model onto a `TypeShape` struct to ask it. With one
    implementation left there is nothing to drift from, and the abstraction was costing a struct,
    a mapping function and a linked-source entry in two projects to answer a question the analyzer
    could ask of a symbol directly. The rules now sit in `GraphQLTypeFacts`, where they are read.

    Worth remembering if a second implementation ever returns: the reason for the indirection was
    real, and this is only safe because the second implementation is gone.

### Phase 6 — verify the AOT claim for the first time — **done**

13. `IsAotCompatible` is set on all five shipping projects. It turns on the trim and AOT analyzers,
    and **it found four real faults that had been there all along** — the claim was not true:

    | Site | Fault |
    | --- | --- |
    | `GraphQLJsonContextRegistry.Build` | appended a `DefaultJsonTypeInfoResolver`, so every read could fall back to reflection |
    | `GraphQLResponseReader.ErrorsIn` | `JsonSerializer.Deserialize<GraphQLError[]>` with no contract |
    | `ParsedReplyConverter` | `options.GetConverter(Type)` |
    | `ParsedReplyConverterFactory` | `MakeGenericType` |

    Fixed rather than annotated:

    - **The envelope is now read by hand.** `Read<TData>` walks `data` and `errors` itself and
      hands only the payload to the serializer. That is what makes a caller's ordinary context
      enough — they declare the type they asked for, never a `GraphQLReply<T>` wrapper of this
      library's. Resolving a contract for a constructed generic nobody declared was what the
      reflection fallback existed for.
    - **The reflection fallback is gone.** Its comment justified it as "the same fallback the
      field tables keep" — and the field tables went in Phase 4.
    - **The `ParsedReply` family was dead.** Reachable only through a module initializer
      registering a factory nothing asked for; the reader had moved to `GraphQLReply<TData>`.
      Four files deleted, two faults with them.
    - **`GraphQLError[]` reads through its own generated context.** Errors are the one part of a
      reply whose type belongs to this library, so the caller cannot be the one to declare it.
    - **Chain operators throw instead of composing.** `Where`, the argument extensions and the
      async terminals built expression trees with `MakeGenericMethod` in bodies that are
      unreachable — a chain's operators are as unreachable as the chain. They now share the
      provider's message.

14. **A NativeAOT binary exists, runs, and passes.** `samples/Feather.GraphQL.AotSmoke` publishes
    to a 3.2 MB self-contained Mach-O arm64 executable with **zero IL warnings**, and exercises all
    three surfaces against a canned transport:

    ```
      ok    compiled chain
      ok    declared document
      ok    hand-written string
    AOT smoke: OK
    ```

    Two build-graph obstacles were in the way, both worth knowing:

    - `Directory.Build.props` sets `TargetFrameworks` (**plural**), which makes every project a
      multi-targeting outer build — and native compilation does not run on an outer build. The
      sample clears it.
    - A global `PublishAot=true` reaches every project in the graph including the netstandard2.0
      analyzer, which fails `NETSDK1207`. The analyzer opts out with `TreatAsLocalProperty`,
      because a global property cannot be overridden any other way.

15. Done earlier: `Feather.GraphQL.Benchmarks.Interpreted` and `QueryPipeline` were deleted in
    Phase 4.

---

## 5. What this costs

**The largest public API break so far**, and unlike the variable contract this one is visible to
anyone writing queries. Every chain outside the compilable subset stops compiling. That is the
intent, but it should ship with the subset written down — not discovered a diagnostic at a time.

**No escape hatch inside LINQ, and none beside it.** An exotic predicate used to cost performance
quietly; it now costs a rewrite — the query leaves the chain and is written out, here or in
whatever else generates GraphQL. The diagnostics say so and name `SendGraphQLQueryAsync`, which is
the whole of what this library offers for a document it did not compile.

**`IQueryable<T>` keeps operators the compiler cannot translate.** `GroupBy`, `Join`, `SelectMany`
and friends remain visible on the type and will now be errors rather than fallbacks. A bespoke chain
type would have made them not exist; keeping `IQueryable` is the decision taken, so the diagnostics
carry that weight instead and should name the operator.

---

## 6. What to measure

| Question | How |
| --- | --- |
| Does deleting the runtime path shrink a published binary? | Phase 6 smoke app, before and after |
| Does it change request cost? | `ClientComparison` — expect no change; the compiled rows never touched it |
| Is anything left that blocks AOT? | `PublishAot` with warnings as errors |

Expect **no runtime speedup**. The compiled path already bypasses everything being deleted; what
this buys is size, startup, trimmability, and one way to do things. Per the 2026-09-12 report,
treat any `ClientComparison` timing move under ~8% as noise.

---

## 7. Open questions

1. ~~**Does streaming survive?**~~ Resolved: no. See §3.1.
2. **Does `Feather.GraphQL.Linq.Providers.HttpClient` still exist?** After Phase 4 its executor is
   gone; what remains is `CreateQueryable` and the options type, which could fold into
   `Feather.GraphQL.Linq`.
3. **Does `Feather.GraphQL.Linq` still reference `Serialization` at run time?** Once nothing
   executes, the generated code is what uses `PooledBody` — the package reference may be
   build-only.
4. **What is the documented subset?** This plan assumes it gets written down. Someone has to
   enumerate what compiles, and it should be generated from the analyzer's own cases rather than
   maintained by hand.
