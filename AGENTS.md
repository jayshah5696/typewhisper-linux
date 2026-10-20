# Project instructions

TypeWhisper Linux is a .NET 10 and Avalonia desktop app. Use the existing `justfile` as the source of build, test, restart, and benchmark commands.

## Pull requests

- Never open a pull request unless explicitly asked.
- When asked, PRs belong to our fork (`origin`, `linux` branch), never `upstream`.

## Commands

- `just build` builds the local Debug app and CLI and bundles the Linux plugins.
- `just restart` builds and replaces the running local app. Use it when the user requests a restart or asks to try the updated build.
- `just build-release` builds the Release app and CLI. Run `just restore` first for a fresh checkout.
- `just doctor` checks the running app and local environment without printing credentials.
- `just verify-recovery` verifies public audio through dictation, History, the recovery UI, and clipboard read-back in a private X11 desktop. Build Release first. See `scripts/verification/README.md` for prerequisites.
- `just test-linux`, `just test-core`, `just test-plugins`, `just test-cli`, and `just test-integration` run the corresponding Release test projects. Build Release before these commands.
- `just ci` restores, builds Release, and runs the full test set.
- `just benchmark` runs the public audio benchmark. Read `scripts/verification/README.md` for measurement boundaries and comparisons.
- `just eval-tests` checks the quality-eval tools without model calls. Use `just eval-template`, `just eval-review`, and `just eval-score` for manual before/after review. Read `evals/dictation/README.md` before optional `just eval-judge` calls.

Run `just --list` when a command is unclear. The recipes locate the local SDK automatically. Use `DOTNET` to select another SDK. Locally built CLI executables can also require `DOTNET_ROOT=/tmp/typewhisper-dotnet`.

## Engineering

Prefer simple systems and complete user-visible behavior. Fix root causes and avoid unnecessary abstractions. Inspect current code and all callers before changing a contract.

Write unit tests before production code. Keep tests focused on failure cases and real behavior. Do not remove failing tests or replace errors with dummy success results. Run the build and relevant tests before reporting completion. Read complete failure logs and distinguish existing failures from new ones.

Use `rg` for searches and `apply_patch` for source edits. Use `uv` for Python tools. Do not create phased plans unless requested.

## Maintainer review conventions

- Keep each PR limited to its stated change. Put shared fixes in the PR that owns them. Rebase dependent PRs onto the intended parent, then onto `upstream/linux` after that parent merges. Preserve newer maintainer commits and avoid stale copies of dependency changes.
- For GPU backends, check the loader, required bundled libraries, and compatible hardware before initializing a native loader that cannot retry in the same process. Report CPU fallback and its reason. Check the distributed plugin ZIP and package dependency closure, not only the build output.
- Keep requested acceleration, preparing or restart-required state, and the active backend distinct. A plugin capability or installed driver does not prove GPU execution.
- Make automatic provider labels match actual routing. Keep global defaults separate from per-action overrides. Scope unavailable placeholder choices to the setting or editor that owns them. Refresh option lists only when relevant state changes, and defer collection changes that would run inside a selector's selection transaction.
- On interface language changes, refresh computed labels, filter options, and current status text without replacing an in-progress operation's state with an old result. Group new keys beside related entries in all four locale files.
- Opening plugin settings must not refresh catalogs or save settings. Explicit refresh must preserve edits, save only dirty settings if saving is needed, and notify only when normalized catalogs change. Give provider refreshes a deadline shorter than the host boundary and reject results superseded by a newer refresh or credential change.
- Preview and evaluation processing must not record dictionary or snippet usage, update learning data, or persist incidental statistics. Discarded live drafts must not produce final-cleanup failure logs or suppress later final-cleanup errors.
- Disabled live cleanup must return before profile, action, or language resolution and make no draft requests. Bound speculative draft work and cancellation without delaying final cleanup. Do not impose a draft deadline or shared serialization gate on ordinary cleanup. Feed reusable drafts through the same stages that precede final cleanup.

## App verification and user data

Use the [project verification skill](.agents/skills/verify-typewhisper-linux/SKILL.md) for app, plugin, model, audio, and acceleration changes. Check app state before stopping a process. Do not restart an existing instance merely to verify a change; a user-requested restart is authorized.

Before handing off a user-facing change, run the skill's relevant build, tests, and real app checks end to end. Exercise the changed controls and verify the final result at its destination. For dictation changes, use public audio through an isolated virtual microphone. For recovery changes, start with an actual dictation, expand its History entry, and read copied text back from the clipboard. Compilation, a successful API response, a rendered screenshot, or a successful copy return alone does not complete verification.

If a verification environment blocks a check, first try a safe isolated alternative that preserves user data. Keep the PR in draft and report the exact remaining check only when those alternatives cannot complete it. Save a repeatable verification command and evidence with the change. Update the PR's verification results after fixes and reruns.

Preserve models, history, recordings, credentials, and provider settings. A cleanup request does not authorize deleting user data. Do not record a physical microphone without an approved test phrase. Use public audio and a dedicated virtual source for scripted checks.

Save evidence under `artifacts/verification/`. Report transcription, cleanup, and delivery timings separately. Confirm the active execution backend. A generated transcript or a successful delivery return does not prove that a target application displayed the text.
