```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.8037)
Unknown processor
.NET SDK 10.0.104
  [Host]     : .NET 8.0.25 (8.0.2526.11203), X64 RyuJIT AVX2
  DefaultJob : .NET 8.0.25 (8.0.2526.11203), X64 RyuJIT AVX2


```
| Method                  | Mean      | Error     | StdDev    | Gen0   | Allocated |
|------------------------ |----------:|----------:|----------:|-------:|----------:|
| RegistryTryGetCache     |  16.40 ns |  0.291 ns |  0.272 ns |      - |         - |
| RegistryDecodeAndUpdate | 556.12 ns | 10.818 ns | 15.514 ns | 0.0048 |      48 B |
| RegisterThenUnregister  |  83.63 ns |  1.449 ns |  1.423 ns | 0.0178 |     168 B |
