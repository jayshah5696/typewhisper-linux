# Verification and Testing

Verification in TypeWhisper Linux enforces strict behavioral, performance, and user-data guarantees across five distinct verification layers.

Workflows and command recipes are maintained in the root [[justfile]] and orchestrated via `.agents/skills/verify-typewhisper-linux`.

## Five Verification Layers

Different testing layers answer specific correctness questions, and no single layer can substitute for another.

| Layer | Question Answered | Primary Tool or Recipe |
|---|---|---|
| **Unit & Integration Tests** | Is the code logically correct? | `just ci`, `just test-core`, `just test-linux` |
| **Live Verification** | Does the real user path function in an active app? | `just live-smoke`, `just verify-recovery` |
| **UI Verification** | Are all controls rendered, readable, and accessible? | `just ui-check`, `just ui-live` |
| **Quality Evals** | Is the recognized and transformed text accurate? | `just eval-capture`, `just eval-score` |
| **Performance** | How fast is inference and where is time spent? | `just benchmark`, `just perf` |

## Isolated Instance Harness

The isolated test harness in [[scripts/verification/isolated_app.py#IsolatedApp]] launches a sandboxed instance of TypeWhisper Linux.

The harness ensures complete isolation from user data:
- Sets dedicated temporary directories for `HOME`, `XDG_DATA_HOME`, `XDG_CONFIG_HOME`, and `XDG_STATE_HOME`.
- Allocates an ephemeral REST API port and authentication token.
- Optionally wires a PulseAudio null sink using [[scripts/verification/isolated_app.py#VirtualMic]].
- Never mutates production configuration, dictation history, or saved user credentials.

## Virtual Audio and Needle Smoke

Automated end-to-end tests stream known audio through virtual microphone devices to verify audio transcription and delivery.

The `just verify-needle` recipe plays fixture audio from `tests/fixtures/` directly into a virtual microphone. The test inspects the responding process, asserts successful audio recognition through the Needle Whistle model, and reads delivered text back from a private X11 clipboard.

## UI and Accessibility Checks

Rendered UI tests inspect window accessibility trees, check control naming, and detect clipped or raw localized strings.

The `just ui-check` recipe launches the Avalonia desktop interface against headless render buffers or virtual X11/Mutter displays:
- Verifies that all buttons, sliders, and input fields possess valid automation names.
- Flags unlocalized raw resource keys or clipped text labels.
- Saves rendered screenshot artifacts to `artifacts/ui-snapshots/`.

[[scripts/verification/flow_control_smoke.py#run]] operates the floating controls and verifies the result in a private GTK text field. [[scripts/verification/quiet_dock_smoke.py#run]] verifies all four dock edges, persistence after restart, finishing cancellation, and clipboard recovery. [[scripts/verification/overlay_click_through.py]] checks that neighboring clicks reach the underlying window.

## Quality Evals and Benchmarks

Evaluation suites in [[evals/dictation]] and benchmarking recipes measure transcription speed, latency distribution, and text quality.

- `just benchmark` runs repeatable speed comparisons across ASR engines (e.g. Sherpa-ONNX, Parakeet, Whisper.cpp) and execution backends (CPU, Vulkan, WebGPU).
- Quality evaluation tools record real audio transcripts, execute deterministic rule evaluation, and calculate word error rates (WER) against reference text.

## Static code guideline checks

Compile-time Roslyn analyzers in [[Directory.Build.props]] and [[.editorconfig]] combine with [[scripts/verification/code_guidelines_lint.py#lint_repository]] to enforce repository coding rules before every push.

- [[scripts/verification/code_guidelines_lint.py#main]] runs via `just lint` and checks exhaustive enum `switch` expressions and `switch` statements with `BAML001`, explicit `TimeSpan` durations on delays, timers, and waits with `BAML002`, specific exception types with `BAML003`, named `[GeneratedRegex]` methods with `BAML004`, and 30 required compiler and Roslyn analyzer error codes with `BAML005`.
- [[tests/verification/test_code_guidelines_lint.py#CodeGuidelinesLintTests]] verifies each rule against synthetic violations and confirms the repository tree has zero findings.
- [[.githooks/pre-push]] runs `just pre-push` so `just lint`, `just eval-tests`, `just build-release`, and the fast unit test suites pass before code is pushed.

