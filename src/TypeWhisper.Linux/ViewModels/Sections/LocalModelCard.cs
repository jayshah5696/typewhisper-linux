using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

/// <summary>One downloadable local model in Dictation settings, with its state and actions.</summary>
public sealed partial class LocalModelCard(
    string modelId,
    string displayName,
    string engineName,
    string? sizeDescription,
    bool isRecommended,
    PluginModelInfo? metadata = null) : ObservableObject
{
    [ObservableProperty]
    private bool _isListed;

    [ObservableProperty]
    private string _badgeText = "";

    [ObservableProperty]
    private string _stateText = "";

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    private bool _showProgress;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _percentText = "";

    [ObservableProperty]
    private bool _showDownload;

    [ObservableProperty]
    private bool _canDownload;

    [ObservableProperty]
    private bool _showUse;

    [ObservableProperty]
    private bool _canUse;

    [ObservableProperty]
    private string _useText = "";

    [ObservableProperty]
    private bool _showRemove;

    [ObservableProperty]
    private bool _canRemove;

    [ObservableProperty]
    private bool _showCancel;

    public string ModelId { get; } = modelId;
    public string DisplayName { get; } = displayName;
    public bool IsRecommended { get; } = isRecommended;

    internal PluginModelInfo Metadata { get; } = metadata ?? new PluginModelInfo(modelId, displayName);
    public string EngineName { get; } = engineName;

    public string DetailText => string.IsNullOrWhiteSpace(sizeDescription)
        ? EngineName
        : $"{Loc.Instance["Dictation.ModelDownloadSize"]}: {sizeDescription} · {EngineName}";

    public string CapabilityText => string.Join(" · ",
        Loc.Instance.GetString("Dictation.ModelStreaming", KnownSupport(Metadata.SupportsStreaming)),
        Loc.Instance.GetString("Dictation.ModelTranslation", KnownSupport(Metadata.SupportsTranslation)),
        Loc.Instance.GetString("Dictation.ModelLanguageDetection", KnownSupport(Metadata.SupportsLanguageDetection)));

    public string LanguagesText => Loc.Instance.GetString("Dictation.ModelLanguages",
        Metadata.Languages is { Count: > 0 } languages
            ? string.Join(", ", languages.Select(code => DictationSectionViewModel.GetLanguageDisplayName(code) ?? code))
            : Loc.Instance["Dictation.ModelMetadataUnknown"]);

    public string LicenseText => Loc.Instance.GetString("Dictation.ModelLicense",
        Metadata.License ?? Loc.Instance["Dictation.ModelMetadataUnknown"]);
    public string? LicenseUrl => Metadata.LicenseUrl;
    public bool HasReferenceBenchmarks => Metadata.ReferenceBenchmarks is { Count: > 0 };
    public string ReferenceBenchmarkText => string.Join("\n\n", (Metadata.ReferenceBenchmarks ?? []).Select(result =>
        $"{result.Dataset} · {result.Language} · {result.Metric}: {result.Value.ToString("G", System.Globalization.CultureInfo.CurrentCulture)}\n{result.Conditions}\n{result.SourceUrl}"));

    internal void RefreshLocalizedMetadata()
    {
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(CapabilityText));
        OnPropertyChanged(nameof(LanguagesText));
        OnPropertyChanged(nameof(LicenseText));
        OnPropertyChanged(nameof(ReferenceBenchmarkText));
    }

    private static string KnownSupport(bool? support) => Loc.Instance[support switch
    {
        true => "Dictation.ModelMetadataYes",
        false => "Dictation.ModelMetadataNo",
        null => "Dictation.ModelMetadataUnknown",
    }];

    // Cached so progress ticks do not stat every model's files.
    internal bool Downloaded { get; set; }
    internal ModelStatusType? LastStatusType { get; set; }
    internal bool WasBusy { get; set; }
}
