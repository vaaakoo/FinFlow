using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using FinFlow.Messaging;

BenchmarkSwitcher.FromAssembly(typeof(TransferEventBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
public class TransferEventBenchmarks
{
    private TransferCompleted message = null!;
    private byte[] payload = null!;

    [GlobalSetup]
    public void SetUp()
    {
        message = new TransferCompleted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            12500, "GEL", DateTimeOffset.UtcNow, new string('a', 32), Guid.NewGuid().ToString());
        payload = JsonSerializer.SerializeToUtf8Bytes(message);
    }

    [Benchmark]
    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(message);

    [Benchmark]
    public TransferCompleted? Deserialize() => JsonSerializer.Deserialize<TransferCompleted>(payload);
}
