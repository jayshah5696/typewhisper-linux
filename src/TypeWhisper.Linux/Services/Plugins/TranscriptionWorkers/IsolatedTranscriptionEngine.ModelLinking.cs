using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;

internal sealed partial class IsolatedTranscriptionEngine
{
    private sealed class ModelLinkProxy(IsolatedTranscriptionEngine owner, ITranscriptionModelLinking inner)
        : ITranscriptionModelLinking
    {
        public string? GetLinkedModelPath(string modelId) => inner.GetLinkedModelPath(modelId);

        public async Task LinkModelAsync(string modelId, string sourcePath, CancellationToken ct)
        {
            await owner._gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(owner._disposed, owner);
                if (owner._worker is { IsAlive: true } && owner._state?.LoadedModelId == modelId)
                    throw new InvalidOperationException("Unload this model before replacing its files.");
                await inner.LinkModelAsync(modelId, sourcePath, ct).ConfigureAwait(false);
            }
            finally { owner._gate.Release(); }
        }
    }
}
