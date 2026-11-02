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

## Speculative and Final Cleanup

Live text refinement runs speculatively during speech, while final cleanup applies authoritative prompt templates and LLM formatting on dictation end.

- **Speculative live cleanup**: Produces real-time draft suggestions for responsive visual feedback in the floating overlay. Discarded live drafts never record snippet statistics, increment dictionary counters, or pollute error logs.
- **Final cleanup**: When the user releases the dictation hotkey, final cleanup passes the authoritative transcript to the configured processing provider. It applies punctuation restoration, custom vocabulary substitutions, and LLM formatting rules before delivery.

## Text Delivery and Clipboard Handling

Completed text is delivered to target applications using synthetic keypress simulation or clipboard paste operations.

On Linux desktops, input delivery interacts with the active display server:
- On X11, the app places text onto the system clipboard and dispatches paste shortcuts via tools such as `xdotool` or `xclip`.
- Destination applications receive text without corrupting existing clipboard contents by restoring the prior clipboard payload after paste completion.

## Dictation Recovery and History

History and recovery retain completed transcripts when delivery fails, subject to the user's retention and recording settings. They do not guarantee recovery of every interrupted capture or failed recognition.

If an application rejects simulated keypresses or loses focus during paste, the user can open the History window or click the recovery overlay to copy or re-insert the recognized speech. The recovery mechanism is verified end-to-end via [[verification#Virtual Audio and Needle Smoke]].
