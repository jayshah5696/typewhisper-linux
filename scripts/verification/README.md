# Performance verification

## Recovery verification

Run `just doctor`, then `just build-release` and `just verify-recovery <new-output-directory>` for dictation recovery changes. The recovery helper uses the hidden desktop dependencies plus `xclip`, `xdotool`, and system Python with GI and AT-SPI bindings. It defaults to the existing public clip under `/tmp/typewhisper-benchmark/`; run `just prepare-corpus` if that clip is missing.

The helper starts an isolated Release app and copies the existing Sherpa model without modifying the source files. It records only a virtual microphone. It verifies public audio through dictation and saved History, then opens the recovery palette through the actual History button. Both copy actions must match text read back from the private X11 clipboard. A separate clipboard value detects unwanted copying during preview, cancellation, or timeout. The original history file must remain unchanged.

Screenshots, the actual backend, separate stage timings, and check results go into the output directory. Every required check must pass for the command to succeed. The cleanup endpoint is a local fake provider, so this proves behavior and routing rather than LLM writing quality. The check proves X11 clipboard behavior and the shared recovery controls; it does not establish native Wayland clipboard behavior. It never reads or replaces the user's clipboard.

For before/after cleanup quality, dictionary cases, audio scenarios, and optional LLM judging, use the [dictation eval workflow](../../evals/dictation/README.md). Keep quality verdicts separate from performance timings.

Use the same public audio corpus and order when comparing engines, backends, or code changes. Keep the first request separate from later requests. Do not call a preloaded first request a cold model load.

The current file benchmark measures a complete CLI/API response. It includes CLI startup, transport, file decoding, and transcription. It excludes dictation cleanup, live preview, and delivery. Record the model, active backend, host, commit, corpus, and local changes with each result.

The built CLI requires the .NET runtime location on this development host:

```sh
export PATH="/tmp/typewhisper-dotnet:$PATH"
export DOTNET_ROOT=/tmp/typewhisper-dotnet
uv run --no-project scripts/verification/benchmark_transcription.py \
  --manifest benchmarks/data/librispeech-50.jsonl \
  --engine needle-whistle --model whistle --expect-backend cpu \
  --cli src/TypeWhisper.Cli/bin/Release/net10.0/typewhisper-cli \
  --output artifacts/verification/current/whistle.json
```

The app must have its API enabled and the requested model loaded. The helper checks the actual reported backend and rejects a pending restart.

Compare two successful runs:

```sh
uv run --no-project scripts/verification/compare_benchmarks.py \
  artifacts/verification/before/benchmark.json \
  artifacts/verification/after/benchmark.json \
  --output artifacts/verification/comparison.json
```

The comparison rejects incomplete runs and mismatched corpus IDs, references, durations, or ordering. It reports first-request speed separately from warm median and nearest-rank p95. It reports word error rate separately from speed. A positive WER delta means more errors. A single run cannot establish a stable improvement; repeat the comparison under the same load and settings.

## Dictation stages

`GET /v1/dictation/transcription?sessionId=...` now includes `performance`. Ordinary dictation publishes its completed session once, after the delivery attempt. The existing text-ready event still precedes delivery. Legacy or non-dictation result paths can have null performance. When available, it contains:

- `transcription_milliseconds`, which measures the final provider call or streaming-result reuse. It excludes earlier capture teardown, streaming finalization, and model acquisition.
- `processing_milliseconds`, which contains each final pipeline step, including cleanup and custom prompt processing.
- `queue_wait_milliseconds` and `delivery_milliseconds`.
- `stop_to_delivery_milliseconds`, which includes capture teardown and the remaining work after an accepted stop. It is null for sessions without a captured stop timestamp.
- `delivery_outcome`, which distinguishes copying, pasting, typing, action handling, and failures.
- `live_cleanup_requests` and `live_cleanup_reused`.

These are server-side timings. A successful paste or type return does not prove that a target application displayed the text. A benchmark must check the destination separately. Do not report `state=ready` as proof of delivery. Failed and canceled sessions can lack performance data.

For a live benchmark, replay public audio through a dedicated virtual source. Compare final-only cleanup and live cleanup with the same audio, pause schedule, provider, model, and destination. Keep actual LLM quality tests separate from tests that use a simulated endpoint. The isolated fixture in the 14 October 2026 evidence directory demonstrates the request lifecycle and exact-input reuse.

Run helper checks with:

```sh
uv run --no-project python -m unittest discover -s tests/verification
```
