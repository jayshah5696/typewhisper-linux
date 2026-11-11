using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;

namespace TypeWhisper.Linux.Services;

public sealed partial class ModelManagerService
{
    /// <summary>Only isolated local providers can guarantee no host-side selection writes.</summary>
    public bool SupportsRawModelTest(string modelId) => GetTranscriptionPlugin(modelId) is IsolatedTranscriptionEngine
        && GetTranscriptionModelInfo(modelId) is { UnavailableReason: null } info
        && (info.Languages is null || info.Languages.Count == 0
            || info.Languages.Contains("en", StringComparer.OrdinalIgnoreCase));

    public async Task<RawModelTestResult> TestModelAsync(string modelId, CancellationToken ct)
    {
        if (!SupportsRawModelTest(modelId)
            || GetTranscriptionPlugin(modelId) is not IsolatedTranscriptionEngine engine)
            throw new NotSupportedException("This model cannot run the isolated English audio test.");
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
            var (selectionId, target) = ParsePluginModelId(modelId);
            if (engine.SupportsModelDownload && !engine.IsModelDownloaded(target))
                throw new FileNotFoundException("Download or link this model before testing it.");
            var settings = _settings.Current;
            var requested = GetAccelerationPreference(settings.LocalModelAcceleration);
            var hardware = engine.HardwareSelection is null ? null
                : await ResolveHardwareRequestAsync(engine, target, requested,
                    settings.GetLocalModelDevice(selectionId, settings.LocalModelAcceleration), ct);
            // Legacy Auto retains the ordinary load's CPU default/preflight policy. This action
            // never provisions a missing runtime: that remains an explicit settings operation.
            var resolved = hardware is not null ? TranscriptionHardwareSelector.ToPreference(hardware.Device.Backend)
                : requested == TranscriptionAccelerationPreference.Auto
                    ? engine.SupportedAccelerationBackends.Contains(TranscriptionAccelerationBackend.NvidiaCuda)
                        ? ResolveAutoPreference(requested, () => CudaRuntimePreflight().Success)
                        : TranscriptionAccelerationPreference.Cpu
                    : requested;
            if (resolved != TranscriptionAccelerationPreference.Cpu && engine.ProvisionsCudaRuntimeOnDemand
                && !engine.IsCudaRuntimeProvisioned)
                throw new InvalidOperationException("Prepare the GPU runtime explicitly before testing, or choose CPU.");
            await using var fixture = typeof(ModelManagerService).Assembly.GetManifestResourceStream("TypeWhisper.ModelTest.wav")
                ?? throw new FileNotFoundException("The public model test fixture is missing.");
            using var audio = new MemoryStream();
            await fixture.CopyToAsync(audio, ct);
            return await engine.TestModelAsync(target, audio.ToArray(), "en", resolved, hardware, ct);
        }
        finally
        {
            if (slot is not null) EndFileOperation(modelId, slot, ModelFileOperationOutcome.Ended);
            try { ScheduleAutoUnload(); }
            finally { _modelLock.Release(); }
        }
    }

    private static async Task<TranscriptionHardwareRequest> ResolveHardwareRequestAsync(
        ITranscriptionEngineRole plugin, string modelId,
        TranscriptionAccelerationPreference requested, string? stableId, CancellationToken ct)
    {
        IReadOnlyList<TranscriptionDevice> devices;
        if (requested == TranscriptionAccelerationPreference.Cpu)
        {
            devices = [TranscriptionHardwareSelector.CpuDevice];
        }
        else
        {
            using var discovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
            discovery.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                devices = await plugin.HardwareSelection!.DiscoverDevicesAsync(modelId, discovery.Token)
                    .WaitAsync(discovery.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && discovery.IsCancellationRequested)
            {
                throw new TimeoutException("Native device discovery timed out. Select CPU to retry.", ex);
            }
        }
        return TranscriptionHardwareSelector.Resolve(requested,
            devices.Where(device => plugin.SupportedAccelerationBackends.Contains(device.Backend)).ToArray(), stableId);
    }
}
