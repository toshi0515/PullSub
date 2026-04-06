```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.8037)
Unknown processor
.NET SDK 10.0.104
  [Host]     : .NET 8.0.25 (8.0.2526.11203), X64 RyuJIT AVX2
  DefaultJob : .NET 8.0.25 (8.0.2526.11203), X64 RyuJIT AVX2


```
| Method                        | Mean     | Error   | StdDev  | Gen0   | Allocated |
|------------------------------ |---------:|--------:|--------:|-------:|----------:|
| DispatchDataOnly_BorrowPath   | 591.7 ns | 4.61 ns | 5.66 ns | 0.0048 |      48 B |
| DispatchMixed_CopyOnQueuePath | 688.5 ns | 2.03 ns | 1.80 ns | 0.0238 |     232 B |
