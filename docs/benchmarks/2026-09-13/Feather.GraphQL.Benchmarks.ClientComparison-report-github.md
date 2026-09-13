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
| **Static**                              | **1**    |   **509.8 ns** |  **5.94 ns** |  **4.96 ns** |  **1.00** |    **0.01** | **0.2508** |      **-** |   **2.05 KB** |        **1.00** |
| &#39;Static (filtered)&#39;                 | 1    |   667.3 ns | 13.11 ns | 13.46 ns |  1.31 |    0.03 | 0.8240 | 0.0143 |   6.73 KB |        3.28 |
| &#39;Feather: AOT SourceGen&#39;            | 1    |   344.2 ns |  2.60 ns |  2.31 ns |  0.68 |    0.01 | 0.1864 |      - |   1.52 KB |        0.74 |
| &#39;Feather: AOT SourceGen (filtered)&#39; | 1    |   346.5 ns |  0.78 ns |  0.69 ns |  0.68 |    0.01 | 0.1864 |      - |   1.52 KB |        0.74 |
| &#39;Feather: LINQ compiled&#39;            | 1    |   371.0 ns |  2.38 ns |  1.99 ns |  0.73 |    0.01 | 0.1960 |      - |    1.6 KB |        0.78 |
| &#39;Feather: LINQ compiled (filtered)&#39; | 1    |   372.1 ns |  1.35 ns |  1.20 ns |  0.73 |    0.01 | 0.1950 |      - |   1.59 KB |        0.78 |
| &#39;GraphQL.Client: send + read&#39;       | 1    |   939.8 ns |  2.72 ns |  2.27 ns |  1.84 |    0.02 | 0.5341 |      - |   4.41 KB |        2.14 |
|                                     |      |            |          |          |       |         |        |        |           |             |
| **Static**                              | **25**   | **3,948.6 ns** |  **5.09 ns** |  **4.25 ns** |  **1.00** |    **0.00** | **0.8926** | **0.0153** |   **7.31 KB** |        **1.00** |
| &#39;Static (filtered)&#39;                 | 25   | 4,073.7 ns | 14.52 ns | 12.87 ns |  1.03 |    0.00 | 1.4648 | 0.0305 |  11.98 KB |        1.64 |
| &#39;Feather: AOT SourceGen&#39;            | 25   | 2,691.9 ns |  9.22 ns |  7.70 ns |  0.68 |    0.00 | 0.7668 | 0.0038 |   6.27 KB |        0.86 |
| &#39;Feather: AOT SourceGen (filtered)&#39; | 25   | 2,660.4 ns | 16.38 ns | 14.52 ns |  0.67 |    0.00 | 0.7706 | 0.0038 |    6.3 KB |        0.86 |
| &#39;Feather: LINQ compiled&#39;            | 25   | 2,629.0 ns | 16.12 ns | 13.46 ns |  0.67 |    0.00 | 0.9079 | 0.0076 |   7.42 KB |        1.01 |
| &#39;Feather: LINQ compiled (filtered)&#39; | 25   | 2,644.8 ns |  9.78 ns |  8.67 ns |  0.67 |    0.00 | 0.9117 | 0.0076 |   7.48 KB |        1.02 |
| &#39;GraphQL.Client: send + read&#39;       | 25   | 4,518.1 ns | 22.36 ns | 19.82 ns |  1.14 |    0.00 | 1.1597 |      - |   9.66 KB |        1.32 |
