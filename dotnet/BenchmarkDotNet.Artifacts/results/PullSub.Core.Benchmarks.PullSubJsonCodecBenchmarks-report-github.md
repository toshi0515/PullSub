```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.8037)
Unknown processor
.NET SDK 10.0.104
  [Host]     : .NET 8.0.25 (8.0.2526.11203), X64 RyuJIT AVX2
  DefaultJob : .NET 8.0.25 (8.0.2526.11203), X64 RyuJIT AVX2


```
| Method              | Mean       | Error    | StdDev   | Gen0   | Allocated |
|-------------------- |-----------:|---------:|---------:|-------:|----------:|
| JsonEncodeBytes     |   460.3 ns |  6.11 ns |  5.42 ns | 0.1240 |    1168 B |
| FlatJsonEncodeBytes | 1,115.9 ns |  9.79 ns |  7.64 ns | 0.1183 |    1120 B |
| JsonDecode          |   533.3 ns |  6.10 ns |  5.09 ns | 0.0048 |      48 B |
| FlatJsonDecode      | 1,504.8 ns | 29.71 ns | 27.79 ns | 0.1240 |    1176 B |
