```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.4 (25E246) [Darwin 25.4.0]
Apple M5 Pro, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.300
  [Host]     : .NET 10.0.8 (10.0.8, 10.0.826.23019), Arm64 RyuJIT armv8.0-a
  Job-COAERA : .NET 10.0.8 (10.0.8, 10.0.826.23019), Arm64 RyuJIT armv8.0-a

WarmupCount=1  

```
| Method                              | Rows | Mean       | Error    | StdDev   | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|------------------------------------ |----- |-----------:|---------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| **Static**                              | **1**    |   **511.2 ns** |  **4.20 ns** |  **3.73 ns** |  **1.00** |    **0.01** | **0.2823** |      **-** |   **2.31 KB** |        **1.00** |
| &#39;Static (filtered)&#39;                 | 1    |   535.0 ns |  1.51 ns |  1.34 ns |  1.05 |    0.01 | 0.2956 |      - |   2.42 KB |        1.05 |
| &#39;Feather: AOT SourceGen&#39;            | 1    |   382.4 ns |  0.65 ns |  0.51 ns |  0.75 |    0.01 | 0.2189 | 0.0005 |   1.79 KB |        0.77 |
| &#39;Feather: AOT SourceGen (filtered)&#39; | 1    |   411.6 ns |  1.68 ns |  1.49 ns |  0.81 |    0.01 | 0.2303 | 0.0005 |   1.88 KB |        0.81 |
| &#39;Feather: LINQ compiled&#39;            | 1    |   401.5 ns |  1.61 ns |  1.26 ns |  0.79 |    0.01 | 0.2236 | 0.0005 |   1.83 KB |        0.79 |
| &#39;Feather: LINQ compiled (filtered)&#39; | 1    |   454.3 ns |  1.32 ns |  1.17 ns |  0.89 |    0.01 | 0.2370 | 0.0005 |   1.94 KB |        0.84 |
| &#39;GraphQL.Client: send + read&#39;       | 1    |   928.4 ns |  6.91 ns |  6.13 ns |  1.82 |    0.02 | 0.5341 |      - |    4.4 KB |        1.90 |
|                                     |      |            |          |          |       |         |        |        |           |             |
| **Static**                              | **25**   | **3,843.8 ns** |  **8.15 ns** |  **6.80 ns** |  **1.00** |    **0.00** | **0.9155** | **0.0153** |   **7.52 KB** |        **1.00** |
| &#39;Static (filtered)&#39;                 | 25   | 3,884.0 ns |  7.47 ns |  6.23 ns |  1.01 |    0.00 | 0.9308 | 0.0076 |   7.63 KB |        1.02 |
| &#39;Feather: AOT SourceGen&#39;            | 25   | 2,692.2 ns |  5.72 ns |  5.35 ns |  0.70 |    0.00 | 0.7896 | 0.0038 |   6.47 KB |        0.86 |
| &#39;Feather: AOT SourceGen (filtered)&#39; | 25   | 2,690.7 ns |  5.83 ns |  4.87 ns |  0.70 |    0.00 | 0.8049 | 0.0038 |    6.6 KB |        0.88 |
| &#39;Feather: LINQ compiled&#39;            | 25   | 2,521.5 ns |  6.17 ns |  5.77 ns |  0.66 |    0.00 | 0.8507 | 0.0076 |   6.95 KB |        0.93 |
| &#39;Feather: LINQ compiled (filtered)&#39; | 25   | 2,593.1 ns | 23.20 ns | 21.70 ns |  0.67 |    0.01 | 0.8583 | 0.0076 |   7.03 KB |        0.94 |
| &#39;GraphQL.Client: send + read&#39;       | 25   | 4,460.2 ns | 21.11 ns | 17.63 ns |  1.16 |    0.00 | 1.1597 |      - |   9.63 KB |        1.28 |
