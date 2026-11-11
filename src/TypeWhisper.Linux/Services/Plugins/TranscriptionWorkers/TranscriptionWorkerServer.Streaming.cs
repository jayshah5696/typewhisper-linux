using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;

internal static partial class TranscriptionWorkerServer
{
    private sealed partial class Session
    {
        private IStreamingSession? _stream;
        private string? _streamId;
        private long _streamSequence;
        private Action<StreamingTranscriptEvent>? _streamHandler;
        private readonly Lock _updatesSync = new();
        private readonly List<StreamingTranscriptEvent> _updates = [];
        private int _updateCharacters;
        private Exception? _streamFault;

        private async Task<TranscriptionWorkerMessage> ExecuteStreamAsync(
            TranscriptionWorkerMessage message, byte[] payload, CancellationToken ct)
        {
            if (message.Command == TranscriptionWorkerCommands.StreamStart)
            {
                if (_stream is not null || string.IsNullOrWhiteSpace(message.SessionId)
                    || message.SessionId.Length > 64 || message.Sequence != 0)
                    throw new InvalidOperationException("A fresh streaming session identity is required.");
                await EnsureModelAsync(message.ModelId, ct).ConfigureAwait(false);
                if (!engine.SupportsStreaming)
                    throw new NotSupportedException("The selected model does not support streaming.");
                _stream = message.LanguageHints is { } hints
                    ? await engine.StartStreamingWithLanguageHintsAndPromptAsync(hints, message.Prompt, ct).ConfigureAwait(false)
                    : await engine.StartStreamingAsync(message.Language, ct).ConfigureAwait(false);
                _streamId = message.SessionId;
                _streamSequence = 0;
                _streamFault = null;
                var id = _streamId;
                var active = _stream;
                _streamHandler = update =>
                {
                    lock (_updatesSync)
                    {
                        if (_streamId != id || _stream != active) return;
                        if (_updates.Count >= 64 || _updateCharacters + update.Text.Length > 1024 * 1024)
                        {
                            _streamFault = new InvalidDataException("Streaming transcript updates overflowed. Retain the recording for retry.");
                            return;
                        }
                        _updates.Add(update);
                        _updateCharacters += update.Text.Length;
                    }
                };
                _stream.TranscriptReceived += _streamHandler;
                return new TranscriptionWorkerMessage
                {
                    SessionId = _streamId,
                    Sequence = 0,
                    FinalizeTimeout = _stream is IStreamingSessionFinalizeTimeout budget ? budget.FinalizeTimeout : null,
                };
            }

            // Identity/order mistakes must not mutate or abandon the valid active session.
            if (_stream is null || message.SessionId != _streamId || message.Sequence != _streamSequence + 1)
                throw new InvalidOperationException("Stale session identity or out-of-order streaming audio.");
            _streamSequence = message.Sequence;
            try
            {
                switch (message.Command)
                {
                    case TranscriptionWorkerCommands.StreamFeed:
                        await _stream.SendAudioAsync(payload, ct).ConfigureAwait(false);
                        break;
                    case TranscriptionWorkerCommands.StreamFinalize:
                        await _stream.FinalizeAsync(ct).ConfigureAwait(false);
                        break;
                    case TranscriptionWorkerCommands.StreamDispose:
                        break;
                    default:
                        throw new NotSupportedException("Unknown streaming command.");
                }
                ct.ThrowIfCancellationRequested();
                StreamingTranscriptEvent[] updates;
                lock (_updatesSync)
                {
                    if (_streamFault is { } failure) throw failure;
                    updates = [.. _updates];
                    _updates.Clear();
                    _updateCharacters = 0;
                }
                var result = new TranscriptionWorkerMessage
                {
                    SessionId = _streamId, Sequence = _streamSequence, Updates = updates,
                };
                if (message.Command is TranscriptionWorkerCommands.StreamFinalize or TranscriptionWorkerCommands.StreamDispose)
                    await DisposeStreamAsync().ConfigureAwait(false);
                return result;
            }
            catch
            {
                await DisposeStreamAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async ValueTask DisposeStreamAsync()
        {
            var stream = _stream;
            var handler = _streamHandler;
            lock (_updatesSync)
            {
                _streamId = null;
                _updates.Clear();
                _updateCharacters = 0;
            }
            _stream = null;
            _streamHandler = null;
            if (stream is null) return;
            if (handler is not null) stream.TranscriptReceived -= handler;
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
