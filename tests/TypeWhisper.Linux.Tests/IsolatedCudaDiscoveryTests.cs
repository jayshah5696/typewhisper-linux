using Moq;
using TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class IsolatedCudaDiscoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OptionalCudaProbeCannotPoisonBaselineVulkanDiscovery(bool ready, bool broken)
    {
        var inner = new Mock<ITranscriptionEngineRole>();
        inner.SetupGet(value => value.HardwareSelection).Returns(Mock.Of<ITranscriptionHardwareSelection>());
        inner.SetupGet(value => value.ProvisionsCudaRuntimeOnDemand).Returns(true);
        inner.SetupGet(value => value.IsCudaRuntimeProvisioned).Returns(ready);
        inner.SetupGet(value => value.SupportedAccelerationBackends).Returns([TranscriptionAccelerationBackend.Cpu,
            TranscriptionAccelerationBackend.Vulkan, TranscriptionAccelerationBackend.NvidiaCuda]);
        var workers = new List<Probe>();
        await using var engine = new IsolatedTranscriptionEngine(inner.Object, (preference, _, _) =>
        {
            var worker = new Probe(preference, broken);
            workers.Add(worker);
            return Task.FromResult<ITranscriptionWorkerConnection>(worker);
        }, (_, _) => { });

        var devices = await engine.HardwareSelection!.DiscoverDevicesAsync("model", CancellationToken.None);

        Assert.Contains(devices, value => value.Backend == TranscriptionAccelerationBackend.Vulkan && value.IsAvailable);
        Assert.Equal(ready ? 2 : 1, workers.Count);
        Assert.All(workers, value => Assert.True(value.Disposed));
        Assert.Equal(ready && !broken, devices.Any(value => value.Backend == TranscriptionAccelerationBackend.NvidiaCuda && value.IsAvailable));
        if (ready && broken) Assert.Contains(devices, value => value.Backend == TranscriptionAccelerationBackend.NvidiaCuda && !value.IsAvailable
            && value.UnavailableReason!.Contains("driver", StringComparison.Ordinal));
        inner.Verify(value => value.EnsureCudaRuntimeReadyAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(engine.WorkerProcessId);
    }

    private sealed class Probe(TranscriptionAccelerationPreference preference, bool broken) : ITranscriptionWorkerConnection
    {
        public TranscriptionAccelerationPreference Acceleration => preference;
        public int ProcessId => 1;
        public bool IsAlive => !Disposed;
        public TranscriptionWorkerState? State => null;
        internal bool Disposed { get; private set; }
        public Task<TranscriptionWorkerMessage> SendAsync(TranscriptionWorkerMessage request, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            var cuda = preference == TranscriptionAccelerationPreference.NvidiaCuda;
            return Task.FromResult(cuda && broken
                ? new TranscriptionWorkerMessage { Error = TranscriptionWorkerProtocol.ToError(new InvalidOperationException("broken CUDA driver")) }
                : new TranscriptionWorkerMessage { Devices = [new TranscriptionDevice(cuda ? TranscriptionAccelerationBackend.NvidiaCuda : TranscriptionAccelerationBackend.Vulkan,
                    "pci:gpu", "GPU", TranscriptionDeviceKind.DiscreteGpu)] });
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
