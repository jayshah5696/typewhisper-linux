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

## App verification and user data

Use the [project verification skill](.agents/skills/verify-typewhisper-linux/SKILL.md) for app, plugin, model, audio, and acceleration changes. Check app state before stopping a process. Do not restart an existing instance merely to verify a change; a user-requested restart is authorized.

Before handing off a user-facing change, run the skill's relevant build, tests, and real app checks end to end. Exercise the changed controls and verify the final result at its destination. For dictation changes, use public audio through an isolated virtual microphone. For recovery changes, start with an actual dictation, open the recovery UI, and read copied text back from the clipboard. Compilation, a successful API response, a rendered screenshot, or a successful copy return alone does not complete verification.

If a verification environment blocks a check, first try a safe isolated alternative that preserves user data. Keep the PR in draft and report the exact remaining check only when those alternatives cannot complete it. Save a repeatable verification command and evidence with the change. Update the PR's verification results after fixes and reruns.

Preserve models, history, recordings, credentials, and provider settings. A cleanup request does not authorize deleting user data. Do not record a physical microphone without an approved test phrase. Use public audio and a dedicated virtual source for scripted checks.

Save evidence under `artifacts/verification/`. Report transcription, cleanup, and delivery timings separately. Confirm the active execution backend. A generated transcript or a successful delivery return does not prove that a target application displayed the text.
