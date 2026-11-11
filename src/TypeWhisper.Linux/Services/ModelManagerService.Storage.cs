namespace TypeWhisper.Linux.Services;

public sealed partial class ModelManagerService
{
    // Protected by _fileOperationGate. No download or per-model operation may start during a move.
    private bool _exclusiveAssetOperation;

    public async Task ChangeModelStorageAsync(Func<Task> change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!await _modelLock.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
            throw new InvalidOperationException("A model is in use. Finish transcription before changing storage.");
        var claimed = false;
        try
        {
            lock (_fileOperationGate)
            {
                if (_exclusiveAssetOperation || _fileOperations.Count != 0)
                    throw new InvalidOperationException("Model files are in use. Finish the current operation before changing storage.");
                _exclusiveAssetOperation = claimed = true;
            }
            await UnloadModelCoreAsync().ConfigureAwait(false);
            if (ActiveModelId is not null)
                throw new InvalidOperationException("The model could not unload. Storage was not changed.");
            ct.ThrowIfCancellationRequested();
            await change().ConfigureAwait(false);
        }
        finally
        {
            if (claimed)
            {
                lock (_fileOperationGate) { _exclusiveAssetOperation = false; }
            }
            _modelLock.Release();
        }
    }
}
