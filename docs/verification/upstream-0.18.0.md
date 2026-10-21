# Upstream 0.18.0 integration

This merge brings upstream tag `v0.18.0`, commit `e6be760b`, into our `linux` branch. The previous fork head was `915a739b`. It includes 34 upstream commits.

## Fork behavior preserved

The conflict resolutions keep our History recovery controls, explicit cleanup outcomes, live cleanup, terminal-safe selection capture and target-window checks. They also retain our GPU choices and ChatGPT model-discovery contract. The recent-transcriptions service receives both our cleanup service and upstream's session-activity monitor.

The merged app and bundled plugins report version 0.18.0. The upstream upgrade notes remain in `docs/releases/v0.18.0.md`.

## Fixes found during verification

- The new microphone and support-report status strings did not refresh after an interface-language change. They now refresh without losing the active microphone test or the last result. Three regression cases cover running and stopped tests and a failed support-report export.
- The new paste-last shortcut field and diagnostic-log switch had no accessible names. Both now use their localized labels.
- A fresh WebGPU worker exposed an existing native-loader ordering error. The C++ library depends on the C API library. Loading the C API first allows the bundled WebGPU runtime to load without an external library search path.
- The dashboard day-rollover test could race asynchronous History loading. It now awaits History loading before checking rollover and disposal. No production dashboard behavior changed.

## Results

`just ci` passed with the local SDK on `PATH`. It built Release with zero warnings and zero errors.

- Core passed 1,253 tests.
- Plugins passed 3,519 tests, with 10 optional live-model tests skipped.
- Linux passed 3,348 tests, with two optional live checks skipped.
- CLI passed 197 tests.
- Integration passed 92 tests.
- Rendered UI checks passed five tests across four interface languages and two window sizes.
- `just eval-tests` passed 66 verification-tool tests without model calls.

The separate real Sherpa worker test passed. It compared the worker with the in-process engine, killed the worker during decode, froze a worker and checked recovery, and cancelled native decoding. Cancellation returned in 322 ms. The long repeated-audio fixture produced empty-chunk warnings in both the reference and worker. This comparison does not establish long-recording recognition quality.

The isolated backend checks transcribed the public clip `1462-170145-0012.wav` as "He bent his face over her hair." They confirmed a separate `--transcription-worker` child and the active backend after inference for Whisper base on CPU, Whisper base on Vulkan, and Sherpa Parakeet on WebGPU. These checks verify reported backend selection and successful inference. They do not measure the share of work performed on the GPU.

The microphone UI check clicked Test microphone, supplied public audio through `tw_verify_fixture.monitor`, observed Audio signal received, and clicked Stop test. It created no recording.

The final History recovery check passed all 20 checks. It started with public-audio dictation, expanded its actual History entry, read both copy actions from a private X11 clipboard, checked cancellation and dismissal, and verified that preview cleanup did not overwrite History.

The separate live-cleanup check passed all five checks on private X11. One draft request reached the local fake provider before stop, its result was reused, and clipboard read-back matched the final text. For that single run, transcription took 183 ms, the final cleanup stage took 1.5 ms because it reused the draft, delivery took 269 ms, and stop-to-delivery took 823 ms. The fake provider's configured response delay was 250 ms. These numbers are not a model-quality evaluation or a before/after performance comparison.

The user's running app was left running. Its settings, models, credentials, recordings and History were not changed. Isolated microphone sources and runtime directories were removed after the checks. The pre-existing `.audit/` directory was not staged.

## Repeat the checks

Use a fresh evidence directory for the History recovery recipe. The backend scripts expect the public clip under `/tmp/typewhisper-benchmark` and existing local Whisper base and Sherpa Parakeet models. They copy model data into isolated instances.

```bash
export PATH="/tmp/typewhisper-dotnet:$PATH"
export DOTNET_ROOT=/tmp/typewhisper-dotnet

# Generate the optional native payload before checking WebGPU.
bash scripts/build-sherpa-webgpu.sh
just ci
just eval-tests

# xclip and xdotool must be available. On this host they were extracted locally.
export PATH="/tmp/pr137-tools/xclip/usr/bin:/tmp/typewhisper-verify-tools/usr/bin:$PATH"
export LD_LIBRARY_PATH="/tmp/pr137-tools/xclip/usr/lib:/tmp/typewhisper-verify-tools/usr/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

just verify-recovery artifacts/verification/upstream-0.18.0/recovery-repeat
uv run --no-project artifacts/verification/upstream-0.18.0/check_backends.py
uv run --no-project artifacts/verification/upstream-0.18.0/check_microphone.py
uv run --no-project artifacts/verification/upstream-0.18.0/check_live_cleanup.py
```

The real-worker fault test uses the environment variables documented in `tests/TypeWhisper.Linux.Tests/TranscriptionWorkerLiveTests.cs`. Set them to a scratch copy of PluginData, the Release Plugins directory and a public WAV directory. Give the scratch root a short path because it holds Unix sockets. Filter the test to `DisplayName~AWorkerTranscribesLikeTheEngineInProcessAndSurvivesNativeFaults&DisplayName~sherpa` when only the Parakeet model is available. The Whisper case expects the small model, which was not installed here.

Local logs, JSON results, screenshots and isolated data are under `artifacts/verification/upstream-0.18.0/`. Only the three repeatable check scripts are committed from that directory. The generated instance directories contain disposable API tokens and are not committed.

## Remaining limits

Cloud APIs were not exercised with account credentials. CUDA and MIGraphX were not verified on compatible hardware. The package formats and distributed plugin ZIPs were not rebuilt or smoke-tested for this merge.

The generic hidden Wayland live-smoke command failed at clipboard delivery, including after making the clipboard tools available. Its transcription and draft-cleanup checks succeeded. The private X11 alternative completed the same audio, cleanup and clipboard path successfully. Native Wayland clipboard delivery remains unverified by this run.

The other new upstream controls have unit and rendered-UI coverage, but this run did not exercise every export dialog, memory editor, CLI profile or provider setting through the app UI. No cloud-provider behavior or full release-packaging readiness is claimed.
