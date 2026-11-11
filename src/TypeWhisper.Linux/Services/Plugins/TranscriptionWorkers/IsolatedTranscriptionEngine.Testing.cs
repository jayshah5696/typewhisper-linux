using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;

internal sealed partial class IsolatedTranscriptionEngine
{
    /// <summary>
    /// Runs raw inference without persisting a temporary selection. Neither load nor selection is mirrored to the
    /// metadata plugin. Worker settings writes are memory-only. A matching loaded model is tested
    /// in its existing worker because its files have an exclusive lifetime lease. Another model
    /// needs extra memory; insufficient memory is a test failure, not a reason to evict the live model.
    /// The caller holds ModelManagerService's model lease lock throughout this operation.
    /// </summary>
    internal async Task<RawModelTestResult> TestModelAsync(
        string modelId,
        byte[] audio,
        string language,
        TranscriptionAccelerationPreference preference,
        TranscriptionHardwareRequest? hardware,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new TranscriptionWorkerMessage
        {
            Command = TranscriptionWorkerCommands.Transcribe,
            ModelId = modelId,
            Language = language,
            Translate = false,
            Prompt = null,
        };
        if (_worker is { IsAlive: true } && _state?.LoadedModelId == modelId)
        {
            var configured = _hardwareRequest;
            if (AccelerationPreference != preference
                || configured?.RequestedPreference != hardware?.RequestedPreference
                || configured?.Device.Backend != hardware?.Device.Backend
                || configured?.Device.StableId != hardware?.Device.StableId)
                throw new InvalidOperationException("Reload or unload this model before testing different hardware.");
            var current = await RunAsync(request, audio, ct).ConfigureAwait(false);
            return new(TranscriptionWorkerProtocol.FromResult(current.Result
                ?? throw new InvalidDataException("The model test returned no transcription.")), AccelerationStatus);
        }
        await using var test = new IsolatedTranscriptionEngine(_inner, _start, _log,
            time: _time, settings: _settings, executionPreference: preference)
        {
            _hardwareRequest = hardware,
        };
        // RunAsync names the test model directly. Calling LoadModelAsync or SelectModel here
        // would mirror its selection into the host and could persist provider settings.
        var response = await test.RunAsync(request, audio, ct).ConfigureAwait(false);
        return new RawModelTestResult(
            TranscriptionWorkerProtocol.FromResult(response.Result
                ?? throw new InvalidDataException("The model test returned no transcription.")),
            test.AccelerationStatus);
    }
}

public sealed record RawModelTestResult(
    PluginTranscriptionResult Transcription,
    TranscriptionAccelerationStatus Acceleration);
