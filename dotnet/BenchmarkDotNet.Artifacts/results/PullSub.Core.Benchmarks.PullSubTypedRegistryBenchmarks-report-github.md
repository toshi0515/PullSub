```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.8037)
Unknown processor
.NET SDK 10.0.104
  [Host]     : .NET 8.0.25 (8.0.2526.11203), X64 RyuJIT AVX2
  DefaultJob : .NET 8.0.25 (8.0.2526.11203), X64 RyuJIT AVX2


```
| Method                  | Mean      | Error    | StdDev   | Gen0   | Allocated |
|------------------------ |----------:|---------:|---------:|-------:|----------:|
| RegistryTryGetCache     |  16.27 ns | 0.037 ns | 0.031 ns |      - |         - |
| RegistryDecodeAndUpdate | 536.66 ns | 6.311 ns | 5.904 ns | 0.0048 |      48 B |
| RegisterThenUnregister  |  78.67 ns | 0.699 ns | 0.654 ns | 0.0169 |     160 B |
