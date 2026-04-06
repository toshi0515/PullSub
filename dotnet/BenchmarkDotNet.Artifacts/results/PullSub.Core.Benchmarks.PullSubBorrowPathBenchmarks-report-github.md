```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.8037)
Unknown processor
.NET SDK 10.0.104
  [Host]     : .NET 8.0.25 (8.0.2526.11203), X64 RyuJIT AVX2
  DefaultJob : .NET 8.0.25 (8.0.2526.11203), X64 RyuJIT AVX2


```
| Method                        | Mean     | Error    | StdDev  | Gen0   | Allocated |
|------------------------------ |---------:|---------:|--------:|-------:|----------:|
| DispatchDataOnly_BorrowPath   | 605.9 ns | 10.08 ns | 9.43 ns | 0.0048 |      48 B |
| DispatchMixed_CopyOnQueuePath | 694.4 ns |  4.16 ns | 3.24 ns | 0.0238 |     232 B |
