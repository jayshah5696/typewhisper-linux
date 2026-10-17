namespace TypeWhisper.Core.Models;

/// <summary>The cleanup that actually ran, independent of provenance capture or text changes.</summary>
public sealed record CleanupOutcome(
    CleanupLevel RequestedLevel,
    CleanupLevel AppliedLevel,
    CleanupFallbackReason? FallbackReason = null
);

public enum CleanupFallbackReason
{
    ProviderUnavailable,
    TimedOut,
    ProviderFailed,
    EmptyResponse,
}
