# Upstream 0.19 integration verification

Merged all 24 upstream commits through `d9fbf9f9` into
`t3/check-upstream-and-local-status-3`. No upstream commits remain absent.
Merge commit `f7418cc0` preserves the fork changes. Commit `63fb05bf` removes
the redundant History accessible-name binding and updates the recovery verifier
for upstream's time-plus-preview row names.

## Resolutions and review

- Kept the fork GPU documentation in the README conflict.
- Accepted removal of the online registry publishing workflow with its deleted publisher.
- Resolved the test lock file with the new 0.19 SDK and Tmds 0.94.1 dependencies.
- Removed duplicate accessibility attributes from the automatically merged plugin controls.
- Retained upstream's time-plus-preview History name and updated the verifier to select it.

Pi reviewed the integration with `openai/gpt-6.1-sol`, thinking `xhigh`.
The reviewer found no remaining actionable integration findings. It also inspected
the History and verifier corrections before completion. Those corrections are
committed in `63fb05bf`. See [review.md](review.md).

## Results

| Check | Result | Evidence |
|---|---|---|
| `just ci` | 9,339 passed, 14 skipped, no failures | `ci.log`, `../../test-results/` |
| `just eval-tests` | 74 passed | `eval-tests-final.log` |
| Final `just build-release` | Passed, no build warnings or errors | `final-build.log` |
| Final `just ui-check` | All 43 tests passed across six languages | `final-ui.log`, `final-ui/` |
| `just verify-plugins` | All 39 bundled plugins loaded against app and SDK 0.19 | `plugins.log` |
| Installed 0.18 host and its original bundle | Compatible before the scratch upgrade | `installed-before-plugins.log` |
| Needle file and virtual microphone transcription | All 9 checks passed, CPU execution and private clipboard read-back | `needle-rerun/result.json` |
| History recovery from an actual dictation | All 20 checks passed | `recovery-final/recovery-smoke.json` |
| Native overlay input regions | Empty while idle, restored during recording, empty after cancel | `overlay/result.json` |
| Live Settings and welcome wizard | Open Wizard control opened the real wizard | `ui-live/hidden-desktop.json` |
| Scratch 0.18 to 0.19 upgrade | All 7 data-preservation and launcher checks passed | `upgrade/preservation.json` |
| Needle through upgraded executable from desktop entry | All 9 checks passed | `upgrade/needle/result.json` |
| Live cleanup with a local echo provider | All 7 checks passed, including a request before stop and exact clipboard read-back | `live-cleanup-x11/result.json` |
| Isolated run cleanup | No temporary runtime directories or fixture sinks remain | `cleanup.json` |

The 14 skipped CI cases require opt-in model downloads, native runtime checks, or
access to the real data folder. The live checks used existing local models copied
into isolated instances. They captured only the dedicated virtual microphone.

Recovery checks copied both the original text and the new cleanup result and read
them back from the private X11 clipboard. Preview, dismissal, collapse, and timeout
kept the clipboard unchanged. The saved History file also remained unchanged.
The rendered History recovery and compact profile editor screenshots were inspected.

## Observed stage timings

These are individual verification runs, not a performance comparison. All ran on CPU.

| Run | Final transcription | Final cleanup step | Delivery |
|---|---:|---:|---:|
| History recovery setup dictation | 306.2 ms | 138.6 ms | 273.2 ms |
| Live cleanup dictation | 427.3 ms | 2.3 ms, reused a draft | 279.4 ms |
| Upgraded Needle dictation | 417.8 ms | Not requested | 276.4 ms |

The live cleanup provider was a loopback echo fixture with a simulated 250 ms delay.
These checks establish routing and reuse behavior, not LLM output quality.

## Environment and repeat commands

The SDK is .NET 10.0.401 at
`/home/jshah/Documents/Codex/2026-10-11/typewhisper-linux-cachyos/work/dotnet-sdk`.
Set `DOTNET` to its `dotnet` executable and `DOTNET_ROOT` to that directory.
The host lacked `xclip` and `xdotool`. Their distribution packages and dependencies
were extracted into `/tmp/typewhisper-verify-tools` without a system installation.
The checks used its `usr/bin` on `PATH` and `usr/lib` on `LD_LIBRARY_PATH`.

Use fresh evidence directories for reruns. The audio fixture is
`tests/fixtures/librispeech-1462-170145-0012.wav`.

```sh
just ci
just eval-tests
just verify-plugins
just verify-needle <fresh-needle-directory>
uv run --no-project scripts/verification/recovery_smoke.py --out <fresh-recovery-directory> --audio tests/fixtures/librispeech-1462-170145-0012.wav
uv run --no-project artifacts/verification/2026-10-28-upstream-sync/check_overlay.py --out <fresh-overlay-directory>
just ui-live <fresh-ui-directory>
bash scripts/install-linux-app.sh --publish-only
just verify-upgrade <fresh-upgrade-directory>
uv run --no-project artifacts/verification/2026-10-28-upstream-sync/check_live_cleanup.py --out <fresh-live-cleanup-directory>
```

## Limits and installation state

The initial Needle attempt failed because `xclip` was absent. Two recovery attempts
exposed verifier drift from upstream's changed accessible names. The successful
reruns above retain their earlier failed evidence alongside them.

`just live-smoke --desktop hidden` generated text and reused a live draft, but
clipboard delivery failed in the headless Wayland session. The equivalent flow
passed with the private X11 clipboard through `check_live_cleanup.py`. Native
Wayland delivery remains unverified by this run. No cloud account calls or new
GPU execution checks were performed.

The real desktop process stayed at version 0.18, PID 3201, with its original SDK
and plugin directory. Only the scratch copy was upgraded. No user settings,
credentials, history, recordings, or models were replaced. See `doctor-after.json`.
