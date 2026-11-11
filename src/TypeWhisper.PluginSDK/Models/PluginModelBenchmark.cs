namespace TypeWhisper.PluginSDK.Models;

/// <summary>Published reference result, not evidence of local accuracy, latency, or GPU execution.</summary>
public sealed record PluginModelBenchmark(
    string Dataset,
    string Language,
    string Metric,
    double Value,
    string SourceUrl,
    string Conditions);
