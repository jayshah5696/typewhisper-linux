# Desktop Linux Integration

The desktop shell integrates Avalonia UI with Linux windowing systems, display protocols, global input monitors, and audio daemons.

The presentation and system interaction logic resides primarily in [[src/TypeWhisper.Linux]].

## Display Server Compatibility

TypeWhisper supports both traditional X11 sessions and modern Wayland compositors through adaptive input injection and windowing backends.

Differences across display environments:
- **X11**: Full support for global shortcut grabbing via X11 hotkey hooks, window focus queries, and clipboard synchronization with `xclip` or native X11 atoms.
- **Wayland**: Integrates via desktop portal protocols (e.g. `org.freedesktop.portal.Desktop`), Mutter remote desktop streams, or compositor-specific shortcut extensions.

## Global Hotkeys and Short Cuts

System-wide push-to-talk and toggle shortcuts operate independently of active application window focus.

Hotkeys are registered at application launch. Key requirements include:
- Preserving custom user key combinations across application restarts.
- Resolving shortcut collisions gracefully with clear warning messages.
- Supporting push-to-talk (hold to record, release to transcribe) and toggle modes.

## Overlay and Floating Controls

A lightweight, non-stealing overlay window provides immediate visual feedback on dictation state, audio levels, and live transcription drafts.

The floating widget maintains the following behavioral guarantees:
- Window focus is never stolen from the active application being typed into.
- The control can dock to any display edge or float freely according to user preference.
- Mouse clicks beside the collapsed control or through transparent margins pass directly to underlying windows.

## Native Dependencies and Packaging

The Linux application packages required platform runtimes and desktop utilities for standard distributions.

Scripts in `scripts/` provide installation, packaging, and upgrade orchestration:
- `scripts/install-linux-app.sh` installs binaries to user or system prefixes.
- `scripts/upgrade-local-app.sh` handles zero-downtime upgrades while preserving user profiles, models, and history.
- Packaging bundles shared dependencies, desktop launcher files, and icon assets.
