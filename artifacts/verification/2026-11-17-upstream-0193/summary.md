# Upstream 0.19.3 integration

Merged upstream/linux at cc476dad into the fork at 9d2662da. Merge commit 8c7e4762 has both parents. No conflicts occurred. The final upstream tree equals the v0.19.3 release tree after upstream reverted premature QA merges. Pending recovered PRs were not merged.

The merged delta is the app version, release documentation, and five plugin manifest versions. Fork features remain present. Upstream published v0.19.3 on November 17, 2026 with AppImage, tarball, DEB, and RPM assets. This task installs a source-built fork payload and does not publish another GitHub release.

## Verification

All required checks passed with .NET SDK 10.0.403. SDK 10.0.100 initially reported six CA1859 analyzer errors in existing tests. The supported roll-forward SDK resolved them without source changes.

- `just restore`
- `just pre-push`: lint, 95 verification tool tests, Release app/CLI/plugin build, 1394 core tests, 4064 plugin tests, and 197 CLI tests passed. Fourteen optional plugin model tests skipped.
- `just test-linux`: 4218 passed, two optional live tests skipped.
- `just test-integration`: 115 passed.
- `just ui-check artifacts/verification/2026-11-17-upstream-0193/ui`: 91 passed. About screenshot inspected and shows 0.19.3.
- `PATH=/home/jshah/.local/bin:$PATH uv run --no-project scripts/verification/recovery_smoke.py --out artifacts/verification/2026-11-17-upstream-0193/recovery --audio tests/fixtures/librispeech-1462-170145-0012.wav --plugin com.typewhisper.needle-whistle --engine needle-whistle --model whistle`: passed public audio, CPU execution, cleanup, History recovery controls, and private X11 clipboard read-back.
- `just upgrade artifacts/verification/2026-11-17-upstream-0193/upgrade`: publish and scratch upgrade passed seven preservation/launcher checks and nine Needle checks. Real installation completed. Automatic restart failed because the tool environment lacked DISPLAY. Restart with the current GNOME Xwayland display and authority succeeded.
- `just verify-plugins /home/jshah/.local/share/typewhisper-app /home/jshah/.local/share/TypeWhisper/Plugins`: all 40 plugins loaded, no type-load failures.
- `just doctor`: installed executable responds with app 0.19.3, PluginSDK 0.19.3.0, idle state, and installed plugin path. Startup log reports whisper.cpp base loaded using VULKAN. The isolated Needle check explicitly confirmed CPU execution.
- `lat check`: passed.

## User data

Recorded hashes for 314 files before upgrade. Model files, credential files, and History remained unchanged. History still contains 332 entries. Startup applied the existing 30-day capture retention and removed 16 audio captures whose History timestamps are October 16 and 17. Transcript records remain. Settings, settings backup, and Linux update preferences were rewritten during restart. The latest settings backup differs from current settings only in the selected microphone device.

The SDK was restored under /tmp/typewhisper-dotnet. Missing xclip and libXmu were installed into user-local verification tool paths. The ordinary live-smoke recipe declined to use the user's rich clipboard; the private recovery test completed instead.

Detailed logs, manifests, transcripts, screenshots, and local user-data hashes remain in this evidence directory. They are excluded from the committed report.
