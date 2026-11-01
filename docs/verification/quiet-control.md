# Quiet control verification

The optional floating control uses a static quiet bar when idle. It expands on
hover, snaps to any edge center after a drag, and remembers the selected display.
Recording and finishing have compact, separate controls. Copy-only delivery,
cleanup fallback, action results, and failures retain explicit feedback.

## Checks completed

The Release solution and CLI build without warnings or errors. The full
`just ci` run passed 9,371 tests and skipped 14 opt-in tests. All 49 rendered UI
tests passed across the six interface languages. All 74 verification-helper tests
passed, and the app's actual loader accepted all 39 bundled plugins.

Private GNOME/Mutter and Xvfb/Openbox runs verified:

- Four real pointer drags snap to the correct edge center and persist the display.
- Restarting the private app without rewriting settings retains its dock position.
- Dropping the bar never starts a recording.
- The microphone closes before finishing, which remains cancellable.
- Cancellation during a delayed cleanup request does not insert text.
- Stale controls cannot stop a newer recording.
- A genuine clipboard-tool failure exposes Copy text. After restoring the tool,
  copying produces the exact current transcript without pasting into the editor.
- All 36 neighboring clicks reach the underlying GTK target. This includes the
  positions previously blocked by tooltips and the former expanded/disabled area.

The normal dictation control flow passed 17 checks on each desktop. X11 proved
automatic paste into the original field. GNOME proved the copy/manual-paste
fallback. Existing History recovery passed 20 checks, including actual public
audio, expanded History controls, and clipboard read-back.

## Edge layout correction

All four dock positions are rendered in every language. Left and right hover,
recording, and finishing controls stack vertically; top and bottom controls remain
horizontal. The controls use content-sized stacks rather than reserved columns.
Stop and Cancel share a six-DIP gap, and the Cancel cross is centered vector art.

The correction passed all 49 UI tests, 3,753 Linux tests with two opt-in skips,
and 74 helper tests. Both private desktop suites passed 14 live checks, including
real hover/recording alignment at each edge, and all 36 neighboring-click probes.

## Repeat

Build Release first. See `scripts/verification/README.md` for SDK, desktop,
clipboard, accessibility, and model prerequisites. Output directories must be new.

```sh
just ci
just eval-tests
just verify-plugins
just verify-flow-control <output-directory> mutter
just verify-quiet-dock <output-directory> mutter
just verify-overlay-clicks <output-directory> mutter
# Repeat the live recipes with x11 as the second argument.
uv run --no-project scripts/verification/recovery_smoke.py \
  --audio tests/fixtures/librispeech-1462-170145-0012.wav --out <output-directory>
```

These checks use public audio, a dedicated virtual microphone, private displays,
and isolated user data. They do not send physical input or enable the user's API.
The private system bus has no logind service, so verification does not depend on
the physical screen lock. Production lock handling remains unchanged.

## Boundaries

Native Wayland, KDE-specific behavior, physical ydotool delivery, fullscreen
stacking, and mixed-monitor live execution were not newly verified. Pure code
tests cover negative monitor origins, display removal, scaled placement, and
position persistence. Native drag checks used one virtual display. Existing
tiling-desktop notification fallback remains enabled. Meeting audio capture is
not part of this change.

Local logs, result files, and screenshots are under
`artifacts/verification/quiet-dock/`. They are not included in the repository.
