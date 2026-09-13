```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.4 (25E246) [Darwin 25.4.0]
Apple M5 Pro, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.300
  [Host]     : .NET 10.0.8 (10.0.8, 10.0.826.23019), Arm64 RyuJIT armv8.0-a
  Job-COAERA : .NET 10.0.8 (10.0.8, 10.0.826.23019), Arm64 RyuJIT armv8.0-a

WarmupCount=1  

```
| Method                              | Rows | Mean       | Error    | StdDev   | Ratio | Gen0   | Gen1   | Allocated | Alloc Ratio |
|------------------------------------ |----- |-----------:|---------:|---------:|------:|-------:|-------:|----------:|------------:|
| **Static**                              | **1**    |   **506.0 ns** |  **4.32 ns** |  **3.61 ns** |  **1.00** | **0.2508** |      **-** |   **2.05 KB** |        **1.00** |
| &#39;Static (filtered)&#39;                 | 1    |   534.9 ns |  3.78 ns |  3.35 ns |  1.06 | 0.2584 |      - |   2.12 KB |        1.03 |
| &#39;Feather: AOT SourceGen&#39;            | 1    |   343.7 ns |  4.48 ns |  3.97 ns |  0.68 | 0.1874 |      - |   1.53 KB |        0.75 |
| &#39;Feather: AOT SourceGen (filtered)&#39; | 1    |   352.8 ns |  3.88 ns |  3.24 ns |  0.70 | 0.1874 |      - |   1.53 KB |        0.75 |
| &#39;Feather: LINQ compiled&#39;            | 1    |   360.3 ns |  1.89 ns |  1.68 ns |  0.71 | 0.1960 |      - |    1.6 KB |        0.78 |
| &#39;Feather: LINQ compiled (filtered)&#39; | 1    |   374.6 ns |  2.71 ns |  2.41 ns |  0.74 | 0.1960 |      - |    1.6 KB |        0.78 |
| &#39;GraphQL.Client: send + read&#39;       | 1    |   953.0 ns |  2.32 ns |  1.94 ns |  1.88 | 0.5341 |      - |   4.39 KB |        2.14 |
|                                     |      |            |          |          |       |        |        |           |             |
| **Static**                              | **25**   | **3,966.8 ns** | **34.49 ns** | **28.80 ns** |  **1.00** | **0.8926** | **0.0153** |    **7.3 KB** |        **1.00** |
| &#39;Static (filtered)&#39;                 | 25   | 3,966.6 ns | 16.26 ns | 15.21 ns |  1.00 | 0.8926 | 0.0076 |   7.34 KB |        1.01 |
| &#39;Feather: AOT SourceGen&#39;            | 25   | 2,685.5 ns |  8.17 ns |  6.38 ns |  0.68 | 0.7744 | 0.0038 |   6.33 KB |        0.87 |
| &#39;Feather: AOT SourceGen (filtered)&#39; | 25   | 2,662.7 ns |  4.89 ns |  4.08 ns |  0.67 | 0.7668 | 0.0038 |   6.28 KB |        0.86 |
| &#39;Feather: LINQ compiled&#39;            | 25   | 2,630.7 ns |  9.23 ns |  7.71 ns |  0.66 | 0.9079 | 0.0076 |   7.42 KB |        1.02 |
| &#39;Feather: LINQ compiled (filtered)&#39; | 25   | 2,660.8 ns | 17.30 ns | 13.51 ns |  0.67 | 0.9079 | 0.0076 |   7.45 KB |        1.02 |
| &#39;GraphQL.Client: send + read&#39;       | 25   | 4,539.8 ns | 21.45 ns | 19.02 ns |  1.14 | 1.1597 |      - |   9.65 KB |        1.32 |
