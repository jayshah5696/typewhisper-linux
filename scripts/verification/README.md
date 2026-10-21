# Performance verification

## Needle startup and transcription checks

Run `just upgrade` for a local version upgrade. It checks that dictation is idle,
publishes a matching app and plugin bundle, and validates every bundled plugin.
It upgrades a scratch copy of the installed program and checks that synthetic
settings, History, models, recordings, and plugin settings survive unchanged.
It then launches the executable named by the scratch desktop entry and verifies
Needle file transcription, virtual-microphone dictation, and private clipboard
read-back. The real installation is replaced only after those checks pass.
The command restarts the installed executable, checks its reported version,
assembly and plugin paths, and validates the deployed plugins again.

The audio check requires an existing Whistle download, `mutter`, Xwayland,
the project accessibility dependencies, and `xclip`. The public audio fixture
and its attribution are under `tests/fixtures/`. No corpus download is required.

```sh
just upgrade
# Individual checks against an already built or published payload:
just verify-plugins
just verify-needle
just verify-upgrade
```

`just verify-upgrade` expects the Release linux-x64 publish output. Prepare it
with `DOTNET=/path/to/dotnet bash scripts/install-linux-app.sh --publish-only`
when running that check separately. Evidence directories must be fresh.
`just restart` stops the session's idle app before rebuilding Debug, and deploys
development plugins to `artifacts/local-dev/Plugins`. It preserves the installed
plugin folder. `just doctor` reports the version and paths returned by the
running app's control socket. Older apps without these fields report unknown
runtime identity instead of inheriting the repository version.

After an upgrade or upstream merge, check the app payload that the desktop actually
launches against the deployed plugin directory. A fresh build passing its own tests
does not establish that an older installed app can load the same plugins.

```sh
export PATH="/tmp/typewhisper-dotnet:$PATH"
dotnet run --project scripts/verification/plugin_compatibility -- \
  "$HOME/.local/share/typewhisper-app" \
  "$HOME/.local/share/TypeWhisper/Plugins" \
  com.typewhisper.needle-whistle
```

The probe calls that payload's actual plugin loader without activation or user-data
writes. It exits 1 if the requested plugin does not load and reports the underlying
type-load exception. Use the installed app directory for the first argument. For a
development check, use `src/TypeWhisper.Linux/bin/Release/net10.0` and its `Plugins`
subdirectory. Run each host comparison in a separate process.

On 21 October 2026, the installed 0.17 app failed this check because the shared
Needle plugin required PluginSDK 0.18. The startup log said "Plugin class not found"
and dictation later said "Unknown plugin". The class existed, but its SDK dependency
could not load. The matching 0.18 Release app passed. The old installed bundle did
not contain Needle, so its startup preserved the newer DLL in the shared plugin
directory. Restarting the old payload alone does not repair that mismatch.

Build Release, prepare the public corpus, and check Needle audio separately:

```sh
just restore
just build-release
just verify-needle
```

This check uses the project hidden desktop dependencies plus `xclip`. It transcribes
the public file, plays it through an isolated virtual microphone, checks the actual
engine and CPU backend, and reads the final text from a private X11 clipboard. It
requires the expected speech in both transcripts. It never restarts the user's app.
Use a fresh output directory. The local model must already be downloaded.

Evidence from the investigation is under
`artifacts/verification/2026-10-21-needle-investigation/`. The detailed local report
is `docs/tasks/needle-upstream-regression/01-research-needle-load-failure.md`.

## Recovery verification

Run `just doctor`, then `just build-release` and `just verify-recovery <new-output-directory>` for dictation recovery changes. The recovery helper uses the hidden desktop dependencies plus `xclip` and system Python with GI and AT-SPI bindings. It defaults to the existing public clip under `/tmp/typewhisper-benchmark/`; run `just prepare-corpus` if that clip is missing.

The helper starts an isolated Release app and copies the existing Sherpa model without modifying the source files. It records only a virtual microphone. It verifies public audio through dictation and saved History, then expands the actual History entry and uses its inline recovery controls. Both copy actions must match text read back from the private X11 clipboard. A separate clipboard value detects unwanted copying during preview, dismissal, collapse, or timeout. The original history file must remain unchanged.

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
