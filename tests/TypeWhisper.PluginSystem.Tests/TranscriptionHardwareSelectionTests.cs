using System.Text.Json;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class TranscriptionHardwareSelectionTests
{
    private static readonly TranscriptionDevice s_cpu = new(
        TranscriptionAccelerationBackend.Cpu, "cpu", "CPU", TranscriptionDeviceKind.Cpu);
    private static readonly TranscriptionDevice s_vulkan = new(
        TranscriptionAccelerationBackend.Vulkan, "pci:0000:64:00.0", "Radeon 780M", TranscriptionDeviceKind.IntegratedGpu);

    [Fact]
    public void LegacyMetadata_RemainsUnknownInsteadOfClaimingSupport()
    {
        var model = new PluginModelInfo("old", "Legacy model");

        Assert.Null(model.Languages);
        Assert.Null(model.SupportsStreaming);
        Assert.Null(model.SupportsLanguageDetection);
        Assert.Null(model.ReferenceBenchmarks);
        Assert.Null(((ITranscriptionEngineRole)new LegacyEngine()).HardwareSelection);
    }

    [Fact]
    public void DeviceAndMetadata_RoundTripWithoutNativeHandles()
    {
        var model = new PluginModelInfo("new", "New model")
        {
            Languages = ["en", "hi"], SupportsStreaming = true, SupportsLanguageDetection = false,
            License = "apache-2.0", LicenseUrl = "https://example.org/license",
            ReferenceBenchmarks = [new PluginModelBenchmark("FLEURS", "en", "WER", 3.99,
                "https://example.org/benchmark", "Upstream reference, not a local measurement")],
        };
        var copy = JsonSerializer.Deserialize<PluginModelInfo>(JsonSerializer.Serialize(model))!;
        Assert.Equal(model.Languages, copy.Languages);
        Assert.Equal(model.ReferenceBenchmarks, copy.ReferenceBenchmarks);
        Assert.Equal(s_vulkan, JsonSerializer.Deserialize<TranscriptionDevice>(JsonSerializer.Serialize(s_vulkan)));
    }

    [Fact]
    public void Auto_UsesHardwareVulkanWhenCudaIsNotReady()
    {
        var cuda = new TranscriptionDevice(TranscriptionAccelerationBackend.NvidiaCuda,
            "pci:0000:01:00.0", "NVIDIA", TranscriptionDeviceKind.DiscreteGpu,
            IsAvailable: false, UnavailableReason: "Optional runtime not downloaded");

        var request = TranscriptionHardwareSelector.Resolve(TranscriptionAccelerationPreference.Auto,
            [cuda, s_cpu, s_vulkan]);

        Assert.Equal(TranscriptionAccelerationPreference.Auto, request.RequestedPreference);
        Assert.Equal(s_vulkan, request.Device);
    }

    [Fact]
    public void Auto_PrefersReadyCudaThenDiscreteVulkan()
    {
        var discrete = s_vulkan with { StableId = "pci:0000:01:00.0", Kind = TranscriptionDeviceKind.DiscreteGpu };
        var cuda = discrete with { Backend = TranscriptionAccelerationBackend.NvidiaCuda };
        Assert.Equal(cuda, TranscriptionHardwareSelector.Resolve(TranscriptionAccelerationPreference.Auto,
            [s_cpu, s_vulkan, discrete, cuda]).Device);
        Assert.Equal(discrete, TranscriptionHardwareSelector.Resolve(TranscriptionAccelerationPreference.Auto,
            [s_cpu, s_vulkan, discrete]).Device);
    }

    [Fact]
    public void Auto_ExcludesSoftwareGpuAndUnavailableHardware()
    {
        var software = s_vulkan with { Kind = TranscriptionDeviceKind.SoftwareGpu, DisplayName = "llvmpipe" };
        Assert.Equal(s_cpu, TranscriptionHardwareSelector.Resolve(TranscriptionAccelerationPreference.Auto,
            [software, s_vulkan with { IsAvailable = false }, s_cpu]).Device);
    }

    [Fact]
    public void ExplicitGpu_DoesNotSilentlySelectCpuOrAnotherDevice()
    {
        Assert.Throws<InvalidOperationException>(() => TranscriptionHardwareSelector.Resolve(
            TranscriptionAccelerationPreference.Vulkan, [s_cpu]));
        Assert.Throws<InvalidOperationException>(() => TranscriptionHardwareSelector.Resolve(
            TranscriptionAccelerationPreference.Vulkan, [s_cpu, s_vulkan], "pci:missing"));
        var request = TranscriptionHardwareSelector.Resolve(TranscriptionAccelerationPreference.Vulkan,
            [s_cpu, s_vulkan], s_vulkan.StableId);
        Assert.Equal(TranscriptionAccelerationPreference.Vulkan, request.RequestedPreference);
        Assert.Equal(s_vulkan, request.Device);
    }

    [Fact]
    public void Auto_DoesNotOptIntoOtherBackendFamilies()
    {
        var migraphx = s_vulkan with { Backend = TranscriptionAccelerationBackend.AmdMigraphx };
        var webgpu = s_vulkan with { Backend = TranscriptionAccelerationBackend.WebGpu };
        Assert.Throws<InvalidOperationException>(() => TranscriptionHardwareSelector.Resolve(
            TranscriptionAccelerationPreference.Auto, [migraphx, webgpu]));
        Assert.Equal(migraphx, TranscriptionHardwareSelector.Resolve(
            TranscriptionAccelerationPreference.AmdMigraphx, [migraphx]).Device);
    }

    [Fact]
    public void DeviceIdentity_IsStableAcrossEnumerationOrderAndBackendSpecific()
    {
        var second = s_vulkan with { StableId = "pci:0000:65:00.0" };
        var cuda = s_vulkan with { Backend = TranscriptionAccelerationBackend.NvidiaCuda };
        Assert.Equal(s_vulkan, TranscriptionHardwareSelector.Resolve(
            TranscriptionAccelerationPreference.Vulkan, [second, cuda, s_vulkan], s_vulkan.StableId).Device);
        Assert.Equal(s_vulkan, TranscriptionHardwareSelector.Resolve(
            TranscriptionAccelerationPreference.Vulkan, [s_vulkan, cuda, second], s_vulkan.StableId).Device);
    }

    [Fact]
    public void ExplicitCpu_NeverChoosesGpu()
    {
        Assert.Equal(s_cpu, TranscriptionHardwareSelector.Resolve(TranscriptionAccelerationPreference.Cpu,
            [s_vulkan, s_cpu]).Device);
    }

    [Fact]
    public void UnloadedStatus_DoesNotClaimAnActiveDevice()
    {
        var status = new TranscriptionAccelerationStatus(TranscriptionAccelerationBackend.Cpu, "Not loaded")
        {
            IsModelLoaded = false,
        };
        Assert.Null(status.ActiveDevice);
        Assert.Null(status.FallbackReason);
    }

    private sealed class LegacyEngine : ITranscriptionEngineRole
    {
        public string PluginId => "legacy";
        public string ProviderId => "legacy";
        public string ProviderDisplayName => "Legacy";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => [];
        public string? SelectedModelId => null;
        public bool SupportsTranslation => false;
        public void SelectModel(string modelId) { }
        public Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio, string? language,
            bool translate, string? prompt, CancellationToken ct) => throw new NotSupportedException();
    }
}
