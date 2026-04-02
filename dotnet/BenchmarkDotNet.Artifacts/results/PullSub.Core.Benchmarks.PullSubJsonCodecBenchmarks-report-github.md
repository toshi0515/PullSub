```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.26200.8037)
Unknown processor
.NET SDK 10.0.104
  [Host]     : .NET 8.0.25 (8.0.2526.11203), X64 RyuJIT AVX2
  DefaultJob : .NET 8.0.25 (8.0.2526.11203), X64 RyuJIT AVX2


```
| Method              | Mean       | Error    | StdDev  | Gen0   | Allocated |
|-------------------- |-----------:|---------:|--------:|-------:|----------:|
| JsonEncodeBytes     |   427.8 ns |  5.77 ns | 4.82 ns | 0.1240 |    1168 B |
| FlatJsonEncodeBytes | 1,099.3 ns | 10.42 ns | 9.75 ns | 0.1183 |    1120 B |
| JsonDecode          |   536.2 ns |  4.73 ns | 4.42 ns | 0.0048 |      48 B |
| FlatJsonDecode      | 1,506.2 ns |  6.67 ns | 5.57 ns | 0.1240 |    1176 B |
