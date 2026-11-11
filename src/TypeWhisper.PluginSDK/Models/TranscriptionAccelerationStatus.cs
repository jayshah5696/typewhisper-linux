// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>
///     Reports the acceleration backend the engine actually loaded with, plus a
///     restart-required flag for when the pinned runtime no longer matches the
///     user's saved preference.
/// </summary>
// ReSharper disable once UnusedType.Global
public sealed record TranscriptionAccelerationStatus(
    TranscriptionAccelerationBackend ActiveBackend,
    string DisplayText,
    string? Detail = null,
    bool RequiresRestart = false
)
{
    /// <summary>Confirmed loaded model state; null when a legacy provider does not report it.</summary>
    public bool? IsModelLoaded { get; init; }

    /// <summary>Device confirmed by native model loading; null while unloaded or unknown.</summary>
    public TranscriptionDevice? ActiveDevice { get; init; }

    /// <summary>Original GPU failure when an Auto request retried on CPU.</summary>
    public string? FallbackReason { get; init; }
}
