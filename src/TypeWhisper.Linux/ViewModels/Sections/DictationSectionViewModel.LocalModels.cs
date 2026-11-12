using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.ViewModels.Sections;

// MVVM Toolkit [ObservableProperty] generates the On<Property>Changed(value) partial hooks; the
// value parameter is part of the generated signature and cannot be dropped even when ignored here.
// ReSharper disable UnusedParameterInPartialMethod
public partial class DictationSectionViewModel
{
    public ObservableCollection<LocalModelCard> LocalModelCards { get; } = [];

    public bool HasLocalModelCards => LocalModelCards.Count > 0;
    public ObservableCollection<LocalModelLanguageFilter> LocalModelLanguageFilters { get; } = [];

    [ObservableProperty]
    private string _localModelSearch = "";

    [ObservableProperty]
    private bool _livePreviewModelsOnly;

    public bool HasLocalModelFilters => !string.IsNullOrWhiteSpace(LocalModelSearch)
        || LivePreviewModelsOnly || LocalModelLanguageFilters.Any(option => option.IsSelected);

    public string LocalModelResultsText => Loc.Instance.GetString("Dictation.ModelFilterResults",
        LocalModelCards.Count(card => card.IsListed), LocalModelCards.Count);

    partial void OnLocalModelSearchChanged(string value) => RefreshLocalModelCards(checkDisk: false);
    partial void OnLivePreviewModelsOnlyChanged(bool value) => RefreshLocalModelCards(checkDisk: false);

    [RelayCommand]
    private void ClearLocalModelFilters()
    {
        LocalModelSearch = "";
        LivePreviewModelsOnly = false;
        foreach (var option in LocalModelLanguageFilters)
            option.IsSelected = false;
    }

    private void RefreshLocalModelLanguageFilters()
    {
        var selected = LocalModelLanguageFilters.Where(option => option.IsSelected)
            .Select(option => option.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var option in LocalModelLanguageFilters)
            option.PropertyChanged -= OnLocalModelLanguageFilterChanged;
        LocalModelLanguageFilters.Clear();
        var codes = LocalModelCards.SelectMany(card => card.Metadata.Languages ?? [])
            .Concat(selected).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var option in codes.Select(code => new LocalModelLanguageFilter(code, GetLanguageDisplayName(code) ?? code))
            .OrderBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            option.IsSelected = selected.Contains(option.Code);
            option.PropertyChanged += OnLocalModelLanguageFilterChanged;
            LocalModelLanguageFilters.Add(option);
        }
    }

    private void OnLocalModelLanguageFilterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalModelLanguageFilter.IsSelected))
            RefreshLocalModelCards(checkDisk: false);
    }

    private bool MatchesLocalModelFilters(LocalModelCard card)
    {
        var search = LocalModelSearch.Trim();
        return (search.Length == 0
                || card.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || card.EngineName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || card.ModelId.Contains(search, StringComparison.OrdinalIgnoreCase))
            && (!LivePreviewModelsOnly || card.Metadata.SupportsStreaming == true)
            && LocalModelLanguageFilters.Where(option => option.IsSelected).All(option =>
                card.Metadata.Languages?.Contains(option.Code, StringComparer.OrdinalIgnoreCase) == true);
    }

    // The full catalog is long; by default only models the user has or is advised to get are listed.
    [ObservableProperty]
    private bool _showAllLocalModels;

    public bool ShowLocalModelsToggle => !HasLocalModelFilters
        && (ShowAllLocalModels || LocalModelCards.Any(card => !card.IsListed));

    public string LocalModelsToggleText => ShowAllLocalModels
        ? Loc.Instance["Dictation.ShowFewerLocalModels"]
        : Loc.Instance.GetString("Dictation.ShowAllLocalModels", LocalModelCards.Count);

    partial void OnShowAllLocalModelsChanged(bool value)
    {
        RefreshLocalModelCards(checkDisk: false);
    }

    [RelayCommand]
    private void ToggleLocalModels()
    {
        ShowAllLocalModels = !ShowAllLocalModels;
    }

    // Moving the model folder under a running download or load would split or pull its files.
    public bool CanChangeModelStorage =>
        !IsRecording && !IsDownloadingCudaRuntime && !IsClearingGpuRuntime && !IsMigratingModelStorage
        && !HasLocalModelOperation && !_models.IsAnyModelDownloading && !LocalModelCards.Any(card => card.WasBusy);

    partial void OnIsMigratingModelStorageChanged(bool value)
    {
        RefreshLocalModelCards(checkDisk: false);
    }

    private void RebuildLocalModelCards()
    {
        var previous = LocalModelCards.ToDictionary(card => card.ModelId);
        LocalModelCards.Clear();
        foreach (var engine in _pluginManager.TranscriptionEngines.Where(engine => engine.SupportsModelDownload))
        {
            foreach (var model in engine.TranscriptionModels)
            {
                var id = ModelManagerService.GetPluginModelId(engine.GetTranscriptionSelectionId(), model.Id);
                LocalModelCards.Add(previous.TryGetValue(id, out var existing)
                    && existing.Metadata == model && existing.EngineName == engine.ProviderDisplayName
                    ? existing : new LocalModelCard(id, model.DisplayName, engine.ProviderDisplayName,
                        model.SizeDescription, model.IsRecommended, model));
            }
        }

        RefreshLocalModelLanguageFilters();
        OnPropertyChanged(nameof(HasLocalModelCards));
        RefreshLocalModelCards(checkDisk: true);
    }

    /// <summary>
    ///     Re-reads every card. Progress ticks pass <paramref name="checkDisk" /> false, so only a
    ///     card whose recorded status changed (an operation ended, or a delete elsewhere) looks at
    ///     its files again.
    /// </summary>
    private void RefreshLocalModelCards(bool checkDisk)
    {
        var selectedId = SelectedModel?.ModelId;
        var anyDownloading = _models.IsAnyModelDownloading;
        var statuses = LocalModelCards.ToDictionary(card => card, card => _models.GetTrackedStatus(card.ModelId));
        // A selection downloading or loading holds the model lock, so a removal would wait behind it.
        var anyBusy = statuses.Values.Any(IsBusy);
        foreach (var card in LocalModelCards)
        {
            var status = statuses[card];
            var busy = IsBusy(status);
            if (!busy && (checkDisk || card.LastStatusType != status?.Type))
            {
                card.Downloaded = IsDownloadedOnDisk(card.ModelId);
                card.LinkedPath = _models.GetLinkedModelPath(card.ModelId);
            }

            card.LastStatusType = status?.Type;
            card.WasBusy = busy;
            var selected = card.ModelId == selectedId;
            card.IsListed = busy || card.IsTesting || card.IsLinking || selected || (MatchesLocalModelFilters(card)
                && (HasLocalModelFilters || ShowAllLocalModels || card.Downloaded || card.HasLink || card.IsRecommended));
            ApplyCardState(card, status, busy, selected, anyDownloading, anyBusy);
        }

        OnPropertyChanged(nameof(CanChangeModelStorage));
        OnPropertyChanged(nameof(CanDeleteSelectedModel));
        OnPropertyChanged(nameof(SelectedModelRemoveText));
        OnPropertyChanged(nameof(ShowLocalModelsToggle));
        OnPropertyChanged(nameof(LocalModelsToggleText));
        OnPropertyChanged(nameof(HasLocalModelFilters));
        OnPropertyChanged(nameof(LocalModelResultsText));
    }

    // An unavailable model folder or a deactivated plugin throws; show the model as not downloaded.
    private bool IsDownloadedOnDisk(string modelId)
    {
        try
        {
            return _models.IsDownloaded(modelId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsBusy(ModelStatus? status) =>
        status?.Type is ModelStatusType.Downloading or ModelStatusType.Loading;

    private void ApplyCardState(
        LocalModelCard card,
        ModelStatus? status,
        bool busy,
        bool selected,
        bool anyDownloading,
        bool anyBusy)
    {
        var downloading = status?.Type == ModelStatusType.Downloading;
        var failed = status?.Type == ModelStatusType.Error;
        var downloaded = card.Downloaded;

        card.IsFailed = failed;
        card.IsActive = selected && downloaded && !busy && !failed;
        card.BadgeText = status?.Type switch
        {
            ModelStatusType.Downloading => Loc.Instance["Dictation.ModelCardDownloading"],
            ModelStatusType.Loading => Loc.Instance["Dictation.ModelCardLoading"],
            ModelStatusType.Error => Loc.Instance["Dictation.ModelCardFailed"],
            _ when selected && downloaded => Loc.Instance["Dictation.ModelCardActive"],
            _ when downloaded => Loc.Instance["Dictation.ModelCardDownloaded"],
            _ when card.IsRecommended => Loc.Instance["Dictation.ModelCardRecommended"],
            ModelStatusType.NotDownloaded or ModelStatusType.Ready or null or _ => Loc.Instance["Dictation.ModelCardNotDownloaded"],
        };
        card.StateText = status?.Type switch
        {
            ModelStatusType.Downloading => Loc.Instance["Dictation.ModelCardStateDownloading"],
            ModelStatusType.Loading => Loc.Instance["Dictation.ModelCardStateLoading"],
            ModelStatusType.Error => FormatModelStatusError(status.ErrorMessage),
            _ when selected && downloaded => Loc.Instance["Dictation.ModelCardStateSelected"],
            _ when downloaded => Loc.Instance["Dictation.ModelCardStateDownloaded"],
            ModelStatusType.NotDownloaded or ModelStatusType.Ready or null or _ => Loc.Instance["Dictation.ModelCardStateNotDownloaded"],
        };

        if (!string.IsNullOrWhiteSpace(card.Metadata.UnavailableReason) && !busy)
            card.StateText = card.Metadata.UnavailableReason;

        card.ShowProgress = busy;
        card.IsProgressIndeterminate = !downloading;
        card.Progress = downloading ? Math.Clamp(status!.Progress, 0, 1) : 0;
        card.PercentText = downloading ? $"{card.Progress * 100:0}%" : "";
        // A load provisioning its GPU runtime also reports Downloading but cannot be canceled.
        card.ShowCancel = downloading && _models.IsDownloading(card.ModelId);

        card.ShowDownload = !downloaded && !busy;
        var available = string.IsNullOrWhiteSpace(card.Metadata.UnavailableReason);
        card.CanDownload = available && !anyDownloading && !IsMigratingModelStorage;
        card.ShowUse = downloaded && !busy;
        // A selected model that failed to load can be tried again.
        card.CanUse = available && (!selected || failed) && !IsMigratingModelStorage;
        card.UseText = selected
            ? failed ? Loc.Instance["Dictation.ModelCardRetry"] : Loc.Instance["Dictation.ModelCardInUse"]
            : Loc.Instance["Dictation.ModelCardUse"];
        card.ShowRemove = (downloaded || card.HasLink) && !busy;
        card.CanRemove = !anyBusy && !IsMigratingModelStorage && !HasLocalModelOperation;
        card.CanUse &= !HasLocalModelOperation;
        card.CanDownload &= !HasLocalModelOperation;
        card.CanTest = available && downloaded && !anyBusy && !anyDownloading && !IsRecording
            && !IsMigratingModelStorage && !HasLocalModelOperation && _models.SupportsRawModelTest(card.ModelId);
        card.ShowLink = !downloaded && !busy && _models.GetTranscriptionPlugin(card.ModelId)?.ModelLinking is not null;
        card.CanLink = card.ShowLink && available && !anyBusy && !anyDownloading && !IsRecording
            && !IsMigratingModelStorage && !HasLocalModelOperation;
        if (card.IsLinking)
        {
            card.ShowProgress = true;
            card.IsProgressIndeterminate = true;
        }
    }

    [RelayCommand]
    private async Task DownloadLocalModel(LocalModelCard? card)
    {
        if (card is not { ShowDownload: true, CanDownload: true })
        {
            return;
        }

        // The selected model is meant to be loaded, as when it is picked from the list.
        if (SelectedModel is { } selected && selected.ModelId == card.ModelId)
        {
            await DownloadAndLoadSelectedModelAsync(selected);
            return;
        }

        StatusText = Loc.Instance.GetString("Dictation.DownloadingModel", card.DisplayName);
        try
        {
            await _models.DownloadModelAsync(card.ModelId);
            StatusText = Loc.Instance.GetString("Dictation.ModelDownloaded", card.DisplayName);
        }
        catch (ModelDownloadCanceledException)
        {
            StatusText = Loc.Instance.GetString("Dictation.ModelDownloadCanceled", card.DisplayName);
        }
        catch (Exception ex)
        {
            StatusText = Loc.Instance.GetString("Dictation.ModelDownloadFailed", FormatModelStatusError(ex.Message));
        }
        finally
        {
            RefreshLocalModelCards(checkDisk: true);
        }
    }

    [RelayCommand]
    private void CancelLocalModelDownload(LocalModelCard? card)
    {
        if (card is not null && _models.CancelDownload(card.ModelId))
        {
            StatusText = Loc.Instance.GetString("Dictation.ModelDownloadCanceled", card.DisplayName);
        }
    }

    [RelayCommand]
    private void UseLocalModel(LocalModelCard? card)
    {
        if (card is not { ShowUse: true, CanUse: true }
            || ModelOptions.FirstOrDefault(candidate => candidate.ModelId == card.ModelId) is not { } option)
        {
            return;
        }

        if (ReferenceEquals(SelectedModel, option))
        {
            _ = DownloadAndLoadSelectedModelAsync(option);
            return;
        }

        SelectedModel = option;
    }

    public Task DeleteLocalModelAsync(LocalModelCard card) =>
        card is { ShowRemove: true, CanRemove: true }
            ? DeleteModelFilesAsync(card.ModelId, card.DisplayName)
            : Task.CompletedTask;
}
