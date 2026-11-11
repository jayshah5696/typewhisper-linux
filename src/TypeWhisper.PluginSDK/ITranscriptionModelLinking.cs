namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional local-model file linking. A link must verify the exact catalog artifact before it
/// becomes ready. The source remains externally owned: removal unlinks it and must never move,
/// modify or delete the source. All load paths must revalidate the external file.
/// </summary>
public interface ITranscriptionModelLinking
{
    /// <summary>
    /// Reads the saved external path without native initialization or hashing. A missing or changed
    /// source may still have a link, so callers can offer unlink independently of download readiness.
    /// </summary>
    string? GetLinkedModelPath(string modelId);

    /// <summary>Verifies a known catalog model and publishes its external link. Never selects it.</summary>
    Task LinkModelAsync(string modelId, string sourcePath, CancellationToken ct);
}
