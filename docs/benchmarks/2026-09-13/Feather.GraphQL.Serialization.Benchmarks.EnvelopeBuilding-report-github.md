```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.4 (25E246) [Darwin 25.4.0]
Apple M5 Pro, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.300
  [Host]     : .NET 10.0.8 (10.0.8, 10.0.826.23019), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.8 (10.0.8, 10.0.826.23019), Arm64 RyuJIT armv8.0-a


```
| Method                               | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|------------------------------------- |----------:|---------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| &#39;ArrayBufferWriter + Utf8JsonWriter&#39; | 165.90 ns | 2.840 ns | 4.072 ns |  1.00 |    0.03 | 0.5765 | 0.0103 |    4824 B |       1.000 |
| &#39;ArrayBufferWriter, pre-sized&#39;       |  65.11 ns | 0.214 ns | 0.167 ns |  0.39 |    0.01 | 0.0842 | 0.0001 |     704 B |       0.146 |
| PooledBody                           |  26.79 ns | 0.100 ns | 0.084 ns |  0.16 |    0.00 | 0.0038 |      - |      32 B |       0.007 |
| &#39;PooledBody, document pre-escaped&#39;   |  12.73 ns | 0.031 ns | 0.028 ns |  0.08 |    0.00 | 0.0038 |      - |      32 B |       0.007 |
