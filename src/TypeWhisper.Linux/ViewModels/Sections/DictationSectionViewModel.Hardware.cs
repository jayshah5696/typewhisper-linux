using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class DictationSectionViewModel
{
    private string? _hardwareModelId;
    private ITranscriptionEngineRole? _hardwareProvider;
    private IReadOnlyList<TranscriptionDevice> _discoveredDevices = [];
    private CancellationTokenSource? _hardwareDiscoveryCts;
    private long _hardwareDiscoveryGeneration;
    private bool _hardwareOptionsQueued;
    private HardwareOptionsState? _hardwareOptionsState;

    private sealed record HardwareOptionsState(string? ModelId, ITranscriptionEngineRole? Provider,
        string Mode, string? SavedId, string Language, IReadOnlyList<TranscriptionDevice> Devices);
    private bool _replacingHardwareOptions;
    private bool _skipHardwareReload;
    private string? _hardwareDiscoveryError;
    private bool _hardwareDiscoveryTimedOut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalModelDeviceDiscoveryText))]
    private bool _isDiscoveringLocalModelDevices;

    public ObservableCollection<LocalModelDeviceOption> LocalModelDeviceOptions { get; } = [];
    public bool ShowLocalModelDevices => SelectedModelPlugin?.HardwareSelection is not null;
    public bool CanRetryLocalModelOnCpu => ShowLocalModelDevices && !IsRecording
        && SelectedModel is { } selected && _models.IsDownloaded(selected.ModelId)
        && _models.GetStatus(selected.ModelId).Type is not (ModelStatusType.Loading or ModelStatusType.Downloading);
    public bool CanSelectLocalModelDevice => ShowLocalModelDevices
        && LocalModelAcceleration is not (AppSettings.LocalModelAccelerationAuto or AppSettings.LocalModelAccelerationCpu);

    public string LocalModelDeviceDiscoveryText => IsDiscoveringLocalModelDevices
        ? Loc.Instance["Dictation.HardwareDiscovering"]
        : _hardwareDiscoveryTimedOut ? Loc.Instance["Dictation.HardwareDiscoveryTimeout"]
        : _hardwareDiscoveryError is { } error ? Loc.Instance.GetString("Dictation.HardwareDiscoveryFailed", error)
        : Loc.Instance["Dictation.HardwareDiscoveryHelp"];

    public LocalModelDeviceOption? SelectedLocalModelDevice
    {
        get
        {
            var saved = SelectedModelPlugin is { } plugin
                ? _settings.Current.GetLocalModelDevice(plugin.GetTranscriptionSelectionId(), LocalModelAcceleration) : null;
            return LocalModelDeviceOptions.FirstOrDefault(option => option.ModelId == SelectedModel?.ModelId
                && option.Backend == LocalModelAcceleration && option.StableId == saved);
        }
        set
        {
            // ComboBox sends null while its collection is replaced. This must not clear a saved choice.
            if (_replacingHardwareOptions || value is null || !CanSelectLocalModelDevice
                || value.ModelId != SelectedModel?.ModelId || value.Backend != LocalModelAcceleration
                || SelectedModelPlugin is not { } plugin)
            {
                return;
            }

            var providerId = plugin.GetTranscriptionSelectionId();
            if (_settings.Current.GetLocalModelDevice(providerId, LocalModelAcceleration) == value.StableId)
            {
                return;
            }

            _settings.Update(current => current.WithLocalModelDevice(providerId, LocalModelAcceleration, value.StableId));
            OnPropertyChanged();
            OnPropertyChanged(nameof(AccelerationStatusText));
            if (_models.ActiveModelId == value.ModelId && SelectedModel is { } selected)
            {
                _ = ReloadActiveModelForAccelerationChangeAsync(selected);
            }
        }
    }

    private void RefreshLocalModelHardware()
    {
        var provider = SelectedModelPlugin;
        if (_hardwareModelId != SelectedModel?.ModelId || !ReferenceEquals(_hardwareProvider, provider))
        {
            _hardwareModelId = SelectedModel?.ModelId;
            _hardwareProvider = provider;
            _hardwareDiscoveryGeneration++;
            _hardwareDiscoveryCts?.Cancel();
            _hardwareDiscoveryCts = null;
            IsDiscoveringLocalModelDevices = false;
            _discoveredDevices = [];
            _hardwareDiscoveryError = null;
            _hardwareDiscoveryTimedOut = false;
        }

        OnPropertyChanged(nameof(ShowLocalModelDevices));
        OnPropertyChanged(nameof(CanRetryLocalModelOnCpu));
        OnPropertyChanged(nameof(CanSelectLocalModelDevice));
        OnPropertyChanged(nameof(LocalModelDeviceDiscoveryText));
        if (_hardwareOptionsQueued || _hardwareOptionsState == CaptureHardwareOptionsState()
            || (!ShowLocalModelDevices && LocalModelDeviceOptions.Count == 0))
        {
            return;
        }

        _hardwareOptionsQueued = true;
        _postToUi(() =>
        {
            _hardwareOptionsQueued = false;
            _hardwareOptionsState = CaptureHardwareOptionsState();
            ReplaceLocalModelDeviceOptions();
        });
    }

    private HardwareOptionsState CaptureHardwareOptionsState() => new(SelectedModel?.ModelId,
        SelectedModelPlugin, LocalModelAcceleration, SelectedModelPlugin is { } provider
            ? _settings.Current.GetLocalModelDevice(provider.GetTranscriptionSelectionId(), LocalModelAcceleration) : null,
        Loc.Instance.CurrentLanguage, _discoveredDevices);

    private void ReplaceLocalModelDeviceOptions()
    {
        _replacingHardwareOptions = true;
        try
        {
            LocalModelDeviceOptions.Clear();
            if (!ShowLocalModelDevices || SelectedModel is not { } model || SelectedModelPlugin is not { } provider)
            {
                return;
            }

            LocalModelDeviceOptions.Add(new(model.ModelId, LocalModelAcceleration, null, Loc.Instance["Dictation.HardwareAutomaticDevice"]));
            if (!CanSelectLocalModelDevice)
            {
                return;
            }

            var saved = _settings.Current.GetLocalModelDevice(provider.GetTranscriptionSelectionId(), LocalModelAcceleration);
            foreach (var device in _discoveredDevices.Where(device =>
                         provider.SupportedAccelerationBackends.Contains(device.Backend)
                         && TranscriptionHardwareSelector.IsEligible(device)
                         && DeviceBackendSetting(device.Backend) == LocalModelAcceleration)
                     .DistinctBy(device => device.StableId))
            {
                LocalModelDeviceOptions.Add(new(model.ModelId, LocalModelAcceleration, device.StableId,
                    $"{device.DisplayName} · {device.StableId}"));
            }

            if (saved is not null && !LocalModelDeviceOptions.Any(option => option.StableId == saved))
            {
                LocalModelDeviceOptions.Add(new(model.ModelId, LocalModelAcceleration, saved,
                    Loc.Instance.GetString("Dictation.HardwareSavedUnavailable", saved)));
            }
        }
        finally
        {
            _replacingHardwareOptions = false;
            OnPropertyChanged(nameof(SelectedLocalModelDevice));
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshLocalModelDevicesAsync()
    {
        if (SelectedModel is not { } model || SelectedModelPlugin?.HardwareSelection is not { } hardware)
        {
            return;
        }

        _hardwareDiscoveryCts?.Cancel();
        using var discovery = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _hardwareDiscoveryCts = discovery;
        var generation = ++_hardwareDiscoveryGeneration;
        IsDiscoveringLocalModelDevices = true;
        _hardwareDiscoveryError = null;
        _hardwareDiscoveryTimedOut = false;
        try
        {
            var devices = await Task.Run(() => hardware.DiscoverDevicesAsync(
                ModelManagerService.ParsePluginModelId(model.ModelId).ModelId, discovery.Token), discovery.Token)
                .WaitAsync(discovery.Token);
            if (generation == _hardwareDiscoveryGeneration && SelectedModel?.ModelId == model.ModelId)
            {
                _discoveredDevices = devices;
            }
        }
        catch (OperationCanceledException) when (discovery.IsCancellationRequested)
        {
            if (generation == _hardwareDiscoveryGeneration) { _hardwareDiscoveryTimedOut = true; }
        }
        catch (Exception ex)
        {
            if (generation == _hardwareDiscoveryGeneration) { _hardwareDiscoveryError = ex.Message; }
        }
        finally
        {
            if (generation == _hardwareDiscoveryGeneration)
            {
                _hardwareDiscoveryCts = null;
                IsDiscoveringLocalModelDevices = false;
                RefreshLocalModelHardware();
            }
        }
    }

    [RelayCommand]
    private async Task RetryLocalModelOnCpuAsync()
    {
        if (!CanRetryLocalModelOnCpu || SelectedModel is not { } selected)
        {
            return;
        }

        _skipHardwareReload = true;
        try { LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu; }
        finally { _skipHardwareReload = false; }
        await ReloadActiveModelForAccelerationChangeAsync(selected);
    }

    private string NativeHardwareStatusText()
    {
        var saved = SelectedModelPlugin is { } provider
            ? _settings.Current.GetLocalModelDevice(provider.GetTranscriptionSelectionId(), LocalModelAcceleration) : null;
        var requested = SelectedAccelerationOption?.DisplayName ?? LocalModelAcceleration;
        if (saved is not null) { requested += " · " + saved; }
        var lines = new List<string> { Loc.Instance.GetString("Dictation.HardwareRequested", requested) };
        if (SelectedModel is { } selected && _models.GetStatus(selected.ModelId).Type is ModelStatusType.Loading or ModelStatusType.Downloading)
        {
            lines.Add(Loc.Instance["Dictation.HardwarePreparing"]);
        }

        var activeStatus = _models.ActiveTranscriptionPlugin?.AccelerationStatus;
        var status = activeStatus ?? SelectedModelPlugin?.AccelerationStatus;
        if (activeStatus?.IsModelLoaded == true)
        {
            var actual = activeStatus.ActiveBackend.ToString();
            if (activeStatus.ActiveDevice is { } device) { actual += $" · {device.DisplayName} · {device.StableId}"; }
            lines.Add(Loc.Instance.GetString("Dictation.HardwareActive", actual));
            if (_models.ActiveModelId != SelectedModel?.ModelId) { lines.Add(ActiveModelLabel); }
        }
        else
        {
            lines.Add(Loc.Instance["Dictation.HardwareUnloaded"]);
        }

        if (!string.IsNullOrWhiteSpace(status?.FallbackReason))
        {
            lines.Add(Loc.Instance.GetString("Dictation.HardwareFallback", status.FallbackReason));
        }
        if (status?.RequiresRestart == true) { lines.Add(Loc.Instance["Dictation.RestartToApply"]); }
        return string.Join(Environment.NewLine, lines);
    }

    private static string DeviceBackendSetting(TranscriptionAccelerationBackend backend) => backend switch
    {
        TranscriptionAccelerationBackend.Cpu => AppSettings.LocalModelAccelerationCpu,
        TranscriptionAccelerationBackend.NvidiaCuda => AppSettings.LocalModelAccelerationNvidiaCuda,
        TranscriptionAccelerationBackend.Vulkan => AppSettings.LocalModelAccelerationVulkan,
        TranscriptionAccelerationBackend.AmdMigraphx => AppSettings.LocalModelAccelerationAmdMigraphx,
        TranscriptionAccelerationBackend.WebGpu => AppSettings.LocalModelAccelerationWebGpu,
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };
}

public sealed record LocalModelDeviceOption(string ModelId, string Backend, string? StableId, string DisplayName);
