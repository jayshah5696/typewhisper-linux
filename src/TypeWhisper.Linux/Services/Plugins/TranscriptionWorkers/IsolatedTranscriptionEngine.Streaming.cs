using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;

internal sealed partial class IsolatedTranscriptionEngine
{
    private WorkerStreamingSession? _activeStream;

    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamAsync(new TranscriptionWorkerMessage { Language = language }, ct);

    public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(IReadOnlyList<string> languageHints, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAndPromptAsync(languageHints, null, ct);

    public Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(IReadOnlyList<string> languageHints, string? prompt, CancellationToken ct) =>
        StartStreamAsync(new TranscriptionWorkerMessage { LanguageHints = [.. languageHints], Prompt = prompt }, ct);

    private async Task<IStreamingSession> StartStreamAsync(TranscriptionWorkerMessage request, CancellationToken ct)
    {
        if (!SupportsStreaming) throw new NotSupportedException("The selected model does not support streaming.");
        var response = await RunAsync(request with
        {
            Command = TranscriptionWorkerCommands.StreamStart,
            SessionId = Guid.NewGuid().ToString("N"),
            ModelId = SelectedModelId,
        }, default, ct).ConfigureAwait(false);
        return _activeStream is { } active && active.SessionId == response.SessionId
            ? active : throw new InvalidOperationException("The streaming session was stopped while starting.");
    }

    private TranscriptionWorkerMessage AcceptStreamStart(TranscriptionWorkerMessage response,
        TranscriptionWorkerMessage request, CancellationToken ct, CancellationToken stop)
    {
        if (request.Command != TranscriptionWorkerCommands.StreamStart) return response;
        if (response.SessionId != request.SessionId || response.Sequence != 0)
            throw new InvalidDataException("The worker returned an invalid streaming identity.");
        _activeStream = new WorkerStreamingSession(this, response, ct, stop);
        return response;
    }

    // Each feed waits for its response before accepting more audio. The coordinator owns the
    // bounded capture queue; this transport adds no unbounded queue or background audio copies.
    private sealed class WorkerStreamingSession : IStreamingSession, IStreamingSessionFinalizeTimeout
    {
        private readonly IsolatedTranscriptionEngine _owner;
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationToken _token;
        private readonly CancellationTokenRegistration _canceled;
        private long _sequence;
        private int _invalid;
        private int _disposed;
        private readonly TaskCompletionSource _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string SessionId { get; }
        public TimeSpan FinalizeTimeout { get; }
        public event Action<StreamingTranscriptEvent>? TranscriptReceived;

        internal WorkerStreamingSession(IsolatedTranscriptionEngine owner, TranscriptionWorkerMessage start,
            CancellationToken caller, CancellationToken stop)
        {
            _owner = owner;
            SessionId = start.SessionId!;
            FinalizeTimeout = start.FinalizeTimeout ?? TimeSpan.FromSeconds(30);
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller, stop);
            _token = _lifetime.Token;
            _canceled = _token.Register(() => _ = DisposeAsync().AsTask());
        }

        internal void Invalidate() => Interlocked.Exchange(ref _invalid, 1);

        public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (pcm16Audio.Length % 2 != 0) throw new ArgumentException("PCM16 audio must contain complete samples.", nameof(pcm16Audio));
            if (pcm16Audio.IsEmpty) return;
            for (var offset = 0; offset < pcm16Audio.Length; offset += TranscriptionWorkerProtocol.MaximumStreamChunkBytes)
            {
                var size = Math.Min(TranscriptionWorkerProtocol.MaximumStreamChunkBytes, pcm16Audio.Length - offset);
                await SendAsync(TranscriptionWorkerCommands.StreamFeed, pcm16Audio.Slice(offset, size), ct).ConfigureAwait(false);
            }
        }

        public Task FinalizeAsync(CancellationToken ct) => SendAsync(TranscriptionWorkerCommands.StreamFinalize, default, ct);

        private async Task SendAsync(string command, ReadOnlyMemory<byte> audio, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _invalid) != 0)
                throw new InvalidOperationException("The streaming session is no longer active.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _token);
            var entered = false;
            try
            {
                await _owner._gate.WaitAsync(linked.Token).ConfigureAwait(false);
                entered = true;
                if (Volatile.Read(ref _invalid) != 0 || _owner._activeStream != this || _owner._worker is not { IsAlive: true } worker)
                    throw new InvalidOperationException("The streaming session is no longer active. Retain the complete recording for retry.");
                var sequence = ++_sequence;
                // Do not use the batch retry loop: replaying only this chunk loses earlier audio.
                var response = await worker.SendAsync(new TranscriptionWorkerMessage
                {
                    Command = command, SessionId = SessionId, Sequence = sequence,
                }, audio, linked.Token).ConfigureAwait(false);
                if (response.Error is { } error) throw TranscriptionWorkerProtocol.ToException(error);
                if (response.SessionId != SessionId || response.Sequence != sequence)
                    throw new InvalidDataException("The worker returned a stale streaming update.");
                linked.Token.ThrowIfCancellationRequested();
                if (response.State is { } state) _owner.SetState(state, worker.Acceleration);
                foreach (var update in response.Updates ?? [])
                {
                    if (Volatile.Read(ref _invalid) != 0) break;
                    TranscriptReceived?.Invoke(update);
                }
                if (command == TranscriptionWorkerCommands.StreamFinalize)
                {
                    Invalidate();
                    _owner._activeStream = null;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (entered && _owner._activeStream == this)
                    await _owner.DiscardWorkerAsync().ConfigureAwait(false);
                if (ct.IsCancellationRequested) throw new OperationCanceledException("The streaming request was canceled.", ex, ct);
                if (ex is OperationCanceledException)
                    throw new InvalidOperationException("The streaming session was stopped before it finished.", ex);
                if (ex is TranscriptionWorkerCrashedException)
                    throw new InvalidOperationException("The streaming worker stopped. Retain the complete recording for batch retry.", ex);
                throw;
            }
            finally { if (entered) _owner._gate.Release(); }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _ = CompleteDisposalAsync();
            return new ValueTask(_disposal.Task);
        }

        private async Task CompleteDisposalAsync()
        {
            try
            {
                await DisposeCoreAsync().ConfigureAwait(false);
                _disposal.TrySetResult();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _disposal.TrySetException(ex);
            }
        }

        private async Task DisposeCoreAsync()
        {
            Invalidate();
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _owner._gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_owner._activeStream != this) return;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var response = await _owner._worker!.SendAsync(new TranscriptionWorkerMessage
                    {
                        Command = TranscriptionWorkerCommands.StreamDispose, SessionId = SessionId, Sequence = ++_sequence,
                    }, default, timeout.Token).ConfigureAwait(false);
                    if (response.Error is not null) await _owner.DiscardWorkerAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    await _owner.DiscardWorkerAsync().ConfigureAwait(false);
                }
                _owner._activeStream = null;
            }
            finally
            {
                _owner._gate.Release();
                _canceled.Dispose();
                _lifetime.Dispose();
            }
        }
    }
}
