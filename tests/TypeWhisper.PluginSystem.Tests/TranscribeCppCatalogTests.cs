extern alias TranscribeCpp;

using System.Text.Json;
using System.Text.Json.Nodes;
using TranscribeCpp::TypeWhisper.Plugin.TranscribeCpp;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class TranscribeCppCatalogTests
{
    [Fact]
    public void EmbeddedCatalogContainsAllReviewedModelsWithPinnedFilesAndPublisherTerms()
    {
        var catalog = TranscribeModelCatalog.LoadEmbedded();
        Assert.Equal("0.4.1", catalog.EngineVersion);
        Assert.Equal("0.2.3", catalog.SourceVersion);
        Assert.Equal(59, catalog.Models.Count);
        Assert.Equal(59, catalog.Models.Select(m => m.Id).Distinct().Count());
        Assert.Equal(59, catalog.Models.Select(m => m.Filename).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(catalog.Models, model =>
        {
            Assert.Equal("asr", model.Role);
            Assert.NotEmpty(model.Architecture);
            Assert.Equal(40, model.Revision.Length);
            Assert.Equal(64, model.Sha256.Length);
            Assert.True(model.Size > 0);
            Assert.NotEmpty(model.Languages);
            Assert.StartsWith("https://huggingface.co/", model.LicenseUrl);
            Assert.Equal(40, model.PublisherRevision.Length);
            Assert.Equal($"https://huggingface.co/{model.Id}/resolve/{model.Revision}/{model.Filename}", model.DownloadUrl);
        });
    }

    [Fact]
    public void PublisherLicenseOverridesConversionLabelAndCustomTermsRemainGated()
    {
        var catalog = TranscribeModelCatalog.LoadEmbedded();
        var turbo = catalog.Get("handy-computer/whisper-large-v3-turbo-gguf");
        Assert.Equal("apache-2.0", turbo.ConversionLicense);
        Assert.Equal("mit", turbo.License);
        Assert.Null(turbo.UnavailableReason);
        var parakeet = catalog.Get("handy-computer/parakeet-unified-en-0.6b-gguf");
        Assert.Equal("other", parakeet.License);
        Assert.Contains("publisher terms", parakeet.UnavailableReason);
    }

    [Fact]
    public void MetadataPreservesPerModelCapabilitiesAndReferenceProvenance()
    {
        var model = TranscribeModelCatalog.LoadEmbedded().Get("handy-computer/moonshine-streaming-medium-gguf");
        var info = model.ToModelInfo();
        Assert.Equal(["en"], info.Languages);
        Assert.True(info.SupportsStreaming);
        Assert.False(info.SupportsLanguageDetection);
        Assert.False(info.SupportsTranslation);
        Assert.Contains("download", info.SizeDescription, StringComparison.OrdinalIgnoreCase);
        Assert.All(info.ReferenceBenchmarks!, benchmark =>
        {
            Assert.NotEmpty(benchmark.SourceUrl);
            Assert.NotEmpty(benchmark.Conditions!);
        });
        Assert.Contains(info.ReferenceBenchmarks!, b => b.Dataset == "FLEURS test" && b.Metric == "WER percent");
    }

    [Theory]
    [InlineData("filename", "../escape.gguf")]
    [InlineData("filename", "nested/model.gguf")]
    [InlineData("filename", ".hidden.gguf")]
    [InlineData("filename", "C:model.gguf")]
    [InlineData("filename", "model.GGUF")]
    [InlineData("filename", "model.gguf\n")]
    [InlineData("revision", "7e948f21b7bdbac698d3318db9d350f1096f3b6c\n")]
    [InlineData("id", "https://evil.example/model")]
    [InlineData("revision", "main")]
    [InlineData("sha256", "deadbeef")]
    [InlineData("role", "tts")]
    [InlineData("architecture", "unreviewed")]
    public void MalformedCatalogIsRejectedBeforeStorageOrNetwork(string property, string value)
    {
        var json = CatalogJson();
        json["models"]![0]![property] = value;
        Assert.Throws<InvalidDataException>(() => TranscribeModelCatalog.Parse(json.ToJsonString()));
    }

    [Theory]
    [InlineData("size", 0)]
    [InlineData("size", -1)]
    public void InvalidFileSizeIsRejected(string property, long value)
    {
        var json = CatalogJson();
        json["models"]![0]![property] = value;
        Assert.Throws<InvalidDataException>(() => TranscribeModelCatalog.Parse(json.ToJsonString()));
    }

    [Fact]
    public void NullModelAndNullBenchmarkAreRejectedAsMalformedCatalogData()
    {
        var json = CatalogJson();
        json["models"]![0] = null;
        Assert.Throws<InvalidDataException>(() => TranscribeModelCatalog.Parse(json.ToJsonString()));
        json = CatalogJson();
        json["models"]![0]!["referenceBenchmarks"]![0] = null;
        Assert.Throws<InvalidDataException>(() => TranscribeModelCatalog.Parse(json.ToJsonString()));
    }

    [Fact]
    public void FutureEngineRequirementRemainsVisibleButUnavailable()
    {
        var json = CatalogJson();
        json["models"]![0]!["minimumEngineVersion"] = "9.0.0";
        var model = TranscribeModelCatalog.Parse(json.ToJsonString()).Models[0];
        Assert.Contains("9.0.0", model.UnavailableReason);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("filename")]
    public void DuplicateIdentityOrFilenameIsRejected(string property)
    {
        var json = CatalogJson();
        json["models"]![1]![property] = json["models"]![0]![property]!.DeepClone();
        Assert.Throws<InvalidDataException>(() => TranscribeModelCatalog.Parse(json.ToJsonString()));
    }

    [Fact]
    public void EmptyLanguageListAndUnknownCatalogEngineAreRejected()
    {
        var json = CatalogJson();
        json["models"]![0]!["languages"] = new JsonArray();
        Assert.Throws<InvalidDataException>(() => TranscribeModelCatalog.Parse(json.ToJsonString()));
        json = CatalogJson();
        json["engineVersion"] = "0.5.0";
        Assert.Throws<InvalidDataException>(() => TranscribeModelCatalog.Parse(json.ToJsonString()));
    }

    private static JsonNode CatalogJson() => JsonSerializer.SerializeToNode(
        TranscribeModelCatalog.LoadEmbedded(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
}
