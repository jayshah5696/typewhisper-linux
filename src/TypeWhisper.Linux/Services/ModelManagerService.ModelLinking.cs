namespace TypeWhisper.Linux.Services;

public sealed partial class ModelManagerService
{
    public string? GetLinkedModelPath(string modelId)
    {
        var linking = GetTranscriptionPlugin(modelId)?.ModelLinking;
        return linking?.GetLinkedModelPath(ParsePluginModelId(modelId).ModelId);
    }

    public async Task LinkModelAsync(string modelId, string sourcePath, CancellationToken ct)
    {
        var linking = GetTranscriptionPlugin(modelId)?.ModelLinking
            ?? throw new NotSupportedException("This engine does not support verified model links.");
        if (!await _modelLock.WaitAsync(TimeSpan.Zero, ct))
            throw new InvalidOperationException("Transcription or another model operation is active. Try again when it finishes.");
        TaskCompletionSource<ModelFileOperationOutcome>? slot = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!TryBeginLoad(modelId, out var claimed))
                throw new InvalidOperationException("This model has a file operation in progress.");
            slot = claimed;
            CancelAutoUnload();
            await linking.LinkModelAsync(ParsePluginModelId(modelId).ModelId, sourcePath, ct);
            ClearStatus(modelId);
        }
        finally
        {
            if (slot is not null) EndFileOperation(modelId, slot, ModelFileOperationOutcome.Ended);
            try { ScheduleAutoUnload(); }
            finally { _modelLock.Release(); }
        }
    }
}
