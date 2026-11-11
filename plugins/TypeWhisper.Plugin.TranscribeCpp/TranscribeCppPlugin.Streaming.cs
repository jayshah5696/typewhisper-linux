using System.Buffers.Binary;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.TranscribeCpp;

public sealed partial class TranscribeCppPlugin
{
    private NativeStreamingSession? _stream;

    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) => StartStreamAsync(language, null, ct);
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(IReadOnlyList<string> languageHints, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAndPromptAsync(languageHints, null, ct);
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(IReadOnlyList<string> languageHints, string? prompt, CancellationToken ct) =>
        StartStreamAsync(languageHints.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim(), prompt, ct);

    private async Task<IStreamingSession> StartStreamAsync(string? language, string? prompt, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await EnterGateAsync(ct, linked.Token).ConfigureAwait(false);
        try
        {
            var id = SelectedModelId ?? throw new InvalidOperationException("Select a model first.");
            var definition = _catalog.Get(id);
            if (!definition.SupportsStreaming) throw new NotSupportedException("The selected model does not support streaming.");
            language = ResolveLanguage(definition, language);
            await LoadCoreAsync(id, linked.Token).ConfigureAwait(false);
            var context = PluginTranscriptionPrompt.Parse(prompt);
            var session = await Task.Run(() =>
            {
                var native = _model!.CreateSession();
                try
                {
                    native.Begin(language, context.Text, context.DictionaryTerms, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    return new NativeStreamingSession(this, native, language, context.Text, context.DictionaryTerms);
                }
                catch { native.Dispose(); throw; }
            }, CancellationToken.None).ConfigureAwait(false);
            _stream = session;
            return session;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw CancellationFault(ex); }
        finally { _gate.Release(); }
    }

    private sealed class NativeStreamingSession : IStreamingSession, IStreamingSessionFinalizeTimeout
    {
        private readonly TranscribeCppPlugin _owner;
        private readonly ITranscribeNativeSession _native;
        private readonly string? _language;
        private readonly string? _context;
        private readonly IReadOnlyList<string> _vocabulary;
        private readonly Lock _sync = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationToken _stopToken;
        private readonly long _softLimit;
        private readonly long _hardLimit;
        private long _samples;
        private int _quietSamples;
        private readonly float[] _pending;
        private int _pendingCount;
        private bool _disposed;
        private string? _preview;
        public TimeSpan FinalizeTimeout => TimeSpan.FromSeconds(30);
        public event Action<StreamingTranscriptEvent>? TranscriptReceived;

        internal NativeStreamingSession(TranscribeCppPlugin owner, ITranscribeNativeSession native,
            string? language, string? context, IReadOnlyList<string> vocabulary)
        {
            _owner = owner;
            _native = native;
            _language = language;
            _context = context;
            _vocabulary = vocabulary;
            _stopToken = _stop.Token;
            // Leave headroom for finalize/right context and find silence before the hard limit.
            var maximum = (native.MaxAudioMs > 0 ? Math.Min(native.MaxAudioMs, 600_000) : 600_000) * 16;
            var reserve = Math.Min(16000, Math.Max(1, maximum / 10));
            _hardLimit = maximum - reserve;
            _softLimit = maximum - 2 * reserve;
            // Do not decode every capture frame. Keep at most 500 ms and flush the tail at EOF.
            _pending = new float[(int)Math.Min(8000, reserve)];
        }

        internal void Cancel()
        {
            lock (_sync) { if (!_disposed) _stop.Cancel(); }
        }

        public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (pcm16Audio.Length % 2 != 0) throw new ArgumentException("PCM16 input must contain complete samples.", nameof(pcm16Audio));
            // Conversion and native calls happen under the model gate. Task.Run is never canceled
            // early: unload must wait for native work to settle before freeing any handle.
            await ExecuteAsync(() =>
            {
                var bytes = pcm16Audio.Span;
                for (var offset = 0; offset < bytes.Length;)
                {
                    _operationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(_pending.Length - _pendingCount, (bytes.Length - offset) / 2);
                    if (_samples + _pendingCount + count > _hardLimit)
                        throw new InvalidOperationException("No safe silence boundary before the model's streaming limit. Retain the complete recording for batch retry.");
                    for (var index = 0; index < count; index++)
                    {
                        var sample = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(offset + 2 * index, 2)) / 32768f;
                        _pending[_pendingCount++] = sample;
                        _quietSamples = Math.Abs(sample) <= 0.01f ? _quietSamples + 1 : 0;
                    }
                    offset += count * 2;
                    if (_pendingCount == _pending.Length) FlushAudio();
                }
            }, finish: false, ct).ConfigureAwait(false);
        }

        private void FlushAudio()
        {
            if (_pendingCount == 0) return;
            var result = _native.Feed(_pendingCount == _pending.Length ? _pending : _pending[.._pendingCount], _operationToken);
            _samples += _pendingCount;
            _pendingCount = 0;
            if (result.Text != _preview)
            {
                _preview = result.Text;
                TranscriptReceived?.Invoke(new(result.Text, false) { DetectedLanguage = result.Language });
            }
            if (_samples >= _softLimit && _quietSamples >= 2560)
            {
                PublishFinal(_native.Finish(_operationToken));
                _native.Begin(_language, _context, _vocabulary, _operationToken);
                _samples = 0;
                _quietSamples = 0;
                _preview = null;
            }
        }

        // Used only under the owner's gate and cleared before it is released.
        private CancellationToken _operationToken;

        public Task FinalizeAsync(CancellationToken ct) => ExecuteAsync(() =>
        {
            FlushAudio();
            if (_samples > 0) PublishFinal(_native.Finish(_operationToken));
        }, finish: true, ct);

        private void PublishFinal(NativeTranscript transcript) =>
            TranscriptReceived?.Invoke(new(transcript.Text, true) { DetectedLanguage = transcript.Language });

        private async Task ExecuteAsync(Action operation, bool finish, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopToken, _owner._lifetime.Token);
            await _owner.EnterGateAsync(ct, linked.Token).ConfigureAwait(false);
            try
            {
                if (_disposed || _owner._stream != this)
                    throw new InvalidOperationException("The native streaming session is no longer active.");
                _operationToken = linked.Token;
                await Task.Run(operation, CancellationToken.None).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (finish) DisposeUnderGate();
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                DisposeUnderGate();
                throw _owner.CancellationFault(ex);
            }
            catch { DisposeUnderGate(); throw; }
            finally { _operationToken = default; _owner._gate.Release(); }
        }

        internal void DisposeUnderGate()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _stop.Cancel();
                _stop.Dispose();
            }
            _native.Dispose();
            if (_owner._stream == this) _owner._stream = null;
        }

        public async ValueTask DisposeAsync()
        {
            Cancel();
            await _owner._gate.WaitAsync().ConfigureAwait(false);
            try { DisposeUnderGate(); }
            finally { _owner._gate.Release(); }
        }
    }
}
