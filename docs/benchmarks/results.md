# Local performance measurements

Measured on November 25, 2026, on Windows 11 with an Intel Core i7-10750H, Docker Desktop Linux Engine, .NET SDK 10.0.404, and .NET runtime 10.0.15. The results describe a local demonstration workload.

## Event serialization

[TransferEventBenchmarks](../../benchmarks/FinFlow.Benchmarks/Program.cs) measures JSON serialization and deserialization of the actual RabbitMQ transfer event. BenchmarkDotNet 0.15.8 ran one launch with three warmup and three measurement iterations.

| Operation | Mean | Allocation per operation |
| --- | ---: | ---: |
| Serialize | 962.4 ns | 464 B |
| Deserialize | 2,512.5 ns | 592 B |

```sh
dotnet run -c Release --project benchmarks/FinFlow.Benchmarks -- --filter '*TransferEventBenchmarks*' --job short
```

## Gateway latency

[k6 scenario](../../benchmarks/gateway.js) sent GET requests to the gateway liveness endpoint from the secured Compose network. With five virtual users over 15 seconds, it completed 1,404 requests at 93.4 requests/second, with **5.9 ms p95**, **1.75 ms mean**, and **zero failures**. Both thresholds passed. This workload measures gateway health response overhead. Transfer and settlement throughput require a separate workload.

```sh
docker run --rm --network finflow-secure_default \
  --mount "type=bind,source=$PWD/benchmarks/gateway.js,target=/gateway.js,readonly" \
  grafana/k6:2.4.0 run /gateway.js
```
