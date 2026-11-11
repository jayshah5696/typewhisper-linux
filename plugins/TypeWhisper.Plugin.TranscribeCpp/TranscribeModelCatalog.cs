using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.TranscribeCpp;

internal sealed partial record TranscribeModelCatalog
{
    internal const string SupportedEngineVersion = "0.4.1";
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> s_architectures = new(StringComparer.Ordinal)
    {
        "parakeet", "cohere_asr", "canary", "qwen3_asr", "moss", "voxtral", "voxtral_realtime",
        "canary_qwen", "whisper", "moonshine", "moonshine_streaming", "sensevoice", "funasr_nano",
        "gigaam", "granite_speech", "granite_speech_nar", "granite5_ctc", "medasr",
    };

    public required string Source { get; init; }
    public required string SourceVersion { get; init; }
    public required string EngineVersion { get; init; }
    public required string SourceArchiveSha256 { get; init; }
    public required string T3Revision { get; init; }
    public required IReadOnlyList<TranscribeModel> Models { get; init; }

    public static TranscribeModelCatalog LoadEmbedded()
    {
        using var stream = typeof(TranscribeModelCatalog).Assembly.GetManifestResourceStream(
            "TypeWhisper.Plugin.TranscribeCpp.Models.catalog.json")
            ?? throw new InvalidDataException("The pinned transcribe.cpp model catalog is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static TranscribeModelCatalog Parse(string json)
    {
        TranscribeModelCatalog catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<TranscribeModelCatalog>(json, s_json)
                ?? throw new InvalidDataException("The model catalog is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The model catalog is malformed.", ex);
        }
        if (catalog.Source != "@handy-computer/models" || catalog.SourceVersion != "0.2.3"
            || catalog.EngineVersion != SupportedEngineVersion || catalog.Models is not { Count: > 0 }
            || !Sha256Pattern().IsMatch(catalog.SourceArchiveSha256 ?? "")
            || !RevisionPattern().IsMatch(catalog.T3Revision ?? ""))
            throw new InvalidDataException("Review the model catalog source and native engine version before updating them.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in catalog.Models)
        {
            if (model is null)
                throw new InvalidDataException("A model entry in the pinned catalog is null.");
            ValidateModel(model);
            if (!ids.Add(model.Id) || !filenames.Add(model.Filename))
                throw new InvalidDataException("Duplicate model identity or filename in the pinned catalog.");
        }
        return catalog;
    }

    public TranscribeModel Get(string id) => Models.FirstOrDefault(model => model.Id == id)
        ?? throw new ArgumentException($"Unknown transcribe.cpp model '{id}'.", nameof(id));

    internal static void ValidateModel(TranscribeModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!RepositoryPattern().IsMatch(model.Id ?? "")
            || model.Filename is null || !FilenamePattern().IsMatch(model.Filename) || model.Filename.StartsWith('.')
            || !RevisionPattern().IsMatch(model.Revision ?? "")
            || !Sha256Pattern().IsMatch(model.Sha256 ?? "") || model.Size <= 0
            || model.Role != "asr" || !s_architectures.Contains(model.Architecture ?? "")
            || !Version.TryParse(model.MinimumEngineVersion, out var version) || version.Build < 0
            || model.Languages is not { Count: > 0 }
            || model.Languages.Any(language => !LanguagePattern().IsMatch(language ?? ""))
            || model.Languages.Distinct(StringComparer.Ordinal).Count() != model.Languages.Count
            || string.IsNullOrWhiteSpace(model.Name) || string.IsNullOrWhiteSpace(model.License)
            || !RepositoryPattern().IsMatch(model.Publisher ?? "")
            || !RevisionPattern().IsMatch(model.PublisherRevision ?? "")
            || model.LicenseUrl != $"https://huggingface.co/{model.Publisher}/tree/{model.PublisherRevision}"
            || model.ReferenceBenchmarks is null
            || model.ReferenceBenchmarks.Any(result => result is null || !double.IsFinite(result.Value) || result.Value < 0
                || string.IsNullOrWhiteSpace(result.Conditions)
                || !Uri.TryCreate(result.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"))
            throw new InvalidDataException($"Invalid pinned model metadata for '{model.Id}'.");
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]*/[A-Za-z0-9_][A-Za-z0-9_.-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]*\.gguf\z", RegexOptions.CultureInvariant)]
    private static partial Regex FilenamePattern();
    [GeneratedRegex(@"^[a-f0-9]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RevisionPattern();
    [GeneratedRegex(@"^[a-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
    [GeneratedRegex(@"^[a-z]{2,3}(?:-[a-z0-9]{2,8})*\z", RegexOptions.CultureInvariant)]
    private static partial Regex LanguagePattern();
}

internal sealed record TranscribeModel
{
    public required string Id { get; init; }
    public required string Revision { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Filename { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
    public required string Role { get; init; }
    public required string Architecture { get; init; }
    public required string MinimumEngineVersion { get; init; }
    public required string ConversionLicense { get; init; }
    public required string License { get; init; }
    public required string Publisher { get; init; }
    public required string PublisherRevision { get; init; }
    public required string LicenseUrl { get; init; }
    public required IReadOnlyList<string> Languages { get; init; }
    public bool Recommended { get; init; }
    public bool SupportsStreaming { get; init; }
    public bool SupportsLanguageDetection { get; init; }
    public bool SupportsTranslation { get; init; }
    public required IReadOnlyList<PluginModelBenchmark> ReferenceBenchmarks { get; init; }

    [JsonIgnore]
    public string DownloadUrl => $"https://huggingface.co/{Id}/resolve/{Revision}/{Filename}";

    [JsonIgnore]
    public string? UnavailableReason => Version.Parse(MinimumEngineVersion) > Version.Parse(TranscribeModelCatalog.SupportedEngineVersion)
        ? $"Requires transcribe.cpp {MinimumEngineVersion}; bundled engine is {TranscribeModelCatalog.SupportedEngineVersion}."
        : License is "mit" or "apache-2.0" or "cc-by-4.0" or "cc-by-sa-4.0"
            ? null
            : "Original publisher terms require review before this model can be downloaded or loaded. See the license link.";

    public PluginModelInfo ToModelInfo() => new(Id, Name)
    {
        SizeDescription = (Size / (1024d * 1024)).ToString("N0", CultureInfo.CurrentCulture) + " MiB download",
        EstimatedSizeMB = (long)Math.Ceiling(Size / (1024d * 1024)),
        IsRecommended = Recommended,
        LanguageCount = Languages.Count,
        Languages = Languages,
        SupportsStreaming = SupportsStreaming,
        SupportsLanguageDetection = SupportsLanguageDetection,
        SupportsTranslation = SupportsTranslation,
        License = License,
        LicenseUrl = LicenseUrl,
        UnavailableReason = UnavailableReason,
        ReferenceBenchmarks = ReferenceBenchmarks,
    };
}
