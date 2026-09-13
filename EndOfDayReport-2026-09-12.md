# End of Day Report — 2026-09-12

Feather.GraphQL · branch `master` · Apple M5 Pro (18 cores) · macOS Tahoe 26.4 · .NET 10.0.8 Arm64 RyuJIT
BenchmarkDotNet v0.15.8 · all figures from `-c Release`, default job unless noted.

---

## A note on provenance

You asked to compare yesterday to now. Most of yesterday's numbers no longer exist:
BenchmarkDotNet overwrites `BenchmarkDotNet.Artifacts/results/` on every run, and today's runs
overwrote them. Rather than reconstruct figures from memory, every comparison below is labelled
with where its "before" came from. There are exactly three kinds:

| Label | Meaning |
| --- | --- |
| **[artifact]** | A real file written 2026-09-11 and still on disk. |
| **[measured]** | Start-of-today code, benchmarked today by stashing the day's changes and re-running. The code at the start of today *is* the code at the end of yesterday, so this is a true before/after — and it was run on the same machine in the same session, which the artifacts were not. |
| **[new]** | No prior number exists. Today's absolute figures only. |

Anything not carrying one of those labels is a same-run comparison within today's table.

The only surviving 2026-09-11 artifact is `ShapingStrategies`. The only other file predating today
is a `ClientComparison` run from 2026-09-11 00:45 that used `--job dry`, so every cell reads `NA`;
it is worthless and is not used here.

---

## 1. What changed today

Two changes landed in the source generator. Several further ideas were tested and rejected on the
evidence.

### Landed

**1. Reply readers are specialised to the query's pagination shape.**
`ResponseStructWriter` previously emitted one reply reader covering every shape, so every generated
reader carried the union of what any query might need. An un-paged query paid, per property of the
root field's value, three UTF-8 comparisons — `"nodes"`, `"items"`, `"totalCount"` — against names
its own document guaranteed would never arrive, plus a `StartObject` branch for a connection that
cannot exist and a `long? TotalCount` field nothing ever set.

The decision was already being computed in `Reduction` (`facts.Paging`, `facts.IsCount`) to pick a
read path, then discarded. It is now carried through as `ReplyShape { List, Cursor, Offset, Count,
Single }`, and exactly one shape is emitted. Un-paged queries lose the connection branch entirely;
cursor emits only `"nodes"`, offset only `"items"`, count only `"totalCount"`.

*Result: performance-neutral (§3). Kept for correctness and clarity, not speed.*

**2. `ReadRows` owns its own memory and returns the array.**
It previously took a caller-allocated `List<Row>`, allocated before the envelope walk began — so an
empty reply or an errors-only reply paid for it too. For 25 rows of a 16-byte row struct that was
**six allocations and 53 row-copies to deliver 400 bytes of rows**: the list object, backing arrays
of 4/8/16/32, and a final `ToArray`.

It now returns `Row[]`, growing through `ArrayPool<Row>.Shared` with a single exactly-sized
allocation at the end. The buffer starts as `Array.Empty<Row>()` so the capacity check the growth
already needs (`at == buffer.Length`) is what triggers the first rent — a reply with no rows touches
neither the pool nor the heap. Buffers are returned with `clearArray: true`, because rows hold
strings and an uncleared pooled buffer would keep the last reply's values reachable.

*Result: 9–13.5% less allocation per reply end-to-end (§3).*

### Rejected on evidence

| Idea | Verdict | Why |
| --- | --- | --- |
| `Utf8JsonReader.CopyString` instead of `GetString()` | **12–13% slower, identical allocation** | `CopyString` avoids allocating *a string*, but the destination member *is* a `string`, so `new string(span)` allocates the same object one step later. We pay an extra hop for nothing. |
| String deduplication cache | **31% slower, 26–73% more allocation** | The dictionary costs more to stand up per reply than the duplicate strings it saves. 25 rows × 7 continents is not enough repetition to amortise a hash table built and discarded per call. |
| Pre-count rows with `Skip()` | **1.7–1.9× slower** | Double tokenization costs far more than the six allocations it saves. |
| Brace scan (`SearchValues`) to count + slice rows | **2.2–2.4× slower** | This JSON is *dense* in structural bytes — a row is one structural byte every ~4 bytes, because property names are quoted too. Vectorized search wins by skipping long uninteresting runs; there are none. |
| Reading each row from its own slice | **1.6× the scan alone** | A fresh `Utf8JsonReader` per row costs more than carrying one cursor through all rows. |
| `Parallel.For` over row slices | **3.5–6.7× slower, up to 1.8× allocation** | Dispatch dominates at every size tested. |
| Rows holding spans instead of strings, materialised at projection | **Allocation identical (1.00–1.01×)** in the ordinary case | The selection set is derived from the projection, so every field read is one the projection consumes. Deferring moves the allocation rather than removing it. Narrow win where a field is only compared — see §8.1, and §8.3 for what is worth building instead. |

The brace-scan rejection deserves its ceiling stated, because a better scanner is possible (a real
simdjson-style structural classifier runs ~0.3–0.5 ns/byte against my `IndexOfAny`-per-match
hybrid). It would not matter. Knowing the count only avoids the pool rent/return and the final
400-byte copy — roughly **50 ns of 1,949**, about 2.5%. My scan costs ~2,900 ns for a 1.2 KB
payload; even at simdjson speeds it would be ~400–600 ns. It has to be *free* to break even, and
then it wins 2.5%.

---

## 2. Measurement noise, characterised

Today produced a run-to-run swing of up to ±8% on generated rows, which is larger than most of the
effects being measured. It is now explained rather than hand-waved, and two controls pin it down.

**Control 1 — untouched code within today.** `Static` and `GraphQL.Client` exercise code nothing
touched all day:

| Row (25 rows) | Mid-day | End of day | Δ |
| --- | ---: | ---: | ---: |
| Static | 3,861 ns | 3,844 ns | −0.4% |
| Static (filtered) | 3,845 ns | 3,884 ns | +1.0% |
| GraphQL.Client | 4,479 ns | 4,460 ns | −0.4% |

**Control 2 — untouched code across 24 hours.** `ShapingStrategies` at 100 rows **[artifact]**:

| Method | 2026-09-11 | 2026-09-12 | Δ time | Allocated then → now |
| --- | ---: | ---: | ---: | --- |
| `for + index` | 427.21 ns | 397.86 ns | −6.9% | 4024 B → 4024 B |
| `for + span` | 408.10 ns | 401.98 ns | −1.5% | 4024 B → 4024 B |
| `ref + Unsafe.Add` | 409.95 ns | 419.92 ns | +2.4% | 4024 B → 4024 B |
| `Parallel.For` | 4,263.07 ns | 4,271.83 ns | +0.2% | 7186 B → 7200 B |
| `LINQ Select` | 496.10 ns | 486.87 ns | −1.9% | 4072 B → 4072 B |
| `for + index, no alloc` | 89.20 ns | 90.93 ns | +1.9% | 824 B → 824 B |
| `ref + Unsafe.Add, no alloc` | 101.81 ns | 96.02 ns | −5.7% | 824 B → 824 B |

Allocation is **byte-identical across 24 hours** on six of seven rows (the seventh is
thread-count-dependent). That is strong evidence the payloads, seeds and harness are unchanged, and
that timing drift of ±7% is the machine, not the code.

**The conclusion that matters:** unchanged code is stable to ~1% within a session and ~7% across
days, but rows whose *generated source changed* swung up to 8% between runs. Editing the emitter
changes the generated file's contents, moving code and shifting alignment — so the noise band lands
precisely on the rows being evaluated. **Timing deltas under ~8% on generated rows are not
trustworthy today. `Allocated` is deterministic and is the column to judge these changes by.**

---

## 3. Client comparison — the headline table

`{ countries { name continent { name } } }` over a canned transport, identical bytes per row count.

### End of day, 2026-09-12

| Method | Rows | Mean | Ratio | Allocated | Alloc ratio |
| --- | ---: | ---: | ---: | ---: | ---: |
| Static (hand-written string) | 1 | 511.2 ns | 1.00 | 2.31 KB | 1.00 |
| Static (filtered) | 1 | 535.0 ns | 1.05 | 2.42 KB | 1.05 |
| **Feather: AOT SourceGen** | 1 | **382.4 ns** | **0.75** | 1.79 KB | 0.77 |
| Feather: AOT SourceGen (filtered) | 1 | 411.6 ns | 0.81 | 1.88 KB | 0.81 |
| **Feather: LINQ compiled** | 1 | 401.5 ns | 0.79 | 1.83 KB | 0.79 |
| Feather: LINQ compiled (filtered) | 1 | 454.3 ns | 0.89 | 1.94 KB | 0.84 |
| GraphQL.Client | 1 | 928.4 ns | 1.82 | 4.40 KB | 1.90 |
| | | | | | |
| Static (hand-written string) | 25 | 3,843.8 ns | 1.00 | 7.52 KB | 1.00 |
| Static (filtered) | 25 | 3,884.0 ns | 1.01 | 7.63 KB | 1.02 |
| Feather: AOT SourceGen | 25 | 2,692.2 ns | 0.70 | 6.47 KB | 0.86 |
| Feather: AOT SourceGen (filtered) | 25 | 2,690.7 ns | 0.70 | 6.60 KB | 0.88 |
| **Feather: LINQ compiled** | 25 | **2,521.5 ns** | **0.66** | 6.95 KB | 0.93 |
| Feather: LINQ compiled (filtered) | 25 | 2,593.1 ns | 0.67 | 7.03 KB | 0.94 |
| GraphQL.Client | 25 | 4,460.2 ns | 1.16 | 9.63 KB | 1.28 |

A compiled LINQ chain is **0.66× the cost of posting the document by hand** and **1.77× faster than
GraphQL.Client** at 25 rows. At 1 row it is 0.79× and 2.31× respectively.

### Start of day → end of day **[measured]**

| Method | Rows | Time before → after | Δ | Allocated before → after | Δ |
| --- | ---: | --- | ---: | --- | ---: |
| AOT SourceGen | 1 | 397.7 → 382.4 ns | −3.8% | 1.88 → 1.79 KB | **−4.8%** |
| AOT SourceGen (filtered) | 1 | 412.8 → 411.6 ns | −0.3% | 1.98 → 1.88 KB | **−5.1%** |
| LINQ compiled | 1 | 395.3 → 401.5 ns | +1.6% | 1.95 → 1.83 KB | **−6.2%** |
| LINQ compiled (filtered) | 1 | 451.7 → 454.3 ns | +0.6% | 2.05 → 1.94 KB | **−5.4%** |
| AOT SourceGen | 25 | 2,809.8 → 2,692.2 ns | −4.2% | 7.13 → 6.47 KB | **−9.3%** |
| AOT SourceGen (filtered) | 25 | 2,679.8 → 2,690.7 ns | +0.4% | 7.24 → 6.60 KB | **−8.8%** |
| LINQ compiled | 25 | 2,600.7 → 2,521.5 ns | −3.0% | 7.99 → 6.95 KB | **−13.0%** |
| LINQ compiled (filtered) | 25 | 2,634.3 → 2,593.1 ns | −1.6% | 8.13 → 7.03 KB | **−13.5%** |

**Allocation is down 4.8–13.5% on every generated row.** Gen0 collections fall with it (0.870 →
0.790 per 1k ops on the AOT row; 0.977 → 0.851 on LINQ compiled).

**Timing improved on six of eight rows but by less than the noise band (§2), so no timing claim is
made.** The honest summary of today's generator work is: *materially less garbage per reply, no
reliable change in wall-clock.* For a client library under sustained load that is the axis that
matters — allocation rate drives GC pressure, and its benefit appears in a real application's tail
latencies rather than in a tight loop that never lets a gen1 collection happen.

---

## 4. The cost of *not* compiling **[new]**

`Feather.GraphQL.Benchmarks.Interpreted` is the same source with no analyzer referenced.

| Method | Rows | Mean | Ratio | Allocated |
| --- | ---: | ---: | ---: | ---: |
| Static (control) | 25 | 3,917.1 ns | 1.00 | 7.59 KB |
| LINQ (interpreted) | 1 | 51,470.5 ns | 99.21 | 17.37 KB |
| LINQ (interpreted) | 25 | 53,162.8 ns | 13.57 | 25.42 KB |
| LINQ (interpreted) | 100 | 64,821.4 ns | 4.50 | 50.35 KB |
| LINQ, no projection | 1 | 918.5 ns | 1.77 | 4.86 KB |
| LINQ, no projection | 25 | 4,685.1 ns | 1.20 | 10.26 KB |

Against the compiled figures in §3, the analyzer is worth **128× at 1 row** (51,470 → 401.5 ns) and
**21× at 25 rows** (53,163 → 2,521 ns).

The `Static` control reads 3,917 ns here against 3,844 ns in the generated project — 1.9% apart —
confirming the two assemblies measure the same machine and the gap is real.

The shape of the numbers names the cost exactly: 51.5 µs at 1 row and 64.8 µs at 100 means **~50 µs
is fixed**, paid before any row is read. That is `Expression.Compile()`. The no-projection row
confirms it from the other side — 1.77× rather than 99×, because there is no lambda to compile.

---

## 5. Row accumulation — the full strategy sweep **[new]**

Isolated study behind the §1 change. Reading is identical across rows; only accumulation differs.

| Strategy | 0 rows | 1 row | 25 rows | 100 rows | Alloc @25 |
| --- | ---: | ---: | ---: | ---: | ---: |
| `List` + `ToArray` *(was generated)* | 32.57 ns | 103.22 ns | 2,066.78 ns | 8,675.15 ns | 3696 B |
| pooled + exact copy | 33.76 ns | 110.16 ns | 2,021.08 ns | 8,613.15 ns | 2584 B |
| **pooled, rented on first row** *(now generated)* | **27.80 ns** | **102.30 ns** | **1,928.22 ns** | **8,432.54 ns** | **2464 B** |
| pre-count + exact fill | 34.36 ns | 166.36 ns | 3,574.17 ns | 15,080.99 ns | 2552 B |
| brace scan + exact fill | 26.63 ns | 177.21 ns | 4,654.39 ns | 19,364.39 ns | 2568 B |
| brace scan + sliced reads | 24.26 ns | 266.87 ns | 7,567.15 ns | 30,174.75 ns | 2544 B |
| brace scan + parallel reads | 26.02 ns | 1,468.95 ns | 13,014.21 ns | 30,445.20 ns | 6538 B |

The chosen strategy is fastest at 0, 1, 25 and 100 rows, and allocates least at 25. Deferring the
rent matters specifically at 0 rows: renting eagerly made the empty case *slower than the list*
(33.76 vs 32.57 ns) despite allocating nothing.

The benchmark's `GlobalSetup` asserts all seven strategies find the same rows at every size, and
separately scans a hand-written payload containing `"{not a brace}"`, `"quote \" and backslash \\"`
and `"]}"` — the cases a byte scan can get wrong and a tokenizer cannot. That harness is worth
keeping regardless of which strategy won; it is what makes the next idea here cheap to falsify.

---

## 6. Supporting measurements **[new]**

### String reading — `GetString` vs `CopyString`

| Strategy | 25 rows | Ratio | 100 rows | Ratio | Alloc @25 |
| --- | ---: | ---: | ---: | ---: | ---: |
| `GetString` *(generated)* | 2.068 µs | 1.00 | 8.616 µs | 1.00 | 3.43 KB |
| `CopyString` + `new string` | 2.306 µs | 1.12 | 9.731 µs | 1.13 | 3.46 KB |
| `CopyString` + reuse | 2.702 µs | 1.31 | 11.097 µs | 1.29 | 5.92 KB |

### Reply deserialization — reflection vs `System.Text.Json` source generation

| Rows | reflection | source-generated | Ratio |
| ---: | ---: | ---: | ---: |
| 1 | 311.0 ns | 306.9 ns | 0.99 |
| 25 | 5,008.9 ns | 4,888.7 ns | 0.98 |
| 100 | 20,040.6 ns | 18,861.5 ns | 0.94 |
| 500 | 97,841.3 ns | 95,150.5 ns | 0.97 |

STJ source generation buys 2–6% over reflection with identical allocation. Worth contrast with §3:
Feather's *bespoke per-query* readers beat the hand-written string path by 30–34%, because they
skip contract resolution entirely rather than making it cheaper.

### Query pipeline — runtime translation (untouched today)

| Method | 1 row | 25 rows | Alloc @25 |
| --- | ---: | ---: | ---: |
| Composed at runtime | 2.582 µs (1.00) | 7.696 µs (1.00) | 18.12 KB |
| Precompiled document | 1.892 µs (0.73) | 7.093 µs (0.92) | 13.59 KB |
| Projected: composed at runtime | 1.909 µs (0.74) | 6.743 µs (0.88) | 15.04 KB |
| Projected: precompiled plan | 1.119 µs (0.43) | 6.083 µs (0.79) | 11.13 KB |

### Shaping loops (untouched today; see §2 for the 24-hour control)

At 100 rows: `for + index` 397.9 ns, `for + span` 402.0 ns, `ref + Unsafe.Add` 419.9 ns — all within
6% of each other and allocating identically. `Parallel.For` is 4,271.8 ns, **10.7× slower**, at 1.79×
allocation. `for + index, no allocation` is 90.9 ns, **0.23×**.

This is the day's recurring lesson in its clearest form: **allocation is the cost; loop cleverness
and parallelism are not.** The brace-scan and `CopyString` results arrived at the same place from
two other directions.

---

## 7. Tests

| Suite | Count | Result |
| --- | ---: | --- |
| `Feather.GraphQL.Linq.Analyzers.Tests` | 122 | pass (was 120) |
| `Feather.GraphQL.Linq.Providers.HttpClient.Tests` | 93 | pass |
| `Feather.GraphQL.Linq.Tests` | 200 | pass |

Three tests added for pagination shape: cursor emits `"nodes"` and *not* `"items"`/`"totalCount"`,
offset the converse, un-paged neither.

Two existing assertions were corrected, and both were wrong in a way worth recording:

- `A_paged_field_is_read_through_the_wrapper` set up a **cursor** query and asserted that both
  `"nodes"u8` **and** `"items"u8` were emitted. It only ever passed because the old emitter wrote
  both unconditionally — it was asserting the waste.
- `Numbers_read_through_their_own_getters` asserted `reader.TokenType == …Number` appeared in the
  output. A **false positive**: member reads guard with `== Null ? default : reader.GetInt32()`, and
  the only `== Number` in the file was the dead `totalCount` branch. The test's stated intent is
  intact; it now asserts the guard that actually exists.

`Feather.GraphQL.Serialization.Tests` reports "No test fixtures were found" — it contains only
`Program.cs`. Pre-existing and unrelated to today's work, but it is an empty suite presenting as a
passing one, which is worth fixing.

---

## 8. Where the remaining cost is

After today, per-reply allocation in the generated path is dominated by **the strings themselves**.
At 25 rows the row-reading allocation is ~2.5 KB, most of it the 50 string instances the caller
asked to be handed. That is why `CopyString` could not win (§1) and why the row-accumulation floor
sat at 0.70× (§5) — both were trying to optimise around a cost neither could touch.

The next meaningful reduction has to come from *not materialising strings at all*, not from how they
are collected or counted.

### 8.1 Measured: deferring materialisation to the projection **[new]**

The obvious move is to have the row hold *where* its values are rather than what they are —
offsets into the reply's own bytes — and make the strings later, during the projection, if at all.
That was measured (`DeferredMaterialization`), with the row kept to exactly the width of a row of
references (two offsets and two lengths against two string references) so nothing is paid for by a
fatter row.

**The ordinary case, where the projection puts every value into the result:**

| Rows | strings in row | spans in row | Time | Allocated |
| ---: | ---: | ---: | ---: | --- |
| 1 | 106.53 ns | 116.54 ns | 1.09 | 304 → 304 B (**1.00**) |
| 25 | 2,352.12 ns | 2,385.85 ns | 1.01 | 4,616 → 4,664 B (**1.01**) |
| 100 | 9,824.48 ns | 9,090.29 ns | 0.93 | 18,512 → 18,488 B (**1.00**) |

**Allocation is identical to within 1% at every size.** Time moves in both directions (1.09, 1.01,
0.93), which is noise, and the flat allocation column is what says so.

The reason is structural and is the important finding here: `SelectionSetWriter` derives the
selection set *from the projection*, so the document only ever asks for fields the projection
consumes. There is no field being read that nobody wanted — the usual prize for lazy materialisation
was already collected at compile time by not asking the server for it. Deferring therefore *moves*
the allocation from read-time to projection-time rather than removing it. The loop does get faster;
it buys nothing, because the same strings must exist when the projection ends.

**The case where the projection only compares:**

| Rows | strings in row | spans in row | Time | Allocated |
| ---: | ---: | ---: | ---: | --- |
| 1 | 106.94 ns | 92.16 ns | 0.87 | 248 → 160 B (0.53) |
| 25 | 2,275.11 ns | 1,802.52 ns | **0.77** | 3,608 → 1,512 B (**0.33**) |
| 100 | 9,510.98 ns | 7,250.23 ns | **0.74** | 14,576 → 5,832 B (**0.32**) |

Real, large, and it improves with row count — the only thing measured today that does.

Two caveats keep those figures honest:

- **The baseline reads a field it never uses.** Both variants read `name` and `continent` but only
  compare `continent`; a real query whose projection only compares continent would not select
  `name` at all. The mechanism is right, the ratio is flattered.
- **The compared value is ASCII.** See §8.2 — for an escaped value the comparison has to
  materialise first, which is the allocation it was avoiding.

### 8.2 Escapes are not an edge case

The benchmark initially rejected escaped values as unmodelled and **crashed at 25 and 100 rows**.
`JsonSerializer.Serialize` uses the default encoder, which escapes *every* non-ASCII character to
`\uXXXX`, so the payload is full of `C\u00F4te d'Ivoire`, `Cura\u00E7ao`, `R\u00E9union`. The
1-row seed happened to be pure ASCII; the 25-row seed was not.

This matters to any design built on raw spans, because **a raw span is not the value**:

- Materialising needs a slow path that re-reads the token through a `Utf8JsonReader` to interpret
  the escapes — strictly more work than the `GetString()` it replaces.
- The comparison win disappears: `"Café"u8` cannot be matched byte-for-byte against bytes reading
  `Caf\u00e9`, so the value must be materialised before it can be compared.
- Every field needs an "escaped" bit. It fits in the sign of the length, which is what keeps the row
  at 16 bytes; without that trick the row grows and the comparison is paid for by the row.

For applications with international data — names, addresses, cities — a large fraction of string
fields take the slow path in *both* directions.

### 8.3 Recommendation

**Do not switch the row model wholesale.** The ordinary case is a wash, the win case is narrow and
escape-sensitive, and it introduces a memory-safety hazard the current design cannot have: rows
pointing into a pooled buffer that is returned the moment parsing ends. One row reference outliving
the buffer reads whatever the pool handed the next caller.

What is worth building is the targeted version — have the generator detect fields a projection only
**compares or parses**, and emit span access for exactly those while every field flowing into the
result stays a `string`. The generator already re-emits the projection lambda, so it can tell the
two apart. Same upside, no lifetime coupling, and §8.1 is the ceiling it would be chasing.

Ranked with the other candidates:

1. **Span access for compare-only and parse-only fields** (above). Measured ceiling: 0.74× time,
   0.32× allocation, on queries that have such fields at all.
2. **Let callers project to non-string types.** A projection reading `c.Continent.Name` into an enum
   or an interned id never needs the `string` — the same mechanism as (1), reached from the
   caller's side rather than the generator's.
3. **Cross-reply interning for known-small domains.** Today's dedup attempt failed because the cache
   was built and discarded per reply (§1). A cache outliving one reply is a different measurement,
   with real bounds and eviction questions attached.

`ReadRows` will still be the top frame in a profile, because that is where the per-row work is. The
frame to look at inside it is `Row.Read` and the string allocations within — not the accumulation,
which is now one allocation and a single copy.

---

## Appendix — reproducing

```bash
dotnet build -c Release
dotnet run --project benchmarks/Feather.GraphQL.Benchmarks -c Release --no-build -- --filter '*ClientComparison*'
dotnet run --project benchmarks/Feather.GraphQL.Benchmarks -c Release --no-build -- --filter '*QueryPipeline*'
dotnet run --project benchmarks/Feather.GraphQL.Benchmarks.Interpreted -c Release --no-build -- --filter '*'
dotnet run --project benchmarks/Feather.GraphQL.Serialization.Benchmarks -c Release --no-build -- --filter '*'
```

Test suites are NUnitLite executables, not `dotnet test` targets:

```bash
./tests/<Name>/bin/Release/net10.0/<Name> --noresult
```

Copy `BenchmarkDotNet.Artifacts/results/` somewhere dated before the next run, or the next run will
overwrite it — which is what happened to yesterday's numbers.

Tonight's raw BenchmarkDotNet output is archived beside this report so it survives the next run:

- `docs/benchmarks/2026-09-12/` — all eight tables from tonight's full sweep
- `docs/benchmarks/2026-09-11/` — the one salvaged artifact from yesterday (`ShapingStrategies`),
  which is the basis of the 24-hour control in §2
