# UI rendering and UX flows

## Sub-features

- Every Settings section renders inside the real main window with real ViewModels.
- Text fits its space in every UI language (English, German, Spanish, Russian, Japanese, Simplified Chinese) at the default and a compact window size.
- No raw localization key, and no text from another UI language, reaches the screen.
- Every interactive control has an accessible name, so screen readers and UI tests can find it. That includes the sidebar navigation.

## How to get to it (user POV)

Open TypeWhisper and click through the Settings navigation. Change Settings, then General, then Interface Language to switch the UI language. Shrink the window to about 960 by 640 to see the compact layout.

## Driving it with the headless UI tests

```bash
just build-release
just ui-check artifacts/verification/$(date +%F)-ui
```

`tests/TypeWhisper.UiTests` starts Avalonia headless with Skia rendering, builds the production service graph with the integration suite's headless boundaries, and renders the main window once per section, language, and size. It writes one PNG per variant, for example `Dictation-de-dark-compact.png`, and `findings-<language>.json`. It runs in `just ci` (as `test-ui`) and in the PR workflow, which uploads the PNGs. The test fails on:

- `raw-localization-key`: a known catalog key, or text shaped like one, on screen.
- `clipped-text`: an untrimmed `TextBlock` whose widest line is wider than its box. Wrapped text is checked too, because a single long word cannot break.
- `unnamed-control`: a button, toggle, combo box, text box, number box, or slider whose automation peer has no name. Template parts such as scrollbar buttons are skipped.
- `wrong-language-text`: text that only another UI language's catalog contains. This catches computed labels that were not refreshed after a language change.

Each failure lists the section, control, detail, nearby labels, and affected sizes. Open the matching PNG to see the problem. Proven on 2026-10-14: all four languages pass after fixing 59 unnamed controls, clipped labels in Dashboard, Dictation, Shortcuts, and Advanced, and stale engine and model labels after a language switch.

## Gotchas

- The PNG shows only the visible viewport; checks cover the whole section, including content scrolled out of view.
- Settings windows support System, Light, and Dark appearance. The tests render both Light and Dark. The recording overlay and floating live-text window stay Dark.
- Japanese and Chinese rendering requires a CJK font, such as Noto Sans CJK. The rendered tests fail when no matching font is available.
- The test graph supplies a local transcription engine for model-card coverage. It does not load account-backed plugins, so their settings pages still need separate live checks.
- Clipping checks include the root viewport and clipping ancestors, including scroll viewers that disable horizontal scrolling.
- Wrapped lines are measured without their trailing space, which is never drawn.
- Name a control with `AutomationProperties.Name="{loc:Str <label key>}"`, using the key of its visible row label, so the name follows the UI language. Items generated from a template, such as the sidebar buttons, use `AutomationProperties.Name="{Binding Label}"`; without a name, Avalonia does not expose them over AT-SPI at all.
- Prefer layouts that survive long translations: let labels wrap, give label columns a minimum width, cap control widths instead of fixing them, and put wide control groups under their title.

## Live flows in a hidden desktop

```bash
just ui-live artifacts/verification/$(date +%F)-hidden-desktop
just ui-shortcuts artifacts/verification/$(date +%F)-shortcuts
```

`scripts/verification/hidden_desktop.py` starts a private D-Bus session, the AT-SPI bus and registry on it, and `mutter --headless --wayland --virtual-monitor 1440x900`. An isolated app instance draws into that compositor's Xwayland display.

- `HiddenDesktop.accessibility(click=name)` reads the AT-SPI tree, with each node's role, name, screen extents, and text, and invokes the first action of the control with that name.
- `HiddenDesktop.input(steps)` clicks at screen coordinates and types key combinations through mutter's RemoteDesktop API.
- `HiddenDesktop.screenshot(path)` captures a real composited frame through mutter's ScreenCast API and GStreamer `pipewiresrc`.

Proven on 2026-10-14: Open Wizard opened the welcome wizard, the Shortcuts nav item opened its section, and the shortcut recorder saved `Ctrl+Shift+F8`, each with screenshots.

Proven on 2026-10-23 after integrating upstream `d458733e`: the full rendered suite passed 18 tests across four languages and both theme variants. The private desktop check changed Theme to Light and back to Dark through the controls, verified the saved values, selected Floating window, displayed and stopped its sample preview, and inspected local model cards and the fork's wait-limit and GPU choices. The shortcut recorder again saved `Ctrl+Shift+F8`. Repeatable checks and evidence are under `artifacts/verification/2026-10-23-upstream-sync/`.

Proven on 2026-10-24 after integrating upstream `d38b4f21`: all 21 rendered UI tests passed across six languages. The private live check switched to Japanese and Chinese during dictation, verified translated API status without stopping recording, and read copied text back from History in both languages. It also exercised a loopback alias provider through generation, review, and saving, and checked the displayed Parakeet Ultra wizard default. Repeat with `uv run --no-project artifacts/verification/2026-10-24-upstream-sync/check_new_upstream.py --out <fresh-directory>`. The helper explicitly dismisses dropdowns after AT-SPI selection, which otherwise leaves Avalonia popups open.

- The private bus cannot start the a11y bus or registry through systemd; the helper starts `at-spi-bus-launcher` and `at-spi2-registryd` itself.
- Settings sections nest deeply; the AT-SPI walk goes 40 levels deep.
- Never use `ydotool` or other uinput tools here; they type into the user's real desktop. RemoteDesktop input reaches only the hidden compositor.
- Delivery inside the hidden desktop goes to that session's clipboard, never the user's.
