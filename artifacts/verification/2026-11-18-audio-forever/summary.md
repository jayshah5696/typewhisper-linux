# Unlimited dictation audio retention

Audio retention accepts zero for forever. Settings load, save, update, and the Dictation editor preserve zero. Capture cleanup returns before age and orphan pruning in this mode. Positive day counts and minus one retain their previous behavior. All six interface languages explain the new value and the effect of deleting History entries.

The local installed app uses audio retention zero. History retention was already Forever and remains so. Explicit History deletion can still remove the associated audio. Other users keep their existing retention settings and the default remains 30 days.

## Verification

Tests were written first. The initial focused run failed in the zero-value editor case and both old-capture cases. After implementation, the following passed with SDK 10.0.403:

- `just pre-push`: lint, 95 verification tool tests, Release build, 1394 core tests, 4064 plugin tests, and 197 CLI tests. Fourteen optional plugin tests skipped.
- `just test-linux`: 4220 passed, two optional tests skipped.
- `just ui-check artifacts/verification/2026-11-18-audio-forever/ui`: 91 passed.
- `uv run --no-project artifacts/verification/2026-11-18-audio-forever/verify_forever.py`: edited the actual Settings control to zero in an isolated hidden desktop, checked the saved setting, and preserved a two-year-old unreferenced capture through shutdown, startup, and another shutdown.
- `just upgrade artifacts/verification/2026-11-18-audio-forever/upgrade`, with the current GNOME display environment: scratch upgrade preservation, public audio through Needle on CPU with private clipboard read-back, installed app restart, and all 40 installed plugin loads passed.
- Compared all 259 audio files with the private backup after installation. None changed or disappeared. Persisted audio retention is zero and History retention is Forever.
- `just doctor`: installed 0.19.3 app responds and is idle.
- `lat check` and `git diff --check`: passed.

The original audio and settings backup is private under `~/.local/state/typewhisper/audio-retention-backup-20261118/`. Logs, synthetic fixture audio, screenshots, and detailed results remain local in this evidence directory.
