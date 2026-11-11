using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public class HttpApiAccelerationDtoTests
{
    [Fact]
    public void BuildAccelerationDto_Vulkan_ReportsLoadedBackend()
    {
        var plugin = new FakeAccelerationPlugin
        {
            AccelerationStatus = new TranscriptionAccelerationStatus(TranscriptionAccelerationBackend.Vulkan, "Using Vulkan"),
        };
        var json = JsonSerializer.SerializeToElement(HttpApiService.BuildAccelerationDto(plugin,
            AppSettings.Default with { LocalModelAcceleration = "vulkan" }));
        Assert.Equal("vulkan", json.GetProperty("preference").GetString());
        Assert.Equal("vulkan", json.GetProperty("activeBackend").GetString());
        Assert.Equal("Using Vulkan", json.GetProperty("displayText").GetString());
    }

    [Fact]
    public void BuildAccelerationDto_NoActivePlugin_ReturnsNull()
    {
        var dto = HttpApiService.BuildAccelerationDto(null, AppSettings.Default);

        Assert.Null(dto);
    }

    [Fact]
    public void BuildAccelerationDto_WhisperCppCpuPreference_ReflectsCpuStatus()
    {
        var plugin = new FakeAccelerationPlugin
        {
            AccelerationStatus = new TranscriptionAccelerationStatus(
                TranscriptionAccelerationBackend.Cpu,
                "Using CPU"
            ),
        };
        var settings = AppSettings.Default with
        {
            LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu,
        };

        var dto = HttpApiService.BuildAccelerationDto(plugin, settings);
        var json = JsonSerializer.SerializeToElement(dto);

        Assert.Equal("cpu", json.GetProperty("preference").GetString());
        Assert.Equal("cpu", json.GetProperty("activeBackend").GetString());
        Assert.Equal("Using CPU", json.GetProperty("displayText").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("detail").ValueKind);
        Assert.False(json.GetProperty("requiresRestart").GetBoolean());
    }

    [Fact]
    public void BuildAccelerationDto_AutoPreferenceWithCpuLoaded_IncludesDetail()
    {
        var plugin = new FakeAccelerationPlugin
        {
            AccelerationStatus = new TranscriptionAccelerationStatus(
                TranscriptionAccelerationBackend.Cpu,
                "Using CPU",
                "CUDA not available; falling back to CPU."
            ),
        };
        var settings = AppSettings.Default with
        {
            LocalModelAcceleration = AppSettings.LocalModelAccelerationAuto,
        };

        var dto = HttpApiService.BuildAccelerationDto(plugin, settings);
        var json = JsonSerializer.SerializeToElement(dto);

        Assert.Equal("auto", json.GetProperty("preference").GetString());
        Assert.Equal("cpu", json.GetProperty("activeBackend").GetString());
        Assert.Equal(
            "CUDA not available; falling back to CPU.",
            json.GetProperty("detail").GetString());
    }

    [Fact]
    public void BuildAccelerationDto_RequiresRestart_PropagatesFlag()
    {
        var plugin = new FakeAccelerationPlugin
        {
            AccelerationStatus = new TranscriptionAccelerationStatus(
                TranscriptionAccelerationBackend.Cpu,
                "Using CPU",
                "Process is pinned to CPU. Restart to switch to NVIDIA CUDA.",
                RequiresRestart: true
            ),
        };
        var settings = AppSettings.Default with
        {
            LocalModelAcceleration = AppSettings.LocalModelAccelerationNvidiaCuda,
        };

        var dto = HttpApiService.BuildAccelerationDto(plugin, settings);
        var json = JsonSerializer.SerializeToElement(dto);

        Assert.Equal("nvidia-cuda", json.GetProperty("preference").GetString());
        Assert.Equal("cpu", json.GetProperty("activeBackend").GetString());
        Assert.True(json.GetProperty("requiresRestart").GetBoolean());
    }

    [Fact]
    public void BuildAccelerationDto_Unloaded_DoesNotReportAnActiveBackendOrDevice()
    {
        var plugin = new FakeAccelerationPlugin
        {
            AccelerationStatus = new(TranscriptionAccelerationBackend.Vulkan, "Not loaded")
            {
                IsModelLoaded = false,
                ActiveDevice = new(TranscriptionAccelerationBackend.Vulkan, "pci:old", "Old GPU", TranscriptionDeviceKind.IntegratedGpu),
            },
        };
        var json = JsonSerializer.SerializeToElement(HttpApiService.BuildAccelerationDto(plugin, AppSettings.Default));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("activeBackend").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("activeDevice").ValueKind);
        Assert.False(json.GetProperty("isModelLoaded").GetBoolean());
    }

    [Fact]
    public void BuildAccelerationDto_Loaded_ReportsExactDeviceAndFallbackReasonSeparately()
    {
        var plugin = new FakeAccelerationPlugin
        {
            AccelerationStatus = new(TranscriptionAccelerationBackend.Cpu, "Using CPU")
            {
                IsModelLoaded = true,
                ActiveDevice = new(TranscriptionAccelerationBackend.Cpu, "cpu", "CPU", TranscriptionDeviceKind.Cpu),
                FallbackReason = "Vulkan allocation failed",
            },
        };
        var json = JsonSerializer.SerializeToElement(HttpApiService.BuildAccelerationDto(plugin, AppSettings.Default));
        Assert.Equal("auto", json.GetProperty("preference").GetString());
        Assert.Equal("cpu", json.GetProperty("activeBackend").GetString());
        Assert.Equal("cpu", json.GetProperty("activeDevice").GetProperty("stableId").GetString());
        Assert.Equal("cpu", json.GetProperty("activeDevice").GetProperty("backend").GetString());
        Assert.Equal("Vulkan allocation failed", json.GetProperty("fallbackReason").GetString());
    }

    [Fact]
    public void BuildAccelerationDto_ManualRequestRemainsSeparateFromConfirmedDevice()
    {
        var plugin = new FakeAccelerationPlugin
        {
            AccelerationStatus = new(TranscriptionAccelerationBackend.Vulkan, "Using Vulkan")
            {
                IsModelLoaded = true,
                ActiveDevice = new(TranscriptionAccelerationBackend.Vulkan, "pci:old", "Old GPU", TranscriptionDeviceKind.IntegratedGpu),
            },
        };
        var settings = (AppSettings.Default with { LocalModelAcceleration = "vulkan" })
            .WithLocalModelDevice("fake", "vulkan", "pci:requested");
        var json = JsonSerializer.SerializeToElement(HttpApiService.BuildAccelerationDto(plugin, settings));
        Assert.Equal("pci:requested", json.GetProperty("requestedDeviceStableId").GetString());
        Assert.Equal("pci:old", json.GetProperty("activeDevice").GetProperty("stableId").GetString());
        var auto = JsonSerializer.SerializeToElement(HttpApiService.BuildAccelerationDto(plugin, settings with { LocalModelAcceleration = "auto" }));
        Assert.Equal(JsonValueKind.Null, auto.GetProperty("requestedDeviceStableId").ValueKind);
    }

    private sealed class FakeAccelerationPlugin : ITranscriptionEnginePlugin
    {
        public string PluginId => "fake";
        public string PluginName => "Fake";
        public string PluginVersion => "1.0.0";
        public string ProviderId => "fake";
        public string ProviderDisplayName => "Fake";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = [];
        public string? SelectedModelId => null;
        public bool SupportsTranslation => false;

        public TranscriptionAccelerationStatus AccelerationStatus { get; init; } =
            new(TranscriptionAccelerationBackend.Cpu, "Using CPU");

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void SelectModel(string modelId) { }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct
        ) => Task.FromResult(new PluginTranscriptionResult("", DetectedLanguage: null, 0));

        public void Dispose() { }
    }
}
