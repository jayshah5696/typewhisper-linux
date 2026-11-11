using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>Optional provider-native device discovery and exact device selection.</summary>
/// <remarks>
/// Discovery must run in an isolated worker, not in the settings process. It must not download
/// runtimes or load model weights. The host enforces a deadline and supplies the intended model.
/// Implementations register CPU backends only for an explicit CPU request. Configuration does
/// not prove execution; report the loaded device through the engine's acceleration status.
/// </remarks>
public interface ITranscriptionHardwareSelection
{
    /// <summary>Returns devices this provider can use for the model, including unavailable runtime reasons.</summary>
    Task<IReadOnlyList<TranscriptionDevice>> DiscoverDevicesAsync(string modelId, CancellationToken ct);

    /// <summary>
    /// Retains original user intent and the resolved device before loading. Auto may retry CPU
    /// once in a fresh worker. An explicit GPU request must fail rather than silently use CPU.
    /// The legacy engine setter still receives a concrete preference before this call.
    /// </summary>
    void ConfigureHardware(TranscriptionHardwareRequest request);
}
