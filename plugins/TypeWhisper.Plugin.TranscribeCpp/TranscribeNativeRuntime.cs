using System.Runtime.InteropServices;
using System.Text.Json;
using TypeWhisper.PluginSDK.Models;
using static TypeWhisper.Plugin.TranscribeCpp.TranscribeNativeApi;

namespace TypeWhisper.Plugin.TranscribeCpp;

internal interface ITranscribeNativeRuntime : IDisposable
{
    void Initialize(TranscriptionAccelerationBackend? backend);
    IReadOnlyList<TranscriptionDevice> DiscoverDevices();
    ITranscribeNativeModel Load(string path, TranscribeModel definition, TranscriptionDevice device, bool external);
}

internal interface ITranscribeNativeModel : IDisposable
{
    TranscriptionDevice Device { get; }
    ITranscribeNativeSession CreateSession();
}

internal interface ITranscribeNativeSession : IDisposable
{
    long MaxAudioMs { get; }
    void Begin(string? language, string? context, IReadOnlyList<string> vocabulary, CancellationToken ct);
    NativeTranscript Feed(float[] samples, CancellationToken ct);
    NativeTranscript Finish(CancellationToken ct);
    NativeTranscript Run(float[] samples, string? language, bool translate, string? context, IReadOnlyList<string> vocabulary, CancellationToken ct);
}

internal sealed record NativeTranscript(string Text, string? Language);

internal sealed class TranscribeNativeRuntime : ITranscribeNativeRuntime
{
    private static readonly Lazy<IntPtr> s_library = new(LoadLibrary);
    private bool _initialized;

    public void Initialize(TranscriptionAccelerationBackend? backend)
    {
        // Fail closed even if isolation was disabled for diagnosis. Native driver registration must
        // never happen in the desktop process; the worker command is the host's explicit boundary.
        if (!Environment.GetCommandLineArgs().Contains("--transcription-worker", StringComparer.Ordinal))
            throw new NotSupportedException("transcribe.cpp native operations require a transcription worker.");
        if (_initialized) return;
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The bundled transcribe.cpp runtime supports Linux x64 only.");
        _ = s_library.Value;
        if (Text(transcribe_version()) != TranscribeModelCatalog.SupportedEngineVersion)
            throw new InvalidOperationException("The native transcribe.cpp version does not match the pinned binding.");
        VerifyLayout<LoadParams>(0);
        VerifyLayout<RunParams>(2);
        VerifyLayout<Capabilities>(4);
        VerifyLayout<StreamParams>(3);
        VerifyLayout<StreamUpdate>(9);
        VerifyLayout<SessionLimits>(11);
        VerifyLayout<DeviceInfo>(13);
        VerifyLayout<BackendParams>(15);
        var allowed = backend switch
        {
            null => 1u | 4u, // Optional CUDA is added only after its separate artifact is ready.
            TranscriptionAccelerationBackend.Cpu => 1u,
            TranscriptionAccelerationBackend.Vulkan => 1u | 4u,
            TranscriptionAccelerationBackend.NvidiaCuda => throw new NotSupportedException("The optional transcribe.cpp CUDA artifact is not installed."),
            TranscriptionAccelerationBackend.AmdMigraphx => throw new NotSupportedException("transcribe.cpp does not use MIGraphX."),
            TranscriptionAccelerationBackend.WebGpu => throw new NotSupportedException("transcribe.cpp does not use WebGPU."),
            _ => throw new ArgumentOutOfRangeException(nameof(backend)),
        };
        // _ex's mask is authoritative, while the environment also protects any earlier registration.
        Environment.SetEnvironmentVariable("TRANSCRIBE_BACKENDS", backend == TranscriptionAccelerationBackend.Cpu ? "cpu" : "cpu,vulkan");
        Environment.SetEnvironmentVariable("GGML_BACKEND_PATH", null);
        transcribe_backend_init_params_init(out var parameters);
        parameters.Allowed = allowed;
        CheckStatus(transcribe_init_backends_ex(parameters), "initialize backend modules");
        if ((transcribe_allowed_backends() & ~allowed) != 0)
            throw new InvalidOperationException("The native runtime registered an unrequested backend.");
        _initialized = true;
    }

    public IReadOnlyList<TranscriptionDevice> DiscoverDevices() => Devices().Select(value => value.Device).ToArray();

    public ITranscribeNativeModel Load(string path, TranscribeModel definition, TranscriptionDevice device, bool external)
    {
        var match = Devices().SingleOrDefault(value => value.Device.Backend == device.Backend && value.Device.StableId == device.StableId);
        if (match.Handle == IntPtr.Zero)
            throw new InvalidOperationException($"The requested device '{device.DisplayName}' is no longer available. Retry on CPU.");
        transcribe_model_load_params_init(out var parameters);
        parameters.Backend = device.Backend switch
        {
            TranscriptionAccelerationBackend.Cpu => 1,
            TranscriptionAccelerationBackend.Vulkan => 3,
            TranscriptionAccelerationBackend.NvidiaCuda => 5,
            TranscriptionAccelerationBackend.AmdMigraphx => throw new NotSupportedException("MIGraphX is not supported by transcribe.cpp."),
            TranscriptionAccelerationBackend.WebGpu => throw new NotSupportedException("WebGPU is not supported by transcribe.cpp."),
            _ => throw new ArgumentOutOfRangeException(nameof(device)),
        };
        parameters.Device = match.Handle; // Never pass the NULL automatic-selection sentinel.
        parameters.NoMmap = external ? (byte)1 : (byte)0;
        CheckStatus(transcribe_model_load_file(path, parameters, out var pointer), "load model");
        var handle = new NativeModelHandle(pointer);
        try
        {
            if (handle.IsInvalid) throw new InvalidOperationException("Native model load returned no model.");
            var actual = ReadDevice(transcribe_model_device(handle));
            if (actual is null || actual.Backend != device.Backend || actual.StableId != device.StableId)
                throw new InvalidOperationException("Native execution did not use the exact requested device.");
            if ((transcribe_model_roles(handle) & 1) == 0 || Text(transcribe_model_arch_string(handle)) != definition.Architecture)
                throw new InvalidDataException("The GGUF role or architecture does not match the reviewed catalog.");
            transcribe_capabilities_init(out var capabilities);
            CheckStatus(transcribe_model_get_capabilities(handle, ref capabilities), "read model capabilities");
            if (capabilities.SampleRate != TranscribeAudio.SampleRate
                || (definition.SupportsStreaming && capabilities.Streaming == 0)
                || (definition.SupportsTranslation && capabilities.Translation == 0)
                || (definition.SupportsLanguageDetection && capabilities.LanguageDetection == 0))
                throw new InvalidDataException("The native model capabilities do not match the reviewed catalog.");
            return new Model(handle, actual, capabilities);
        }
        catch { handle.Dispose(); throw; }
    }

    private List<(IntPtr Handle, TranscriptionDevice Device)> Devices()
    {
        if (!_initialized) throw new InvalidOperationException("Initialize the native runtime in a worker first.");
        var result = new List<(IntPtr, TranscriptionDevice)>();
        var count = transcribe_device_count();
        if (count is < 0 or > 256) throw new InvalidOperationException("Invalid native device count.");
        for (var index = 0; index < count; index++)
        {
            var pointer = transcribe_device_get(index);
            if (pointer != IntPtr.Zero && ReadDevice(pointer) is { } device) result.Add((pointer, device));
        }
        return result;
    }

    private static TranscriptionDevice? ReadDevice(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return null;
        transcribe_device_info_init(out var info);
        CheckStatus(transcribe_device_get_info(pointer, ref info), "read device identity");
        var name = Text(info.Description);
        if (name.Length == 0) name = Text(info.Name);
        return DescribeDevice(Text(info.Kind), Text(info.Id), name, info.Type);
    }

    internal static TranscriptionDevice? DescribeDevice(string backend, string? id, string name, int type)
    {
        if (backend == "cpu" && type == 0)
            return new(TranscriptionAccelerationBackend.Cpu, "cpu", name, TranscriptionDeviceKind.Cpu);
        if (string.IsNullOrWhiteSpace(id) || type is not (1 or 2)
            || name.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase)
            || name.Contains("lavapipe", StringComparison.OrdinalIgnoreCase)
            || name.Contains("software", StringComparison.OrdinalIgnoreCase)) return null;
        var kind = type == 2 ? TranscriptionDeviceKind.IntegratedGpu : TranscriptionDeviceKind.DiscreteGpu;
        return backend switch
        {
            "vulkan" => new(TranscriptionAccelerationBackend.Vulkan, id, name, kind),
            "cuda" => new(TranscriptionAccelerationBackend.NvidiaCuda, id, name, kind),
            _ => null,
        };
    }

    private static IntPtr LoadLibrary()
    {
        var directory = Path.Combine(Path.GetDirectoryName(typeof(TranscribeNativeRuntime).Assembly.Location)!, "native", "linux-x64");
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "contract.json")));
        if (contract.RootElement.GetProperty("version").GetString() != "0.4.1"
            || contract.RootElement.GetProperty("header_hash").GetString() != "f99ad4169374b8f3")
            throw new InvalidDataException("The transcribe.cpp native artifact contract does not match the pinned header.");
        var library = NativeLibrary.Load(Path.Combine(directory, "libtranscribe.so"));
        NativeLibrary.SetDllImportResolver(typeof(TranscribeNativeRuntime).Assembly,
            (name, _, _) => name == "typewhisper-transcribe" ? library : IntPtr.Zero);
        // Backend modules and this library remain process-local and pinned until worker exit.
        return library;
    }

    private static void VerifyLayout<T>(int id) where T : struct
    {
        if (transcribe_abi_struct_size(id) != (nuint)Marshal.SizeOf<T>() || transcribe_abi_struct_align(id) != 8)
            throw new InvalidOperationException($"The transcribe.cpp ABI layout for {typeof(T).Name} does not match the pinned binding.");
    }

    internal static void CheckStatus(int status, string operation)
    {
        if (status == 0) return;
        throw status switch
        {
            1 => new ArgumentException($"transcribe.cpp cannot {operation}: invalid argument."),
            3 => new FileNotFoundException($"transcribe.cpp cannot {operation}: file not found."),
            2 or 5 or 6 or 10 or 11 or 12 or 15 or 16 or 20 => new NotSupportedException($"transcribe.cpp cannot {operation}: unsupported request (status {status})."),
            17 or 18 or 19 => new InvalidDataException($"transcribe.cpp could not process the complete recording (status {status}). Audio must be retained for retry."),
            _ => new InvalidOperationException($"transcribe.cpp failed to {operation} (status {status})."),
        };
    }

    internal static void CheckCompletion(int status, bool wasTruncated, bool wasAborted, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CheckStatus(status, "transcribe audio");
        if (wasTruncated || wasAborted)
            throw new InvalidOperationException("transcribe.cpp returned an incomplete transcript. Retain the recording for retry.");
    }

    public void Dispose() { /* Native registry lifetime is the worker process, not a plugin instance. */ }

    private sealed class Model(NativeModelHandle handle, TranscriptionDevice device, Capabilities capabilities) : ITranscribeNativeModel
    {
        public TranscriptionDevice Device => device;
        public ITranscribeNativeSession CreateSession()
        {
            CheckStatus(transcribe_session_init(handle, IntPtr.Zero, out var pointer), "create session");
            var session = new NativeSessionHandle(pointer, handle);
            try { return new Session(session, handle, capabilities); }
            catch { session.Dispose(); throw; }
        }
        public void Dispose() => handle.Dispose();
    }

    private sealed class Session : ITranscribeNativeSession
    {
        private readonly NativeSessionHandle _session;
        private readonly NativeModelHandle _model;
        private readonly Capabilities _capabilities;
        public long MaxAudioMs { get; }
        internal Session(NativeSessionHandle session, NativeModelHandle model, Capabilities capabilities)
        {
            _session = session;
            _model = model;
            _capabilities = capabilities;
            transcribe_session_limits_init(out var limits);
            CheckStatus(transcribe_session_get_limits(session, ref limits), "read session limits");
            MaxAudioMs = limits.MaxAudioMs > 0 ? limits.MaxAudioMs : capabilities.MaxAudioMs;
        }

        private string? _language;

        public NativeTranscript Run(float[] samples, string? language, bool translate, string? context, IReadOnlyList<string> vocabulary, CancellationToken ct) =>
            WithParameters(language, translate, context, vocabulary,
                parameters => transcribe_run(_session, samples, samples.Length, parameters), ct);

        public void Begin(string? language, string? context, IReadOnlyList<string> vocabulary, CancellationToken ct)
        {
            if (_capabilities.Streaming == 0) throw new NotSupportedException("This model does not support native streaming.");
            transcribe_stream_params_init(out var stream);
            stream.CommitPolicy = 1; // ON_FINALIZE: native commitment must not freeze an incorrect preview.
            _ = WithParameters(language, false, context, vocabulary,
                parameters => transcribe_stream_begin(_session, parameters, stream), ct);
        }

        public NativeTranscript Feed(float[] samples, CancellationToken ct)
        {
            transcribe_stream_update_init(out var update);
            return Invoke(() => transcribe_stream_feed(_session, samples, samples.Length, ref update), ct);
        }

        public NativeTranscript Finish(CancellationToken ct)
        {
            transcribe_stream_update_init(out var update);
            return Invoke(() => transcribe_stream_finalize(_session, ref update), ct);
        }

        private NativeTranscript WithParameters(string? language, bool translate, string? context,
            IReadOnlyList<string> vocabulary, Func<RunParams, int> operation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _language = language;
            if (translate && _capabilities.Translation == 0) throw new NotSupportedException("This model does not support translation.");
            using var languageText = new Utf8Value(language);
            using var targetText = new Utf8Value(translate ? "en" : null);
            transcribe_run_params_init(out var parameters);
            parameters.Task = translate ? 1 : 0;
            parameters.Language = languageText.Pointer;
            parameters.TargetLanguage = targetText.Pointer;
            using var contextText = new Utf8Value(transcribe_model_supports(_model, 8) ? context : null);
            parameters.Prompt = contextText.Pointer;
            using var terms = new Utf8Vocabulary(transcribe_model_supports(_model, 7) ? vocabulary : []);
            parameters.Vocabulary = terms.Pointer;
            parameters.VocabularyCount = terms.Count;
            return Invoke(() => operation(parameters), ct);
        }

        private NativeTranscript Invoke(Func<int> operation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            AbortCallback abort = _ => ct.IsCancellationRequested;
            transcribe_set_abort_callback(_session, abort, IntPtr.Zero);
            try
            {
                var status = operation();
                CheckCompletion(status, transcribe_was_truncated(_session), transcribe_was_aborted(_session), ct);
                // All result strings are borrowed; copy them before the next run or session free.
                var detected = Text(transcribe_detected_language(_session));
                return new(Text(transcribe_full_text(_session)), detected.Length == 0 ? _language : detected);
            }
            finally
            {
                transcribe_set_abort_callback(_session, null, IntPtr.Zero);
                GC.KeepAlive(abort);
            }
        }
        public void Dispose() => _session.Dispose();
    }
}

internal sealed class Utf8Value(string? text) : IDisposable
{
    internal IntPtr Pointer { get; } = text is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(text);
    public void Dispose() => Marshal.FreeCoTaskMem(Pointer);
}

internal sealed class Utf8Vocabulary : IDisposable
{
    private readonly Utf8Value[] _values;
    internal IntPtr Pointer { get; }
    internal int Count => _values.Length;
    internal Utf8Vocabulary(IReadOnlyList<string> values)
    {
        _values = values.Select(value => new Utf8Value(value)).ToArray();
        if (_values.Length == 0) return;
        Pointer = Marshal.AllocCoTaskMem(checked(IntPtr.Size * _values.Length));
        for (var index = 0; index < _values.Length; index++)
            Marshal.WriteIntPtr(Pointer, index * IntPtr.Size, _values[index].Pointer);
    }
    public void Dispose()
    {
        foreach (var value in _values) value.Dispose();
        Marshal.FreeCoTaskMem(Pointer);
    }
}
