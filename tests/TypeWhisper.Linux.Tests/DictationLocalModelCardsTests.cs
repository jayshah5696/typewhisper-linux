using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationLocalModelCardsTests
{
    [Fact]
    public void Cards_ListOnlyDownloadableModelsWithSizeAndState()
    {
        var local = new DownloadableEngine();
        local.Downloaded.Add("small");
        using var context = new CardsTestContext(local, new CloudEngine());

        Assert.Equal(["Small", "Large", "Medium"], context.Sut.LocalModelCards.Select(card => card.DisplayName));
        var small = context.Card("small");
        var large = context.Card("large");
        Assert.Equal($"{Loc.Instance["Dictation.ModelDownloadSize"]}: ~200 MB · Local engine", small.DetailText);
        Assert.Equal(Loc.Instance["Dictation.ModelCardDownloaded"], small.BadgeText);
        Assert.True(small is { ShowUse: true, CanUse: true, ShowRemove: true, ShowDownload: false });
        Assert.Equal(Loc.Instance["Dictation.ModelCardRecommended"], large.BadgeText);
        Assert.True(large is { ShowDownload: true, CanDownload: true, ShowUse: false, ShowRemove: false });
        Assert.True(context.Sut.HasLocalModelCards);
    }

    [Fact]
    public void Toggle_ListsModelsThatAreNeitherDownloadedNorRecommended()
    {
        var local = new DownloadableEngine();
        local.Downloaded.Add("small");
        using var context = new CardsTestContext(local);

        Assert.True(context.Card("small").IsListed);
        Assert.True(context.Card("large").IsListed);
        Assert.False(context.Card("medium").IsListed);
        Assert.True(context.Sut.ShowLocalModelsToggle);
        Assert.Equal(Loc.Instance.GetString("Dictation.ShowAllLocalModels", 3), context.Sut.LocalModelsToggleText);

        context.Sut.ToggleLocalModelsCommand.Execute(null);

        Assert.True(context.Card("medium").IsListed);
        Assert.Equal(Loc.Instance["Dictation.ShowFewerLocalModels"], context.Sut.LocalModelsToggleText);
    }

    [Fact]
    public async Task Download_ShowsProgressAndCancelThenLeavesSelectionAlone()
    {
        var local = new DownloadableEngine { DownloadGate = new TaskCompletionSource() };
        local.Downloaded.Add("small");
        using var context = new CardsTestContext(local);
        var large = context.Card("large");

        var download = context.Sut.DownloadLocalModelCommand.ExecuteAsync(large);
        await local.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        local.LastProgress!.Report(0.25);
        await context.WaitUntilAsync(() => large.PercentText == "25%");

        Assert.Equal(Loc.Instance["Dictation.ModelCardDownloading"], large.BadgeText);
        Assert.True(large is { ShowProgress: true, IsProgressIndeterminate: false, ShowCancel: true, ShowDownload: false });
        Assert.False(context.Card("small").CanDownload);
        Assert.False(context.Card("small").CanRemove);
        Assert.False(context.Sut.CanChangeModelStorage);

        local.DownloadGate.SetResult();
        await download.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Loc.Instance["Dictation.ModelCardDownloaded"], large.BadgeText);
        Assert.True(large is { ShowProgress: false, ShowCancel: false, ShowUse: true, ShowRemove: true });
        Assert.True(context.Sut.CanChangeModelStorage);
        Assert.Null(context.Sut.SelectedModel);
        Assert.Equal(0, local.LoadCount);
        Assert.Equal(Loc.Instance.GetString("Dictation.ModelDownloaded", "Large"), context.Sut.StatusText);
    }

    [Fact]
    public async Task Cancel_StopsDownloadAndRestoresCard()
    {
        var local = new DownloadableEngine { DownloadGate = new TaskCompletionSource() };
        using var context = new CardsTestContext(local);
        var large = context.Card("large");

        var download = context.Sut.DownloadLocalModelCommand.ExecuteAsync(large);
        await local.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await context.WaitUntilAsync(() => large.ShowCancel);
        context.Sut.CancelLocalModelDownloadCommand.Execute(large);
        await download.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Loc.Instance["Dictation.ModelCardRecommended"], large.BadgeText);
        Assert.True(large is { ShowDownload: true, CanDownload: true, ShowProgress: false, ShowCancel: false });
        Assert.DoesNotContain("large", local.Downloaded);
        Assert.Equal(Loc.Instance.GetString("Dictation.ModelDownloadCanceled", "Large"), context.Sut.StatusText);
    }

    [Fact]
    public async Task Use_SelectsAndLoadsTheModel()
    {
        var local = new DownloadableEngine();
        local.Downloaded.Add("small");
        using var context = new CardsTestContext(local);
        var small = context.Card("small");

        context.Sut.UseLocalModelCommand.Execute(small);
        await context.WaitUntilAsync(() => local.LoadCount == 1 && small.IsActive);

        Assert.Equal(small.ModelId, context.Sut.SelectedModel?.ModelId);
        Assert.Equal(small.ModelId, context.Settings.Object.Current.SelectedModelId);
        Assert.Equal(Loc.Instance["Dictation.ModelCardActive"], small.BadgeText);
        Assert.Equal(Loc.Instance["Dictation.ModelCardInUse"], small.UseText);
        Assert.False(small.CanUse);
    }

    [Fact]
    public async Task Remove_DeletesAnotherModelAndKeepsTheSelection()
    {
        var local = new DownloadableEngine();
        local.Downloaded.UnionWith(["small", "large"]);
        using var context = new CardsTestContext(local);
        context.Sut.UseLocalModelCommand.Execute(context.Card("small"));
        await context.WaitUntilAsync(() => context.Card("small").IsActive);
        var large = context.Card("large");

        await context.Sut.DeleteLocalModelAsync(large);

        Assert.DoesNotContain("large", local.Downloaded);
        Assert.True(large.ShowDownload);
        Assert.Equal(context.Card("small").ModelId, context.Sut.SelectedModel?.ModelId);
    }

    [Fact]
    public async Task Download_OfTheSelectedModel_AlsoLoadsIt()
    {
        var local = new DownloadableEngine { DownloadGate = new TaskCompletionSource() };
        using var context = new CardsTestContext(local);
        var large = context.Card("large");
        context.Sut.SelectedModel = context.Sut.ModelOptions.Single(option => option.ModelId == large.ModelId);
        await local.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await context.WaitUntilAsync(() => large.ShowCancel);
        context.Sut.CancelLocalModelDownloadCommand.Execute(large);
        await context.WaitUntilAsync(() => large.ShowDownload);
        local.DownloadGate.SetResult();

        await context.Sut.DownloadLocalModelCommand.ExecuteAsync(large);
        await context.WaitUntilAsync(() => large.IsActive);

        Assert.Equal(1, local.LoadCount);
        Assert.Equal(large.ModelId, context.Models.ActiveModelId);
    }

    [Fact]
    public void UnavailableStorage_ShowsCardsAsNotDownloaded()
    {
        var local = new DownloadableEngine { StorageUnavailable = true };
        local.Downloaded.Add("small");
        using var context = new CardsTestContext(local);

        Assert.True(context.Card("small").ShowDownload);
        Assert.False(context.Card("small").ShowRemove);
    }

    [Fact]
    public async Task DeleteOutsideTheViewModel_UpdatesTheCard()
    {
        var local = new DownloadableEngine();
        local.Downloaded.Add("small");
        using var context = new CardsTestContext(local);
        var small = context.Card("small");
        Assert.True(small.ShowRemove);

        Assert.Equal(ModelDeleteOutcome.Deleted, await context.Models.TryDeleteModelAsync(small.ModelId, CancellationToken.None));
        await context.WaitUntilAsync(() => small.ShowDownload);

        Assert.False(small.ShowRemove);
        Assert.Equal(Loc.Instance["Dictation.ModelCardNotDownloaded"], small.BadgeText);
    }

    [Fact]
    public void Search_ListsHiddenMatchesAndIncludesProviderAndIdentityWithoutSavingSettings()
    {
        using var context = new CardsTestContext(new DownloadableEngine());
        context.Settings.Invocations.Clear();
        var cards = context.Sut.LocalModelCards.ToArray();

        context.Sut.LocalModelSearch = "  medium  ";
        Assert.Equal(["Medium"], cards.Where(card => card.IsListed).Select(card => card.DisplayName));
        context.Sut.LocalModelSearch = "LOCAL ENGINE";
        Assert.All(cards, card => Assert.True(card.IsListed));
        context.Sut.LocalModelSearch = "com.test.local:small";
        Assert.Equal(["Small"], cards.Where(card => card.IsListed).Select(card => card.DisplayName));
        context.Sut.LocalModelSearch = "";
        Assert.False(context.Card("medium").IsListed);
        Assert.Equal(cards, context.Sut.LocalModelCards);
        context.Settings.Verify(settings => settings.Update(It.IsAny<Func<AppSettings, AppSettings>>()), Times.Never);
    }

    [Fact]
    public void LanguageFilters_RequireEveryLanguageAndRejectUnknownMetadata()
    {
        using var context = new CardsTestContext(new DownloadableEngine());
        context.Sut.ShowAllLocalModels = true;
        var languages = context.Sut.LocalModelLanguageFilters.ToArray();
        languages.Single(option => option.Code == "en").IsSelected = true;
        Assert.Equal(["Small", "Large"], context.Sut.LocalModelCards.Where(card => card.IsListed).Select(card => card.DisplayName));
        languages.Single(option => option.Code == "de").IsSelected = true;
        Assert.Equal(["Large"], context.Sut.LocalModelCards.Where(card => card.IsListed).Select(card => card.DisplayName));
        Assert.Equal(languages, context.Sut.LocalModelLanguageFilters);
        Assert.Equal("auto", context.Sut.Language);
        context.Sut.ClearLocalModelFiltersCommand.Execute(null);
        Assert.All(languages, option => Assert.False(option.IsSelected));
        Assert.All(context.Sut.LocalModelCards, card => Assert.True(card.IsListed));
    }

    [Fact]
    public async Task Filters_KeepSelectedAndDownloadingCardsReachable()
    {
        var local = new DownloadableEngine { DownloadGate = new TaskCompletionSource() };
        local.Downloaded.Add("small");
        using var context = new CardsTestContext(local);
        context.Sut.UseLocalModelCommand.Execute(context.Card("small"));
        await context.WaitUntilAsync(() => context.Card("small").IsActive);
        var download = context.Sut.DownloadLocalModelCommand.ExecuteAsync(context.Card("large"));
        await local.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await context.WaitUntilAsync(() => context.Card("large").ShowCancel);

        context.Sut.LocalModelSearch = "no match";
        context.Sut.LivePreviewModelsOnly = true;
        Assert.True(context.Card("small").IsListed);
        Assert.True(context.Card("large").IsListed);
        Assert.False(context.Card("medium").IsListed);
        context.Sut.CancelLocalModelDownloadCommand.Execute(context.Card("large"));
        await download.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(context.Card("large").IsListed);
        Assert.True(context.Card("small").IsListed);
    }

    [Fact]
    public void LiveFilter_RequiresKnownPerModelSupport()
    {
        using var context = new CardsTestContext(new DownloadableEngine());
        context.Sut.LivePreviewModelsOnly = true;
        Assert.Equal(["Large"], context.Sut.LocalModelCards.Where(card => card.IsListed).Select(card => card.DisplayName));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("zh")]
    public async Task LanguageChange_PreservesFiltersAndOperationProgressWithoutSaving(string language)
    {
        var originalLanguage = Loc.Instance.CurrentLanguage;
        try
        {
            Loc.Instance.CurrentLanguage = "en";
            var local = new DownloadableEngine { DownloadGate = new TaskCompletionSource() };
            using var context = new CardsTestContext(local);
            context.Sut.LocalModelSearch = "Large";
            context.Sut.LocalModelLanguageFilters.Single(option => option.Code == "en").IsSelected = true;
            var large = context.Card("large");
            var download = context.Sut.DownloadLocalModelCommand.ExecuteAsync(large);
            await local.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            local.LastProgress!.Report(0.25);
            await context.WaitUntilAsync(() => large.PercentText == "25%");
            var options = context.Sut.LocalModelLanguageFilters.ToArray();
            context.Settings.Invocations.Clear();

            Loc.Instance.CurrentLanguage = language;
            Assert.Equal(options, context.Sut.LocalModelLanguageFilters);
            await context.WaitUntilAsync(() => !ReferenceEquals(options[0], context.Sut.LocalModelLanguageFilters[0]));
            Assert.True(context.Sut.LocalModelLanguageFilters.Single(option => option.Code == "en").IsSelected);
            Assert.Equal("Large", context.Sut.LocalModelSearch);
            Assert.Equal("25%", large.PercentText);
            Assert.True(large.ShowCancel);
            Assert.Equal(Loc.Instance["Dictation.ModelCardDownloading"], large.BadgeText);
            Assert.Contains(Loc.Instance["Dictation.ModelMetadataYes"], large.CapabilityText);
            Assert.Equal(Loc.Instance.GetString("Dictation.ModelFilterResults", 1, 3), context.Sut.LocalModelResultsText);
            context.Settings.Verify(settings => settings.Update(It.IsAny<Func<AppSettings, AppSettings>>()), Times.Never);
            context.Sut.CancelLocalModelDownloadCommand.Execute(large);
            await download.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Loc.Instance.CurrentLanguage = originalLanguage;
        }
    }

    [Fact]
    public void Metadata_ShowsDownloadSizeTermsAndReferenceProvenanceAndBlocksUnavailableActions()
    {
        var local = new DownloadableEngine
        {
            TranscriptionModels =
            [
                new("restricted", "Restricted")
                {
                    SizeDescription = "400 MiB", Languages = ["en"], SupportsStreaming = false,
                    SupportsTranslation = true, License = "Publisher terms", LicenseUrl = "https://example.org/terms",
                    UnavailableReason = "Terms need review",
                    ReferenceBenchmarks = [new("LibriSpeech", "en", "WER", 4.2, "https://example.org/results", "Publisher CPU run")],
                },
            ],
        };
        local.Downloaded.Add("restricted");
        using var context = new CardsTestContext(local);
        var card = context.Card("restricted");
        Assert.Contains("400 MiB", card.DetailText);
        Assert.Contains(Loc.Instance["Dictation.ModelDownloadSize"], card.DetailText);
        Assert.Contains("Terms need review", card.StateText);
        Assert.Contains("Publisher terms", card.LicenseText);
        Assert.Equal("https://example.org/terms", card.LicenseUrl);
        Assert.Contains("LibriSpeech", card.ReferenceBenchmarkText);
        Assert.Contains("WER", card.ReferenceBenchmarkText);
        Assert.Contains("https://example.org/results", card.ReferenceBenchmarkText);
        Assert.Contains("Publisher CPU run", card.ReferenceBenchmarkText);
        Assert.False(card.CanUse);
        Assert.False(card.CanDownload);
        Assert.True(card.CanRemove);
    }

    [Fact]
    public async Task Devices_OpeningAndChangingModeNeverProbesOrSavesAndRefreshIsExplicit()
    {
        var hardware = new HardwareProbe();
        var local = new DownloadableEngine { HardwareSelection = hardware };
        var settings = new AppSettings { SelectedModelId = ModelManagerService.GetPluginModelId(local.PluginId, "small") }
            .WithLocalModelDevice(local.PluginId, "vulkan", "pci:saved");
        using var context = new CardsTestContext(settings, local);
        context.Settings.Invocations.Clear();
        Assert.True(context.Sut.ShowLocalModelDevices);
        Assert.Equal(0, hardware.Calls);
        context.Sut.LocalModelAcceleration = AppSettings.LocalModelAccelerationVulkan;
        await context.WaitUntilAsync(() => context.Sut.LocalModelDeviceOptions.Any(option => option.StableId == "pci:saved"));
        Assert.Equal(AppSettings.LocalModelAccelerationVulkan, context.Settings.Object.Current.LocalModelAcceleration);
        Assert.Equal("pci:saved", context.Sut.SelectedLocalModelDevice?.StableId);
        Assert.Equal(0, hardware.Calls);
        context.Settings.Invocations.Clear();

        await context.Sut.RefreshLocalModelDevicesCommand.ExecuteAsync(null);
        await context.WaitUntilAsync(() => context.Sut.LocalModelDeviceOptions.Any(option => option.StableId == "pci:gpu"));
        Assert.Equal(1, hardware.Calls);
        Assert.DoesNotContain(context.Sut.LocalModelDeviceOptions, option => option.StableId == "software");
        Assert.DoesNotContain(context.Sut.LocalModelDeviceOptions, option => option.StableId == "unsupported");
        Assert.Contains(context.Sut.LocalModelDeviceOptions, option => option.StableId == "pci:saved");
        context.Settings.Verify(s => s.Update(It.IsAny<Func<AppSettings, AppSettings>>()), Times.Never);

        var options = context.Sut.LocalModelDeviceOptions.ToArray();
        context.Sut.SelectedLocalModelDevice = options.Single(option => option.StableId == "pci:gpu");
        Assert.Equal(options, context.Sut.LocalModelDeviceOptions);
        Assert.Equal("pci:gpu", context.Settings.Object.Current.GetLocalModelDevice(local.PluginId, "vulkan"));
        Assert.Equal(0, local.LoadCount);
        Assert.Null(context.Models.ActiveModelId);
    }

    [Fact]
    public async Task DeviceChoicesAndModelSelectorSurviveUnchangedProviderNotifications()
    {
        var local = new DownloadableEngine { HardwareSelection = new HardwareProbe() };
        var settings = new AppSettings { SelectedModelId = ModelManagerService.GetPluginModelId(local.PluginId, "small"), LocalModelAcceleration = "vulkan" }
            .WithLocalModelDevice(local.PluginId, "vulkan", "pci:gpu");
        using var context = new CardsTestContext(settings, local);
        await context.Sut.RefreshLocalModelDevicesCommand.ExecuteAsync(null);
        await context.WaitUntilAsync(() => context.Sut.LocalModelDeviceOptions.Any(option => option.StableId == "pci:gpu"));
        var models = context.Sut.ModelOptions.ToArray();
        var devices = context.Sut.LocalModelDeviceOptions.ToArray();
        context.Settings.Invocations.Clear();
        context.NotifyPluginStateChanged();
        await context.WaitUntilAsync(() => true);
        Assert.Same(models[0], context.Sut.ModelOptions[0]);
        Assert.Same(devices[0], context.Sut.LocalModelDeviceOptions[0]);
        Assert.Equal("pci:gpu", context.Sut.SelectedLocalModelDevice?.StableId);
        context.Settings.Verify(s => s.Update(It.IsAny<Func<AppSettings, AppSettings>>()), Times.Never);
    }

    [Fact]
    public async Task Devices_LateRefreshCannotReplaceChoicesForAnotherModel()
    {
        var hardware = new HardwareProbe { Gate = new TaskCompletionSource<IReadOnlyList<TranscriptionDevice>>() };
        var local = new DownloadableEngine { HardwareSelection = hardware };
        var settings = new AppSettings { SelectedModelId = ModelManagerService.GetPluginModelId(local.PluginId, "small"), LocalModelAcceleration = "vulkan" };
        using var context = new CardsTestContext(settings, local);
        var refresh = context.Sut.RefreshLocalModelDevicesCommand.ExecuteAsync(null);
        await context.WaitUntilAsync(() => hardware.Calls == 1);
        context.Sut.SelectedModel = null;
        hardware.Gate.SetResult(HardwareProbe.Devices);
        await refresh;
        await context.WaitUntilAsync(() => !context.Sut.IsDiscoveringLocalModelDevices);
        Assert.False(context.Sut.ShowLocalModelDevices);
        Assert.DoesNotContain(context.Sut.LocalModelDeviceOptions, option => option.StableId == "pci:gpu");
        Assert.Equal(0, local.LoadCount);
    }

    [Fact]
    public async Task Devices_LanguageChangeDefersChoicesAndKeepsSavedIdentityWithoutSaving()
    {
        var original = Loc.Instance.CurrentLanguage;
        try
        {
            Loc.Instance.CurrentLanguage = "en";
            var local = new DownloadableEngine { HardwareSelection = new HardwareProbe() };
            var settings = new AppSettings { SelectedModelId = ModelManagerService.GetPluginModelId(local.PluginId, "small"), LocalModelAcceleration = "vulkan" }
                .WithLocalModelDevice(local.PluginId, "vulkan", "pci:gpu");
            using var context = new CardsTestContext(settings, local);
            await context.Sut.RefreshLocalModelDevicesCommand.ExecuteAsync(null);
            await context.WaitUntilAsync(() => context.Sut.LocalModelDeviceOptions.Any(option => option.StableId == "pci:gpu"));
            var options = context.Sut.LocalModelDeviceOptions.ToArray();
            context.Settings.Invocations.Clear();
            Loc.Instance.CurrentLanguage = "de";
            Assert.Equal(options, context.Sut.LocalModelDeviceOptions);
            await context.WaitUntilAsync(() => !ReferenceEquals(options[0], context.Sut.LocalModelDeviceOptions[0]));
            Assert.Equal("pci:gpu", context.Sut.SelectedLocalModelDevice?.StableId);
            context.Settings.Verify(s => s.Update(It.IsAny<Func<AppSettings, AppSettings>>()), Times.Never);
        }
        finally { Loc.Instance.CurrentLanguage = original; }
    }

    [Fact]
    public async Task HardwareStatus_ReportsRequestedUnloadedThenActualDeviceAndFallbackThenUnloaded()
    {
        var local = new DownloadableEngine { HardwareSelection = new HardwareProbe() };
        local.Downloaded.Add("small");
        var settings = new AppSettings { SelectedModelId = ModelManagerService.GetPluginModelId(local.PluginId, "small"), LocalModelAcceleration = "cpu" };
        using var context = new CardsTestContext(settings, local);
        Assert.Contains(Loc.Instance["Dictation.HardwareUnloaded"], context.Sut.AccelerationStatusText);
        Assert.DoesNotContain(Loc.Instance["Dictation.AccelCpuActive"], context.Sut.AccelerationStatusText);
        await context.Models.EnsureModelLoadedAsync(settings.SelectedModelId);
        local.AccelerationStatus = new(TranscriptionAccelerationBackend.Vulkan, "Vulkan")
        {
            IsModelLoaded = true,
            ActiveDevice = HardwareProbe.Devices[1],
            FallbackReason = "original GPU failure",
        };
        Assert.Contains("Native GPU", context.Sut.AccelerationStatusText);
        Assert.Contains("pci:gpu", context.Sut.AccelerationStatusText);
        Assert.Contains("original GPU failure", context.Sut.AccelerationStatusText);
        local.AccelerationStatus = local.AccelerationStatus with { IsModelLoaded = false };
        Assert.Contains(Loc.Instance["Dictation.HardwareUnloaded"], context.Sut.AccelerationStatusText);
        Assert.DoesNotContain("Native GPU", context.Sut.AccelerationStatusText);
        await context.Models.UnloadModelAsync();
        Assert.Contains("original GPU failure", context.Sut.AccelerationStatusText);
        Assert.DoesNotContain("Native GPU", context.Sut.AccelerationStatusText);
    }

    [Fact]
    public async Task HardwareCpuRetry_LoadsSameDownloadedModelEvenWhenNothingIsActive()
    {
        var probe = new HardwareProbe { Fault = new IOException("broken driver") };
        var local = new DownloadableEngine { HardwareSelection = probe };
        local.Downloaded.Add("small");
        var settings = new AppSettings { SelectedModelId = ModelManagerService.GetPluginModelId(local.PluginId, "small"), LocalModelAcceleration = "vulkan" };
        using var context = new CardsTestContext(settings, local);
        context.Settings.Invocations.Clear();
        await context.Sut.RefreshLocalModelDevicesCommand.ExecuteAsync(null);
        Assert.Contains("broken driver", context.Sut.LocalModelDeviceDiscoveryText);
        Assert.Equal("vulkan", context.Sut.LocalModelAcceleration);
        Assert.Equal(0, local.LoadCount);
        context.Settings.Verify(s => s.Update(It.IsAny<Func<AppSettings, AppSettings>>()), Times.Never);

        await context.Sut.RetryLocalModelOnCpuCommand.ExecuteAsync(null);
        Assert.Equal(1, local.LoadCount);
        Assert.Equal(settings.SelectedModelId, context.Models.ActiveModelId);
        Assert.Equal(settings.SelectedModelId, context.Settings.Object.Current.SelectedModelId);
        Assert.Equal("cpu", context.Settings.Object.Current.LocalModelAcceleration);
        Assert.Equal(1, probe.Calls);
    }

    private sealed class HardwareProbe : ITranscriptionHardwareSelection
    {
        public static IReadOnlyList<TranscriptionDevice> Devices { get; } =
        [
            new(TranscriptionAccelerationBackend.Cpu, "cpu", "CPU", TranscriptionDeviceKind.Cpu),
            new(TranscriptionAccelerationBackend.Vulkan, "pci:gpu", "Native GPU", TranscriptionDeviceKind.IntegratedGpu),
            new(TranscriptionAccelerationBackend.Vulkan, "software", "llvmpipe", TranscriptionDeviceKind.SoftwareGpu),
            new(TranscriptionAccelerationBackend.NvidiaCuda, "unsupported", "CUDA", TranscriptionDeviceKind.DiscreteGpu),
        ];
        public int Calls { get; private set; }
        public Exception? Fault { get; init; }
        public TaskCompletionSource<IReadOnlyList<TranscriptionDevice>>? Gate { get; init; }
        public Task<IReadOnlyList<TranscriptionDevice>> DiscoverDevicesAsync(string modelId, CancellationToken ct)
        {
            Calls++;
            if (Fault is not null) { throw Fault; }
            return Gate?.Task ?? Task.FromResult(Devices);
        }
        public void ConfigureHardware(TranscriptionHardwareRequest request) { }
    }

    private sealed class CardsTestContext : IDisposable
    {
        private readonly ConcurrentQueue<Action> _pending = new();
        private readonly AudioRecordingService _audio;
        private readonly PluginManager _pluginManager;

        public CardsTestContext(params ITranscriptionEngineRole[] engines) : this(AppSettings.Default with { SelectedModelId = null }, engines) { }

        public CardsTestContext(AppSettings settings, params ITranscriptionEngineRole[] engines)
        {
            Settings = TestPluginManagerFactory.CreateSettings(settings);
            _pluginManager = TestPluginManagerFactory.Create();
            PluginManagerTestAccess.SetTranscriptionEngines(_pluginManager, engines);
            var commands = new SystemCommandAvailabilityService(new FakeProcessRunner());
            Models = new ModelManagerService(_pluginManager, Settings.Object, commands)
            {
                CudaRuntimePreflight = () => (false, "CUDA not available in test"),
            };
            var devices = new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true));
            _audio = new AudioRecordingService(
                devices.GetDevices, static _ => { }, static () => 0, static () => { },
                postToUiThread: static _ => { });
            // ReSharper disable once InconsistentNaming -- "a11y" is the standard numeronym for accessibility.
            var a11yBus = new Mock<IAccessibilityBusActivation>();
            a11yBus.Setup(bus => bus.IsActivatedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
            Sut = new DictationSectionViewModel(
                (DictationOrchestrator)RuntimeHelpers.GetUninitializedObject(typeof(DictationOrchestrator)),
                Models,
                _audio,
                Settings.Object,
                _pluginManager,
                commands,
                new CudaLibraryPathSetupService(),
                a11yBus.Object,
                devices.GetDevices,
                dispatchNotification: _pending.Enqueue);
        }

        public DictationSectionViewModel Sut { get; }
        public ModelManagerService Models { get; }
        public Mock<ISettingsService> Settings { get; }

        public void NotifyPluginStateChanged() =>
            ((EventHandler?)typeof(PluginManager).GetField("PluginStateChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(_pluginManager))?.Invoke(_pluginManager, EventArgs.Empty);

        public LocalModelCard Card(string modelId) =>
            Sut.LocalModelCards.Single(card => card.ModelId.EndsWith(":" + modelId, StringComparison.Ordinal));

        public async Task WaitUntilAsync(Func<bool> predicate)
        {
            for (var attempt = 0; attempt < 500; attempt++)
            {
                while (_pending.TryDequeue(out var job))
                {
                    job();
                }

                if (predicate())
                {
                    return;
                }

                await Task.Delay(10);
            }

            Assert.True(predicate(), "Condition was not reached before the test deadline.");
        }

        public void Dispose()
        {
            _audio.Dispose();
            Models.Dispose();
            _pluginManager.Dispose();
        }
    }

    private sealed class DownloadableEngine : ITranscriptionEngineRole
    {
        public HashSet<string> Downloaded { get; } = [];
        public TaskCompletionSource? DownloadGate { get; init; }
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<double>? LastProgress { get; private set; }
        public int LoadCount { get; private set; }

        public string PluginId => "com.test.local";
        public string ProviderId => "local";
        public string ProviderDisplayName => "Local engine";
        public bool IsConfigured => true;
        public bool SupportsModelDownload => true;
        public bool SupportsTranslation => false;
        public string? SelectedModelId { get; private set; }

        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; init; } =
        [
            new("small", "Small") { SizeDescription = "~200 MB", Languages = ["en"], SupportsStreaming = false },
            new("large", "Large") { IsRecommended = true, Languages = ["en", "de"], SupportsStreaming = true },
            new("medium", "Medium"),
        ];

        public bool StorageUnavailable { get; init; }
        public ITranscriptionHardwareSelection? HardwareSelection { get; init; }
        public TranscriptionAccelerationStatus AccelerationStatus { get; set; } = new(TranscriptionAccelerationBackend.Cpu, "CPU") { IsModelLoaded = false };
        public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends =>
            [TranscriptionAccelerationBackend.Cpu, TranscriptionAccelerationBackend.Vulkan];

        public bool IsModelDownloaded(string modelId)
        {
            if (StorageUnavailable)
            {
                throw new IOException("The model folder is not available.");
            }

            lock (Downloaded)
            {
                return Downloaded.Contains(modelId);
            }
        }

        public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
        {
            LastProgress = progress;
            DownloadStarted.TrySetResult();
            if (DownloadGate is not null)
            {
                await DownloadGate.Task.WaitAsync(ct);
            }

            lock (Downloaded)
            {
                Downloaded.Add(modelId);
            }
        }

        public Task DeleteModelAsync(string modelId, CancellationToken ct)
        {
            lock (Downloaded)
            {
                Downloaded.Remove(modelId);
            }

            return Task.CompletedTask;
        }

        public Task LoadModelAsync(string modelId, CancellationToken ct)
        {
            LoadCount++;
            SelectedModelId = modelId;
            return Task.CompletedTask;
        }

        public void SelectModel(string modelId) => SelectedModelId = modelId;

        public Task UnloadModelAsync() => Task.CompletedTask;

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class CloudEngine : ITranscriptionEngineRole
    {
        public string PluginId => "com.test.cloud";
        public string ProviderId => "cloud";
        public string ProviderDisplayName => "Cloud engine";
        public bool IsConfigured => true;
        public bool SupportsTranslation => false;
        public string? SelectedModelId => null;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = [new("cloud-1", "Cloud one")];

        public void SelectModel(string modelId) { }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
