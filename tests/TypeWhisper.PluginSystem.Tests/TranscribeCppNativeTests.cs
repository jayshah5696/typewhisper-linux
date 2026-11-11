using System.Buffers.Binary;
using Moq;
using TypeWhisper.Plugin.TranscribeCpp;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class TranscribeCppNativeTests
{
    [Fact]
    public async Task MetadataAndActivationNeverInitializeNativeRuntimeOrSaveSettings()
    {
        using var plugin = new TranscribeCppPlugin(() => throw new InvalidOperationException("Native code reached the host"));
        var host = new Mock<IPluginHostServices>();
        host.SetupGet(value => value.PluginAssetDirectory).Returns(Path.GetTempPath());
        await plugin.ActivateAsync(host.Object);
        Assert.Equal(59, plugin.TranscriptionModels.Count);
        Assert.False(plugin.AccelerationStatus.IsModelLoaded);
        Assert.Null(plugin.AccelerationStatus.ActiveDevice);
        Assert.NotNull(plugin.HardwareSelection);
        host.Verify(value => value.SetSetting(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task NativeRuntimeRefusesInProcessHostEvenForCpu()
    {
        using var runtime = new TranscribeNativeRuntime();
        await Assert.ThrowsAsync<NotSupportedException>(() => Task.Run(() => runtime.Initialize(TranscriptionAccelerationBackend.Cpu)));
    }

    [Theory]
    [InlineData("vulkan", "0000:64:00.0", "AMD Radeon 780M", 2, TranscriptionDeviceKind.IntegratedGpu)]
    [InlineData("cuda", "0000:01:00.0", "NVIDIA", 1, TranscriptionDeviceKind.DiscreteGpu)]
    [InlineData("cpu", null, "AMD Ryzen", 0, TranscriptionDeviceKind.Cpu)]
    public void DevicesUseBackendAndStableHardwareIdentity(string backend, string? id, string name, int type, TranscriptionDeviceKind kind)
    {
        var device = TranscribeNativeRuntime.DescribeDevice(backend, id, name, type);
        Assert.NotNull(device);
        Assert.Equal(id ?? "cpu", device.StableId);
        Assert.Equal(kind, device.Kind);
        Assert.Equal(name, device.DisplayName);
    }

    [Theory]
    [InlineData("vulkan", null, "Vulkan0", 1)]
    [InlineData("vulkan", "0000:01:00.0", "llvmpipe (LLVM)", 1)]
    [InlineData("vulkan", "0000:01:00.0", "lavapipe", 2)]
    [InlineData("accel", null, "BLAS", 3)]
    [InlineData("unknown", "x", "GPU", 1)]
    public void SoftwareOrUnidentifiableGpuAndHostAcceleratorsCannotBeSelected(string backend, string? id, string name, int type)
    {
        Assert.Null(TranscribeNativeRuntime.DescribeDevice(backend, id, name, type));
    }

    [Fact]
    public void WaveReaderUsesRiffChunksNotAFixed44ByteOffset()
    {
        var wav = Wave([short.MinValue, 0, short.MaxValue], extraChunk: true);
        var pcm = TranscribeAudio.ReadWave(wav);
        Assert.Equal(3, pcm.Length);
        Assert.Equal(-1f, pcm[0]);
        Assert.Equal(0f, pcm[1]);
        Assert.InRange(pcm[2], .999f, 1f);
    }

    [Fact]
    public void InvalidOrMismatchedWaveNeverReachesInference()
    {
        Assert.Throws<InvalidDataException>(() => TranscribeAudio.ReadWave(new byte[44]));
        var stereo = Wave([1, 2]);
        BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(22), 2);
        Assert.Throws<InvalidDataException>(() => TranscribeAudio.ReadWave(stereo));
        var wrongRate = Wave([1, 2]);
        BinaryPrimitives.WriteInt32LittleEndian(wrongRate.AsSpan(24), 48000);
        Assert.Throws<InvalidDataException>(() => TranscribeAudio.ReadWave(wrongRate));
        Assert.Throws<InvalidDataException>(() => TranscribeAudio.ReadWave(Wave([1, 2])[..^1]));
    }

    [Fact]
    public void LongRecordingWindowsCoverEverySampleWithoutOverlapOrWordDeduplication()
    {
        var pcm = Enumerable.Repeat(.5f, 16_000 * 200).ToArray();
        Array.Clear(pcm, 16_000 * 70, 16_000);
        var windows = TranscribeAudio.Windows(pcm, 81_920).ToArray();
        Assert.True(windows.Length >= 3);
        Assert.Equal(pcm.Length, windows.Sum(value => value.Length));
        Assert.All(windows, window => Assert.InRange(window.Length, 1, 16_000 * 80));
        Assert.Equal(pcm, windows.SelectMany(value => value.ToArray()).ToArray());
        Assert.InRange(windows[0].Length, 16_000 * 70, 16_000 * 71);
    }

    [Fact]
    public void UninterruptedSpeechAlsoHasBoundedLosslessAudioWindows()
    {
        var pcm = Enumerable.Repeat(.3f, 16_000 * 170).ToArray();
        var windows = TranscribeAudio.Windows(pcm, 81_920).ToArray();
        Assert.Equal(3, windows.Length);
        Assert.Equal(pcm, windows.SelectMany(value => value.ToArray()).ToArray());
    }

    [Fact]
    public void UnknownNativeStatusIsAnErrorNotDummySuccess()
    {
        Assert.Throws<InvalidOperationException>(() => TranscribeNativeRuntime.CheckStatus(999, "test"));
        Assert.Throws<InvalidOperationException>(() => TranscribeNativeRuntime.CheckCompletion(0, wasTruncated: true, wasAborted: false, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => TranscribeNativeRuntime.CheckCompletion(0, wasTruncated: false, wasAborted: true, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => TranscribeNativeRuntime.CheckCompletion(0, false, false, cancelled.Token));
    }

    internal static byte[] Wave(short[] samples, bool extraChunk = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * 2 + (extraChunk ? 12 : 0));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        if (extraChunk)
        {
            writer.Write("JUNK"u8);
            writer.Write(3);
            writer.Write(new byte[4]);
        }
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples) writer.Write(sample);
        return stream.ToArray();
    }
}
