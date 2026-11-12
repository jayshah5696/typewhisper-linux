using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;
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

    [ObservableProperty]
    private bool _showLink;

    [ObservableProperty]
    private bool _canLink;

    [ObservableProperty]
    private bool _isLinking;

    internal string? LinkError { get; set; }
    internal bool LinkCanceled { get; set; }
    internal bool LinkCompleted { get; set; }
    public string LinkStatusText => IsLinking ? Loc.Instance.GetString("Dictation.ModelLinking", DisplayName)
        : LinkError is not null ? Loc.Instance.GetString("Dictation.ModelLinkFailed", LinkError)
        : LinkCanceled ? Loc.Instance["Dictation.ModelLinkCanceled"]
        : LinkCompleted && HasLink ? Loc.Instance.GetString("Dictation.ModelLinked", DisplayName) : "";
    internal void RefreshLinkStatus() => OnPropertyChanged(nameof(LinkStatusText));
    partial void OnIsLinkingChanged(bool value) => RefreshLinkStatus();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLink), nameof(RemoveText), nameof(LinkedSourceText), nameof(LinkStatusText))]
    private string? _linkedPath;

    public bool HasLink => LinkedPath is not null;
    public string RemoveText => Loc.Instance[HasLink ? "Dictation.ModelUnlink" : "Dictation.ModelCardRemove"];
    public string LinkedSourceText => HasLink ? Loc.Instance.GetString("Dictation.ModelLinkedSource", LinkedPath!) : "";

    [ObservableProperty]
    private bool _canTest;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestTranscript))]
    private string _testTranscript = "";

    internal RawModelTestResult? TestResult { get; set; }
    internal string? TestError { get; set; }
    internal bool TestCanceled { get; set; }
    internal bool TestTimedOut { get; set; }
    public bool HasTestTranscript => TestTranscript.Length > 0;
    public string TestStatusText => IsTesting ? Loc.Instance["Dictation.ModelTestRunning"]
        : TestTimedOut ? Loc.Instance.GetString("Dictation.ModelTestFailed", Loc.Instance["Dictation.ModelTestTimeout"])
        : TestError is not null ? Loc.Instance.GetString("Dictation.ModelTestFailed", TestError)
        : TestCanceled ? Loc.Instance["Dictation.ModelTestCanceled"]
        : TestResult is { } result ? Loc.Instance.GetString("Dictation.ModelTestCompleted",
            result.Acceleration.IsModelLoaded == true
                ? $"{result.Acceleration.ActiveBackend} · {result.Acceleration.ActiveDevice?.DisplayName ?? result.Acceleration.DisplayText}"
                : Loc.Instance["Dictation.ModelTestBackendUnknown"])
            + (result.Acceleration.FallbackReason is { } reason ? $"\n{reason}" : "")
        : "";
    internal void RefreshTestStatus() => OnPropertyChanged(nameof(TestStatusText));
    partial void OnIsTestingChanged(bool value) => RefreshTestStatus();

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
        RefreshTestStatus();
        RefreshLinkStatus();
        OnPropertyChanged(nameof(RemoveText));
        OnPropertyChanged(nameof(LinkedSourceText));
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
