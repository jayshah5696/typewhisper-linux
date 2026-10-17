# Recover a dictation

Use History when a transcript is correct but its cleanup needs another attempt. This does not require the recording to be available.

1. Open History and expand the transcript you want to recover.
2. Use Inspect / view source to compare the original transcript with the saved result.
3. Select Copy original to recover the exact original transcript, or select Clean up again to preview another cleanup attempt.
4. Review the new result, then select Copy new result.

The preview appears inside the History entry. The new result goes to the clipboard only when you select Copy new result. The previous result stays in History. Dismiss clears the preview. Collapsing the entry, editing it, or leaving History cancels pending cleanup and clears the preview.

The recent-transcriptions picker is still available through a shortcut under Settings, then Shortcuts. It provides quick access from another app. In that picker, Insert previous result or Enter uses the existing insertion behavior.

Clean up again uses the current cleanup provider. Cloud providers can charge for the request. It uses High cleanup if that level was recorded for the original attempt. Otherwise, it uses Medium cleanup. It repeats only cleanup. It does not run transcription, translation, profile prompt actions, snippets, or plugin actions again.

Expanding another History entry cancels the pending cleanup attempt. A late response cannot restore the dismissed preview or copy text.

If AI cleanup times out, fails, returns no text, or has no provider, the palette shows the reason and previews basic cleanup. It does not copy that fallback automatically. New dictations also show the reason in completion feedback and in their expanded History entry.

Older entries can have no recorded cleanup outcome. The app does not infer an outcome for them. If an entry has no original transcript, its original-copy and cleanup actions are disabled. Spoken command entries do not offer these actions because their original text can be command input rather than a transcript.

Failed audio recognition still uses the separate Retry action in History, which requires the saved recording.

## API outcome

`POST /v1/text/process` reports the actual applied level in `cleanup_level`. When cleanup ran, `cleanup_outcome` includes `requested_level`, `applied_level`, and `fallback_reason`. A null reason means that the requested cleanup completed. A null outcome means that no cleanup outcome was recorded.

Fallback reasons are `ProviderUnavailable`, `TimedOut`, `ProviderFailed`, and `EmptyResponse`. These fields do not depend on optional LLM provenance capture.
