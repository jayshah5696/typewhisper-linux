# Project instructions

TypeWhisper Linux is a .NET 10 and Avalonia desktop app. Use the existing `justfile` as the source of build, upgrade, test, restart, and benchmark commands.

## Pull requests

- Never open a pull request unless explicitly asked.
- When asked, PRs belong to our fork (`origin`, `linux` branch), never `upstream`.

## Commands

- `just build` builds the local Debug app and CLI and bundles the Linux plugins.
- `just upgrade` publishes matching app and plugin assemblies, verifies an upgrade in isolation, then installs and restarts the installed app. Use it after a version upgrade or upstream merge when the user asks to update their installation. It requires the downloaded Whistle model and the hidden desktop dependencies, including `xclip`. Missing tools stop the command before installation.
- `just restart` stops the session's idle app before building and starting Debug. Use it when the user requests a development restart or asks to try the updated build. Development plugins go to `artifacts/local-dev/Plugins`; models and provider settings stay in their existing locations.
- `just build-release` builds the Release app and CLI. Run `just restore` first for a fresh checkout.
- `just doctor` checks the running app and local environment without printing credentials. Its control status reports the responding process's version, SDK, assembly path, and plugin directory. The repository version does not prove which app is running.
- `just verify-plugins` checks every plugin in the selected bundle with that app's actual loader. Pass the host and plugin directories as positional arguments to check an installed payload.
- `just verify-needle` checks Needle file transcription, virtual-microphone dictation, CPU execution, and private X11 clipboard read-back with the public fixture in `tests/fixtures/`.
- `just verify-upgrade` upgrades a scratch copy of the installed program, checks user-data preservation and launcher paths, and runs the Needle check. It expects the Release linux-x64 publish output. See `scripts/verification/README.md` for preparation and prerequisites.
- `just verify-recovery` verifies public audio through dictation, History, the recovery UI, and clipboard read-back in a private X11 desktop. Build Release first. See `scripts/verification/README.md` for prerequisites.
- `just test-linux`, `just test-core`, `just test-plugins`, `just test-cli`, and `just test-integration` run the corresponding Release test projects. Build Release before these commands.
- `just lint` runs the static code guideline and compiler analyzer configuration check in `scripts/verification/code_guidelines_lint.py`.
- `just pre-push` runs `just lint`, `just eval-tests`, `just build-release`, `just test-core`, `just test-plugins`, and `just test-cli`. Enable the tracked hook with `git config core.hooksPath .githooks` or run `just pre-push` before every push.
- `just ci` runs `just lint`, restores, builds Release, and runs the full test set.
- `just benchmark` runs the public audio benchmark. Read `scripts/verification/README.md` for measurement boundaries and comparisons.
- `just eval-tests` checks the quality-eval tools without model calls. Use `just eval-template`, `just eval-review`, and `just eval-score` for manual before/after review. Read `evals/dictation/README.md` before optional `just eval-judge` calls.

Run `just --list` when a command is unclear. The recipes locate the local SDK automatically. Use `DOTNET` to select another SDK. Locally built CLI executables can also require `DOTNET_ROOT=/tmp/typewhisper-dotnet`.

## Engineering

Prefer simple systems and complete user-visible behavior. Fix root causes and avoid unnecessary abstractions. Inspect current code and all callers before changing a contract.

The build in `Directory.Build.props` and `.editorconfig` and `just lint` in `scripts/verification/code_guidelines_lint.py` enforce these code rules as errors:
- Name every repository `enum` variant explicitly in `switch` expressions and avoid `if` and `else if` chains over enum values, enforced by `CS8509`, `IDE0066`, and `BAML001`.
- Pass explicit `TimeSpan` values such as `TimeSpan.FromMilliseconds` or `TimeSpan.FromSeconds` to `Task.Delay`, `Thread.Sleep`, and `CancelAfter` instead of raw integer literals, enforced by `BAML002`.
- Throw specific exception types such as `InvalidOperationException` or `ArgumentException` and use `ArgumentNullException.ThrowIfNull` instead of generic `Exception`, `ApplicationException`, or `SystemException`, enforced by `CA1510` to `CA1513`, `CA2201`, and `BAML003`.
- Use `[GeneratedRegex]` methods with descriptive names showing what the pattern matches instead of inline constant regex strings, enforced by `SYSLIB1045` and `BAML004`.
- Prefer C# standard library methods and the `..` range operator over manual indexing or `Substring` and `Slice` arithmetic, enforced by `CA1826`, `CA1859`, `CA1860`, `CA1865` to `CA1867`, `IDE0057`, and `BAML005`.

Write unit tests before production code. Keep tests focused on failure cases and real behavior. Do not remove failing tests or replace errors with dummy success results. Run `just pre-push` before pushing or reporting completion. Read complete failure logs and distinguish existing failures from new ones.

Use `rg` for searches and `apply_patch` for source edits. Use `uv` for Python tools. Do not create phased plans unless requested.

## Version upgrades and development

Keep the installed app and its bundled plugins compatible with the same PluginSDK. A successful source build does not upgrade the desktop launcher. Verify the running process after installation, and preserve the underlying SDK or type-load error when a plugin cannot load.

Use `just restart` for development so a development launch does not replace installed plugin assemblies. `TYPEWHISPER_PLUGINS_PATH` overrides only the deployed plugin directory and must be absolute. Isolated verification must clear an inherited override before setting up its own directories.

Do not build over assemblies used by a running development instance. The restart command stops the app serving the current session's control socket before rebuilding. Do not replace this with a command that stops every TypeWhisper process; other instances can belong to isolated verification sessions.

After changing upgrade, deployment, or plugin loading behavior, check an older installation followed by the new payload. Verify that user data survives and that Needle transcribes public audio through the installed executable. Save evidence under `artifacts/verification/`.

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

%% lat:begin %%
# Before starting work

Use Lat's knowledge graph to ground each task before changing code.

- Run `lat search` to find sections relevant to your task. Read them to understand the design intent before writing code.

# Post-task checklist (REQUIRED — do not skip)

After EVERY task, before responding to the user:

- [ ] Update `lat.md/` if you added or changed meaningful implemented functionality, architecture, tests, or behavior. Keep it a focused snapshot of the current implemented state, not a journal/changelog. Plans may be drafted in `lat.md/` alongside implementation, with the intent that by commit time they describe what was implemented. Otherwise, keep proposals, hypothetical designs, and future work outside `lat.md/` unless the user explicitly requests them there.
- [ ] Run `lat check` — all validations must pass
- [ ] Do not skip these steps. Do not consider your task done until both are complete.

---

# What is lat.md?

Lat.md is the project's structured knowledge graph for architecture, design decisions, behavior, and tests.

The graph lives in `lat.md/` as cross-linked Markdown describing **what** the project does and **why**. Use it to ground work in actual architecture, and keep it a focused snapshot of the current implemented state rather than a journal or changelog.

Project-specific lat documentation belongs in `lat.md/`. Do not modify this generated instruction file or a generated `lat-md` `SKILL.md` to record project guidance: both are owned by lat tooling and may be replaced by a later `lat init`.

# Commands

Use these commands to navigate and validate the project knowledge graph.

```bash
lat locate "Section Name"      # find a section by name (exact, fuzzy)
lat refs "file#Section"        # find what references a section
lat search "natural language"  # semantic search across all sections
lat external show <handle>      # inspect a pinned external source before cloning it
lat check                      # run full graph and documentation validation
```

Run `lat --help` when in doubt about available commands or options.

If `lat search` fails because no API key is configured, explain to the user that semantic search requires a key provided via `LAT_LLM_KEY` (direct value), `LAT_LLM_KEY_FILE` (path to key file), or `LAT_LLM_KEY_HELPER` (command that prints the key). Supported key prefixes: `sk-...` (OpenAI) or `vck_...` (Vercel). If the user doesn't want to set it up, use `lat locate` for direct lookups instead.

# Syntax primer

Lat uses stable section ids, wiki links, source links, and code references to connect documentation with implementation.

- **Section ids**: `lat.md/path/to/file#Heading#SubHeading` — full form uses project-root-relative path (e.g. `lat.md/tests/search#RAG Replay Tests`). Short form uses bare file name when unique (e.g. `search#RAG Replay Tests`, `cli#search#Indexing`).
- **Wiki links**: `[[target]]` or `[[target|alias]]` — cross-references between sections. Can also reference repository paths or source code: `[[schema.sql]]`, `[[src/components]]`, `[[src/foo.ts#myFunction]]`.
- **Link specificity**: When documenting implemented behavior, prefer the narrowest relevant source-symbol link over a whole-file link. Use whole-file or directory links when discussing the module or resource as a whole, or when no supported symbol represents the target. If prose names a symbol, link that symbol directly.
- **Repository path links**: Wiki links without a `#` fragment may target any existing file or directory inside the project. Unsupported formats validate but cannot be opened by Lat; fragments require a `lat.md/` section or supported source file.
- **Source code links**: Wiki links in `lat.md/` files can reference functions, classes, constants, and methods in supported source files. Use the full path: `[[src/config.ts#getConfigDir]]`, `[[src/server.ts#App#listen]]` (class method), `[[lib/utils.py#parse_args]]`, `[[src/lib.rs#Greeter#greet]]` (Rust impl method), `[[src/app.go#Greeter#Greet]]` (Go method), `[[src/app.h#Greeter]]` (C struct). When prose names an implementation symbol or a behavior governed by one, link the symbol instead of using a bare code span or copying its literal value. Prefer `[[src/config.ts#DEFAULT_TIMEOUT]]` (or an aliased form) over a bare identifier or copied value. `lat check` validates these exist.
- **Code refs**: `// @lat: [[section-id]]` (JS/TS/Rust/Go/C/PHP) or `# @lat: [[section-id]]` (Python/PHP) — ties source code to concepts

# Diagrams

Default to prose. Reserve Mermaid diagrams for concepts involving more than seven interacting subjects (such as components, states, or stages) with complex sequencing, branching, or dependencies that prose alone makes hard to follow.

The subject count alone does not justify a diagram; omit diagrams for simple sequences or relationships that a short paragraph or list explains clearly. A diagram should give users and later agents an overview of the interactions.

Keep each diagram focused on one process, use readable labels, and explain the important decisions in surrounding prose with precise source-symbol links. Prefer a small overview or several focused diagrams over one dense diagram. Use fenced `mermaid` blocks and run `lat check` to validate syntax and readability.

# Test specs

Key tests can be described as sections in `lat.md/` files (e.g. `tests.md`). Add frontmatter to require that every leaf section is referenced by a `// @lat:` or `# @lat:` comment in test code:

```markdown
---
lat:
  require-code-mention: true
---
# Tests

Authentication and authorization test specifications.

## User login

Verify credential validation and error handling for the login endpoint.

### Rejects expired tokens
Tokens past their expiry timestamp are rejected with 401, even if otherwise valid.

### Handles missing password
Login request without a password field returns 400 with a descriptive error.
```

Every section MUST have a description — at least one sentence explaining what the test verifies and why. Empty sections with just a heading are not acceptable. (This is a specific case of the general leading paragraph rule below.)

Each test in code should reference its spec with exactly one comment placed next to the relevant test — not at the top of the file:

```python
# @lat: [[tests#User login#Rejects expired tokens]]
def test_rejects_expired_tokens():
    ...

# @lat: [[tests#User login#Handles missing password]]
def test_handles_missing_password():
    ...
```

Do not duplicate refs. One `@lat:` comment per spec section, placed at the test that covers it. `lat check` will flag any spec section not covered by a code reference, and any code reference pointing to a nonexistent section.

# Section structure

Concise opening paragraphs give every section a stable overview for validation and discovery.

Every section in `lat.md/` **must** have a leading paragraph — at least one sentence immediately after the heading, before any child headings or other block content. The first paragraph must be ≤250 characters (excluding `[[wiki link]]` content). This paragraph serves as the section's overview and is used in search results, command output, and RAG context — keeping it concise guarantees the section's essence is always captured.

```markdown
# Good Section

Brief overview of what this section documents and why it matters.

More detail can go in subsequent paragraphs, code blocks, or lists.

## Child heading

Details about this child topic.
```

```markdown
# Bad Section

## Child heading

Details about this child topic.
```

The second example is invalid because `Bad Section` has no leading paragraph. `lat check` validates this rule and reports errors for missing or overly long leading paragraphs.
%% lat:end %%
