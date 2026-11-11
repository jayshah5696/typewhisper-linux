using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Plugins.Shared.Cuda;

namespace TypeWhisper.Plugin.TranscribeCpp;

public sealed partial class TranscribeCppPlugin
{
    private string? _cudaAssetDirectory;
    private TranscribeCudaArtifact? _cudaArtifact;
    private CudaRuntimeProvisioner? _cudaLibraries;
    private FileStream? _cudaLease;

    // Construction and readiness are metadata only. No driver initialization or native loads.
    private (TranscribeCudaArtifact Artifact, CudaRuntimeProvisioner Libraries) Cuda
    {
        get
        {
            var directory = _host?.PluginAssetDirectory
                ?? throw new InvalidOperationException("Activate the plugin before preparing CUDA.");
            lock (_storeSync)
            {
                if (_cudaArtifact is null || _cudaLibraries is null || directory != _cudaAssetDirectory)
                {
                    if (_cudaLease is not null) throw new InvalidOperationException("Unload CUDA before changing model storage.");
                    _cudaArtifact = new TranscribeCudaArtifact(Path.Combine(directory, "Runtimes"), _client);
                    _cudaLibraries = new CudaRuntimeProvisioner(
                        CudaRuntimeProvisioner.CacheRootForPluginAssetDirectory(directory), _client,
                        text => _host?.Log(PluginLogLevel.Info, text),
                        () => _host?.Processes ?? throw new NotSupportedException("The host does not provide process supervision."));
                    _cudaAssetDirectory = directory;
                }
                return (_cudaArtifact, _cudaLibraries);
            }
        }
    }

    public bool ProvisionsCudaRuntimeOnDemand => true;
    public bool IsCudaRuntimeProvisioned => _host is not null && Cuda is var cuda
        && cuda.Artifact.IsInstalled && cuda.Libraries.IsProfileSatisfied(CudaRuntimeProfile.WhisperCublas);

    // This is the explicit Settings action. The shared network/disk operation does not preload.
    public async Task EnsureCudaRuntimeReadyAsync(IProgress<double>? progress, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await EnterGateAsync(ct, linked.Token).ConfigureAwait(false);
        try
        {
            var cuda = Cuda;
            await cuda.Artifact.InstallAsync(new InlineProgress(value => progress?.Report(value * 0.2)), linked.Token).ConfigureAwait(false);
            await cuda.Libraries.DownloadAndExtractAsync(CudaRuntimeProfile.WhisperCublas,
                new InlineProgress(value => progress?.Report(0.2 + value * 0.8)), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw CancellationFault(ex); }
        finally { _gate.Release(); }
    }

    public async Task ClearCudaRuntimeAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await EnterGateAsync(ct, linked.Token).ConfigureAwait(false);
        try
        {
            if (_model is not null) throw new InvalidOperationException("Unload the model before clearing CUDA libraries.");
            var cuda = Cuda;
            await cuda.Artifact.ClearAsync(linked.Token).ConfigureAwait(false);
            await cuda.Libraries.ClearCacheAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw CancellationFault(ex); }
        finally { _gate.Release(); }
    }

    private async Task PrepareCudaWorkerAsync(CancellationToken ct)
    {
        if (_cudaLease is not null) return;
        var cuda = Cuda;
        if (!cuda.Artifact.IsInstalled)
            throw new InvalidOperationException("Download the optional transcribe.cpp CUDA runtime explicitly, or retry on CPU or Vulkan.");
        if (!Environment.GetCommandLineArgs().Contains("--transcription-worker", StringComparer.Ordinal))
            throw new NotSupportedException("Initialize CUDA only in a transcription worker.");
        var lease = await cuda.Artifact.AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            await cuda.Libraries.PreloadInstalledAsync(CudaRuntimeProfile.WhisperCublas, ct).ConfigureAwait(false);
            _cudaLease = lease;
        }
        catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
