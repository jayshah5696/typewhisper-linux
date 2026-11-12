using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

public sealed partial class ModelManagerService
{
    public async Task PrepareCudaRuntimeAsync(ITranscriptionEngineRole engine, IProgress<double>? progress, CancellationToken ct)
    {
        var key = "runtime:" + engine.GetTranscriptionSelectionId();
        if (!TryBeginLoad(key, out var slot))
            throw new InvalidOperationException("A runtime operation is already running for this provider.");
        try { await engine.EnsureCudaRuntimeReadyAsync(progress, ct).ConfigureAwait(false); }
        finally { EndFileOperation(key, slot, ModelFileOperationOutcome.Ended); }
    }
}
