using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.TranscribeCpp;

public sealed partial class TranscribeCppPlugin : ITranscriptionEnginePlugin, ITranscriptionHardwareSelection,
    ITranscriptionLanguageSelectionCapabilities, ITranscriptionModelLinking
{
    private readonly TranscribeModelCatalog _catalog;
    private readonly Func<ITranscribeNativeRuntime> _runtimeFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _client = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
    { Timeout = TimeSpan.FromHours(2) };
    private CancellationTokenSource _lifetime = new();
    private IPluginHostServices? _host;
    private TranscribeModelStore? _store;
    private ITranscribeNativeRuntime? _runtime;
    private ITranscribeNativeModel? _model;
    private TranscribeModelLease? _lease;
    private string? _loadedModelId;
    private TranscriptionHardwareRequest? _hardware;
    private bool _disposed;

    public TranscribeCppPlugin() : this(() => new TranscribeNativeRuntime()) { }
    internal TranscribeCppPlugin(Func<ITranscribeNativeRuntime> runtimeFactory, TranscribeModelCatalog? catalog = null)
    {
        _runtimeFactory = runtimeFactory;
        _catalog = catalog ?? TranscribeModelCatalog.LoadEmbedded();
    }

    public string PluginId => "com.typewhisper.transcribe-cpp";
    public string PluginName => "transcribe.cpp (Local)";
    public string PluginVersion => PluginBuildInfo.Version;
    public string ProviderId => "transcribe-cpp";
    public string ProviderDisplayName => "Local (transcribe.cpp)";
    public bool IsConfigured => true;
    public string? SelectedModelId { get; private set; }
    public bool SupportsModelDownload => true;
    public bool SupportsStructuredDictionaryTerms => true;
    public bool SupportsTranslation => SelectedDefinition?.SupportsTranslation == true;
    public bool SupportsStreaming => SelectedDefinition?.SupportsStreaming == true;
    public IReadOnlyList<string> SupportedLanguages => SelectedDefinition?.Languages ?? [];
    public LanguageSelectionSupport AutomaticDetectionSupport => SelectedDefinition is { SupportsLanguageDetection: true }
        or { Languages.Count: 1 } ? LanguageSelectionSupport.Supported : LanguageSelectionSupport.Unsupported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;
    public ITranscriptionHardwareSelection HardwareSelection => this;
    public ITranscriptionModelLinking ModelLinking => this;
    public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends { get; } =
        [TranscriptionAccelerationBackend.Cpu, TranscriptionAccelerationBackend.Vulkan];
    public TranscriptionAccelerationPreference AccelerationPreference { get; private set; } = TranscriptionAccelerationPreference.Cpu;
    public TranscriptionAccelerationStatus AccelerationStatus { get; private set; } = UnloadedStatus();
    public IReadOnlyList<PluginModelInfo> TranscriptionModels => _catalog.Models.Select(value => value.ToModelInfo()).ToArray();
    private TranscribeModel? SelectedDefinition => SelectedModelId is { } id ? _catalog.Get(id) : null;
    private TranscribeModelStore Store => _store ?? throw new InvalidOperationException("Activate the plugin before managing model files.");

    public Task ActivateAsync(IPluginHostServices host)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lifetime.IsCancellationRequested) { _lifetime.Dispose(); _lifetime = new CancellationTokenSource(); }
        _host = host;
        _store = new TranscribeModelStore(Path.Combine(host.PluginAssetDirectory, "models"), _client);
        var saved = host.GetSetting<string>("selectedModel");
        SelectedModelId = _catalog.Models.Any(value => value.Id == saved) ? saved : null;
        return Task.CompletedTask;
    }

    public void SelectModel(string modelId)
    {
        _ = _catalog.Get(modelId);
        _host?.SetSetting("selectedModel", modelId);
        SelectedModelId = modelId;
    }

    public string? GetLinkedModelPath(string modelId) => _store?.GetLinkedPath(_catalog.Get(modelId));
    public async Task LinkModelAsync(string modelId, string sourcePath, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await EnterGateAsync(ct, linked.Token).ConfigureAwait(false);
        try
        {
            if (_loadedModelId == modelId)
                throw new InvalidOperationException("Unload this model before replacing its files.");
            await Store.LinkAsync(_catalog.Get(modelId), sourcePath, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw CancellationFault(ex); }
        finally { _gate.Release(); }
    }

    public bool IsModelDownloaded(string modelId) => _store?.IsReady(_catalog.Get(modelId)) == true;
    public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try { await Store.DownloadAsync(_catalog.Get(modelId), progress, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw CancellationFault(ex); }
    }
    public async Task DeleteModelAsync(string modelId, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await EnterGateAsync(ct, linked.Token).ConfigureAwait(false);
        try
        {
            if (_loadedModelId == modelId) await UnloadCoreAsync().ConfigureAwait(false);
            await Store.DeleteAsync(_catalog.Get(modelId), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw CancellationFault(ex); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<TranscriptionDevice>> DiscoverDevicesAsync(string? modelId, CancellationToken ct)
    {
        if (modelId is not null) _ = _catalog.Get(modelId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await EnterGateAsync(ct, linked.Token).ConfigureAwait(false);
        try
        {
            if (_model is not null || _hardware is not null)
                throw new InvalidOperationException("Probe devices in a fresh worker, not a configured model worker.");
            return await Task.Run(() =>
            {
                linked.Token.ThrowIfCancellationRequested();
                _runtime ??= _runtimeFactory();
                _runtime.Initialize(null);
                var devices = _runtime.DiscoverDevices();
                linked.Token.ThrowIfCancellationRequested();
                return devices;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw CancellationFault(ex); }
        finally { _gate.Release(); }
    }

    public void SetAccelerationPreference(TranscriptionAccelerationPreference preference)
    {
        _ = preference switch
        {
            TranscriptionAccelerationPreference.Cpu => true,
            TranscriptionAccelerationPreference.Vulkan => true,
            TranscriptionAccelerationPreference.Auto => throw new ArgumentException("The host must resolve Auto before calling the legacy setter.", nameof(preference)),
            TranscriptionAccelerationPreference.NvidiaCuda or TranscriptionAccelerationPreference.AmdMigraphx or TranscriptionAccelerationPreference.WebGpu =>
                throw new NotSupportedException("This transcribe.cpp runtime supports CPU and Vulkan."),
            _ => throw new ArgumentOutOfRangeException(nameof(preference)),
        };
        AccelerationPreference = preference;
    }

    public void ConfigureHardware(TranscriptionHardwareRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_runtime is not null || _hardware is not null)
            throw new InvalidOperationException("Configure hardware once in a fresh worker before native initialization.");
        var expected = AccelerationPreference switch
        {
            TranscriptionAccelerationPreference.Cpu => TranscriptionAccelerationBackend.Cpu,
            TranscriptionAccelerationPreference.Vulkan => TranscriptionAccelerationBackend.Vulkan,
            TranscriptionAccelerationPreference.Auto or TranscriptionAccelerationPreference.NvidiaCuda
                or TranscriptionAccelerationPreference.AmdMigraphx or TranscriptionAccelerationPreference.WebGpu => throw new InvalidOperationException("Resolve a supported backend first."),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        if (request.Device.Backend != expected || !request.Device.IsAvailable
            || string.IsNullOrWhiteSpace(request.Device.StableId)
            || request.Device.Kind is TranscriptionDeviceKind.SoftwareGpu or TranscriptionDeviceKind.Unknown
            || (request.RequestedPreference != TranscriptionAccelerationPreference.Auto && request.RequestedPreference != AccelerationPreference))
            throw new ArgumentException("The configured device does not match the resolved hardware request.", nameof(request));
        _hardware = request;
    }

    public async Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await EnterGateAsync(ct, linked.Token).ConfigureAwait(false);
        try { await LoadCoreAsync(modelId, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw CancellationFault(ex); }
        finally { _gate.Release(); }
    }

    private async Task LoadCoreAsync(string modelId, CancellationToken ct)
    {
        if (_stream is not null) throw new InvalidOperationException("Finish or dispose the active streaming session first.");
        if (_model is not null && _loadedModelId == modelId) return;
        var definition = _catalog.Get(modelId);
        var request = _hardware ?? throw new InvalidOperationException("Configure exact hardware in an isolated worker before loading a model.");
        await UnloadCoreAsync().ConfigureAwait(false);
        var lease = await Store.AcquireAsync(definition, ct).ConfigureAwait(false);
        try
        {
            var model = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                _runtime ??= _runtimeFactory();
                _runtime.Initialize(request.Device.Backend);
                return _runtime.Load(lease.Path, definition, request.Device, lease.IsExternal);
            }, CancellationToken.None).ConfigureAwait(false);
            if (ct.IsCancellationRequested) { model.Dispose(); ct.ThrowIfCancellationRequested(); }
            _model = model;
            _loadedModelId = modelId;
            _lease = lease;
            AccelerationStatus = new(model.Device.Backend, $"Using {model.Device.Backend}: {model.Device.DisplayName}")
                { IsModelLoaded = true, ActiveDevice = model.Device };
        }
        catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var samples = TranscribeAudio.ReadWave(wavAudio);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await EnterGateAsync(ct, linked.Token).ConfigureAwait(false);
        try
        {
            var id = SelectedModelId ?? throw new InvalidOperationException("Select a model first.");
            var definition = _catalog.Get(id);
            language = ResolveLanguage(definition, language);
            if (translate && !definition.SupportsTranslation)
                throw new NotSupportedException("The selected model does not support translation.");
            await LoadCoreAsync(id, linked.Token).ConfigureAwait(false);
            var context = PluginTranscriptionPrompt.Parse(prompt);
            return await Task.Run(() =>
            {
                using var session = _model!.CreateSession();
                var texts = new List<string>();
                string? detected = null;
                foreach (var window in TranscribeAudio.Windows(samples, session.MaxAudioMs))
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var result = session.Run(window.ToArray(), language, translate, context.Text, context.DictionaryTerms, linked.Token);
                    texts.Add(result.Text.Trim());
                    detected ??= result.Language;
                }
                return new PluginTranscriptionResult(string.Join("\n", texts.Where(text => text.Length > 0)), detected, samples.Length / 16000d, NoSpeechProbability: null);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw CancellationFault(ex); }
        finally { _gate.Release(); }
    }

    private static string? ResolveLanguage(TranscribeModel definition, string? language)
    {
        language = string.IsNullOrWhiteSpace(language) || language == "auto" ? null : language;
        if (language is not null && !definition.Languages.Contains(language, StringComparer.Ordinal))
            throw new NotSupportedException($"{definition.Name} does not support language '{language}'.");
        if (language is null && !definition.SupportsLanguageDetection)
            return definition.Languages.Count == 1 ? definition.Languages[0]
                : throw new NotSupportedException("Select a language for this model; it does not support automatic detection.");
        return language;
    }

    private InvalidOperationException CancellationFault(OperationCanceledException ex) => new(
        _lifetime.IsCancellationRequested ? "The transcribe.cpp plugin was deactivated."
            : "A transcribe.cpp dependency cancelled without a caller request.", ex);

    private async Task EnterGateAsync(CancellationToken ct, CancellationToken linked)
    {
        try { await _gate.WaitAsync(linked).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw CancellationFault(ex); }
    }

    public async Task UnloadModelAsync()
    {
        _stream?.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await UnloadCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private async Task UnloadCoreAsync()
    {
        _stream?.DisposeUnderGate();
        _model?.Dispose();
        _model = null;
        _loadedModelId = null;
        AccelerationStatus = UnloadedStatus();
        if (_lease is not null) { await _lease.DisposeAsync().ConfigureAwait(false); _lease = null; }
    }
    private static TranscriptionAccelerationStatus UnloadedStatus() =>
        new(TranscriptionAccelerationBackend.Cpu, "No model loaded") { IsModelLoaded = false };

    public async Task DeactivateAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await UnloadModelAsync().ConfigureAwait(false);
        _host = null;
        _store = null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        UnloadModelAsync().GetAwaiter().GetResult();
        _runtime?.Dispose();
        _lifetime.Dispose();
        _client.Dispose();
        _gate.Dispose();
    }
}
