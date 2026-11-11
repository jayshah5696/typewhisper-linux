using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;

internal sealed partial class IsolatedTranscriptionEngine
{
    private readonly ITranscriptionHardwareSelection? _hardwareSelection;
    private TranscriptionHardwareRequest? _hardwareRequest;
    private TranscriptionHardwareRequest? _workerHardwareRequest;
    private string? _hardwareFallbackReason;

    public ITranscriptionHardwareSelection? HardwareSelection => _hardwareSelection;

    private TranscriptionAccelerationStatus HardwareAccelerationStatus()
    {
        var status = _worker is { IsAlive: true } && _stateAcceleration == EffectivePreference()
            ? _state?.Status
            : null;
        return (status ?? _inner.AccelerationStatus with { IsModelLoaded = false, ActiveDevice = null }) with
        {
            RequiresRestart = false,
            FallbackReason = _hardwareFallbackReason,
        };
    }

    private TranscriptionHardwareRequest? EffectiveHardwareRequest()
    {
        return FallbackActive() && _hardwareRequest is { } request
            ? request with { Device = TranscriptionHardwareSelector.CpuDevice }
            : _hardwareRequest;
    }

    // Discovery can initialize a native registry only once. Never use the model worker for it:
    // explicit CPU must still be able to register CPU backends only in a fresh process.
    private async Task<IReadOnlyList<TranscriptionDevice>> DiscoverDevicesAsync(string modelId, CancellationToken ct)
    {
        CancellationToken stop;
        lock (_stopSync)
        {
            stop = _stop.Token;
        }

        using var running = CancellationTokenSource.CreateLinkedTokenSource(ct, stop);
        try
        {
            await _gate.WaitAsync(running.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                await using var probe = await _start(TranscriptionAccelerationPreference.Cpu, null, running.Token)
                    .ConfigureAwait(false);
                var response = await probe.SendAsync(new TranscriptionWorkerMessage
                {
                    Command = TranscriptionWorkerCommands.DiscoverDevices,
                    ModelId = modelId,
                }, default, running.Token).ConfigureAwait(false);
                if (response.Error is { } error)
                {
                    throw TranscriptionWorkerProtocol.ToException(error);
                }

                return response.Devices ?? throw new InvalidDataException("The worker returned no device list.");
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException ex) when (stop.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw StoppedException(ex);
        }
    }

    private void ConfigureHardware(TranscriptionHardwareRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resolved = TranscriptionHardwareSelector.Resolve(request.RequestedPreference, [request.Device], request.Device.StableId);
        if (TranscriptionHardwareSelector.ToPreference(resolved.Device.Backend) != _inner.AccelerationPreference)
        {
            throw new ArgumentException("The hardware device must match the resolved acceleration preference.", nameof(request));
        }

        _hardwareRequest = request;
        _workerHardwareRequest = null;
        _cpuFallback = false;
        _hardwareFallbackReason = null;
        _state = null;
    }

    // This policy is deliberately separate from legacy crash recovery. Only Auto can retry a
    // GPU failure, and the same model, audio, language and prompt get exactly one CPU attempt.
    private async Task<TranscriptionWorkerMessage> RunHardwareAsync(
        TranscriptionWorkerMessage request,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct
    )
    {
        var hardware = _hardwareRequest
            ?? throw new InvalidOperationException("Configure hardware before loading this transcription engine.");
        var canRetry = hardware.RequestedPreference == TranscriptionAccelerationPreference.Auto
            && EffectivePreference() != TranscriptionAccelerationPreference.Cpu
            && _inner.SupportedAccelerationBackends.Contains(TranscriptionAccelerationBackend.Cpu);
        for (var attempt = 0; ; attempt++)
        {
            Exception? failure = null;
            TranscriptionWorkerMessage? response = null;
            try
            {
                response = await SendAsync(request, payload, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TranscriptionWorkerCrashedException or TimeoutException or IOException)
            {
                failure = ex;
            }
            catch (NotSupportedException)
            {
                // A worker without the negotiated capability must not accept a model load.
                await DiscardWorkerAsync().ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (_worker is not { IsAlive: true })
                {
                    await DiscardWorkerAsync().ConfigureAwait(false);
                }

                throw;
            }

            if (response is not null)
            {
                if (response.State is { } state)
                {
                    SetState(state, EffectivePreference());
                }

                if (response.Error is not { } error)
                {
                    return response;
                }

                // Decode engine errors outside transport catches. Missing weights are also
                // IOExceptions, but cannot be fixed by changing execution devices.
                failure = TranscriptionWorkerProtocol.ToException(error);
                if (error.Kind is not ("invalid-operation" or "io" or "timeout"))
                {
                    throw failure;
                }
            }

            ct.ThrowIfCancellationRequested();
            await DiscardWorkerAsync().ConfigureAwait(false);
            if (canRetry && attempt == 0)
            {
                _cpuFallback = true;
                _fallbackFrom = _inner.AccelerationPreference;
                _hardwareFallbackReason = failure!.Message;
                _log(PluginLogLevel.Warning, $"{ProviderDisplayName} GPU failed; retrying once on CPU: {failure!.Message}");
                continue;
            }

            var retryInstruction = hardware.RequestedPreference != TranscriptionAccelerationPreference.Auto
                && hardware.Device.Backend != TranscriptionAccelerationBackend.Cpu
                    ? " Retry explicitly on CPU or choose another device."
                    : " The transcription did not complete. Try another model or device.";
            throw new TranscriptionWorkerFaultedException(failure!.Message + retryInstruction, failure);
        }
    }

    private sealed class HardwareProxy(IsolatedTranscriptionEngine owner) : ITranscriptionHardwareSelection
    {
        public Task<IReadOnlyList<TranscriptionDevice>> DiscoverDevicesAsync(string modelId, CancellationToken ct)
        {
            return owner.DiscoverDevicesAsync(modelId, ct);
        }

        public void ConfigureHardware(TranscriptionHardwareRequest request)
        {
            owner.ConfigureHardware(request);
        }
    }
}
