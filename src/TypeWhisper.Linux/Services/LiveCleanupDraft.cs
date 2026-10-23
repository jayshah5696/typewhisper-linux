using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services;

internal sealed class LiveCleanupDraft : IDisposable
{
    private readonly Lock _gate = new();
    private readonly LiveCleanupSession _session;
    private readonly LlmCleanupService _cleanup;
    private string? _capturedInput;
    private LlmCallCapture? _capture;
    private int _requests, _reused;
    public int RequestCount => Volatile.Read(ref _requests);
    public bool Reused => Volatile.Read(ref _reused) != 0;
    private CleanupLevel Level { get; }
    private string? Language { get; }
    private string Provider { get; }
    private ProfileStylePreset? StylePreset { get; }

    public LiveCleanupDraft(LlmCleanupService cleanup, CleanupLevel level, string? language,
        string provider, Func<string, CancellationToken, Task<string>> prepare,
        Action<string, string> publish, TimeSpan? debounce = null, ProfileStylePreset? stylePreset = null)
    {
        Level = level;
        _cleanup = cleanup;
        Language = language;
        Provider = provider;
        StylePreset = stylePreset;
        _session = new LiveCleanupSession(async (input, token) =>
        {
            Interlocked.Increment(ref _requests);
            var capture = new LlmCallCapture();
            var result = await cleanup.CleanWithResultAsync(input, level, capture: capture,
                ct: token, providerOverride: provider, preparedLightText: true,
                isDraft: true, sourceLanguage: language, stylePreset: stylePreset).ConfigureAwait(false);
            if (result.Outcome.FallbackReason is not null)
            {
                // A timed-out provider may still be running. Do not start another draft
                // in this recording after failure, even if it ignores cancellation.
                StopScheduling();
                return null;
            }
            lock (_gate) { _capturedInput = input; _capture = capture; }
            return result.Text;
        }, publish, debounce, prepare: async (input, token) =>
            _cleanup.PrepareLiveInput(await prepare(input, token).ConfigureAwait(false), language, stylePreset));
    }

    public void Submit(string input) => _session.Submit(input);
    public void StopScheduling() => _session.StopScheduling();
    public bool Matches(CleanupLevel level, string? language, string? provider, ProfileStylePreset? stylePreset = null) =>
        Level == level && string.Equals(Language, language, StringComparison.OrdinalIgnoreCase) && Provider == provider && StylePreset == stylePreset;
    public bool MatchesFinal(CleanupLevel level, string? provider, ProfileStylePreset? stylePreset = null) =>
        Level == level && Provider == provider && StylePreset == stylePreset;

    public async Task<string?> ReuseAsync(string input, LlmCallCapture? capture, CancellationToken token,
        string? finalLanguage = null, ProfileStylePreset? stylePreset = null)
    {
        // Reuse requires the exact text that reaches the LLM after all local passes.
        input = _cleanup.PrepareLiveInput(input, finalLanguage, stylePreset ?? StylePreset);
        var output = await _session.ReuseAsync(input, token).ConfigureAwait(false);
        if (output is null) return null;
        lock (_gate)
        {
            if (_capturedInput != input) return null;
            if (capture is not null && _capture is not null)
                foreach (var call in _capture.Calls) capture.Add(call);
        }
        Volatile.Write(ref _reused, 1);
        return output;
    }
    public void Dispose() => _session.Dispose();
}
