# Dictation Pipeline

The dictation pipeline transforms microphone audio into typed text through voice activity detection, speech recognition, speculative text cleanup, and destination input delivery.

The pipeline spans components across [[src/TypeWhisper.Core]] and desktop input injectors in [[src/TypeWhisper.Linux]].

## Audio Capture and VAD

Continuous audio streaming relies on platform capture backends paired with voice activity detection to isolate speech segments from ambient noise.

The capture layer records 16 kHz mono PCM audio buffers. When voice activity begins, buffers accumulate into speech utterances. Silence detection triggers chunk finalization while avoiding mid-word audio clipping.

## Speech to Text Routing

Audio segments are routed to the active ASR provider chosen in user configuration or overridden for specific application profiles.

Providers defined in [[plugin-system#ASR and Transcription Providers]] process the audio:
- Local offline engines (such as Needle Whistle or Sherpa-ONNX) perform immediate on-device inference without external network calls.
- Cloud providers (such as Deepgram, AssemblyAI, or Gladia) stream or upload audio over authenticated HTTPS endpoints.

## Streaming preview snapshots

Streaming previews replace the previous hypothesis. Polling previews retain their separate stabilization policy.

[[src/TypeWhisper.Linux/Services/StreamingTranscriptionCoordinator.cs|The coordinator]] joins completed segments with the current hypothesis. [[src/TypeWhisper.Linux/Services/StreamingTranscriptState.cs|The transcript state]] accepts that complete snapshot without splicing an obsolete prefix into a correction. Empty snapshots clear a retracted preview. Session versions and commit revisions reject stale correction work in both modes. [[tests/TypeWhisper.Linux.Tests/StreamingTranscriptStateTests.cs|Focused tests]] cover corrections, retractions and concurrent callbacks.

## Speculative and Final Cleanup

Live text refinement runs speculatively during speech, while final cleanup applies authoritative prompt templates and LLM formatting on dictation end.

- **Speculative live cleanup**: Produces real-time draft suggestions for responsive visual feedback in the floating overlay. Discarded live drafts never record snippet statistics, increment dictionary counters, or pollute error logs.
- **Final cleanup**: When the user releases the dictation hotkey, [[src/TypeWhisper.Linux/Services/DictationOrchestrator.Recovery.cs]] passes the authoritative transcript and any opt-in screen or clipboard reference context to [[src/TypeWhisper.Linux/Services/LlmCleanupService.cs]], applying punctuation restoration, custom vocabulary substitutions, and LLM formatting rules before delivery.

## Text Delivery and Clipboard Handling

Completed text is delivered to target applications using synthetic keypress simulation or clipboard paste operations.

On Linux desktops, input delivery interacts with the active display server:
- On X11, the app places text onto the system clipboard and dispatches paste shortcuts via tools such as `xdotool` or `xclip`.
- In [[src/TypeWhisper.Linux/Services/TextInsertionService.cs]], non-ASCII text falls back to clipboard paste when no Unicode-capable typing backend (`xdotool` or `wtype`) is available, avoiding dropped glyphs in `ydotool`.
- Destination applications receive text without corrupting existing clipboard contents by restoring the prior clipboard payload after paste completion.

[[src/TypeWhisper.Linux/Services/Insertion/RichClipboardWriter.cs|Rich clipboard restoration]] republishes captured X11 MIME payloads after Auto or Clipboard paste. [[src/TypeWhisper.Linux/Services/TextInsertionService.cs|Text insertion]] avoids wtype for XWayland targets and reports clipboard fallback when safe direct Unicode typing is unavailable.

## Dictation Recovery and History

History and recovery retain completed transcripts when delivery fails, subject to the user's retention and recording settings. They do not guarantee recovery of every interrupted capture or failed recognition.

If an application rejects simulated keypresses or loses focus during paste, the user can open the History window or click the recovery overlay to copy or re-insert the recognized speech. The recovery mechanism is verified end-to-end via [[verification#Virtual Audio and Needle Smoke]].
