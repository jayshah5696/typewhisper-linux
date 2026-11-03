# Desktop Linux Integration

The desktop shell integrates Avalonia UI with Linux windowing systems, display protocols, global input monitors, and audio daemons.

The presentation and system interaction logic resides primarily in [[src/TypeWhisper.Linux]].

## Display Server Compatibility

TypeWhisper supports both traditional X11 sessions and modern Wayland compositors through adaptive input injection and windowing backends.

Differences across display environments:
- **X11**: Full support for global shortcut grabbing via X11 hotkey hooks, window focus queries, and clipboard synchronization with `xclip` or native X11 atoms.
- **Wayland**: Global shortcuts use available desktop integration or the Linux evdev backend. Input delivery depends on compositor support and installed tools. Mutter RemoteDesktop input belongs to the isolated verification harness, not the production input backend.

## Global Hotkeys and Short Cuts

System-wide push-to-talk and toggle shortcuts operate independently of active application window focus.

Hotkeys are registered at application launch. Key requirements include:
- Preserving custom user key combinations across application restarts.
- Resolving shortcut collisions gracefully with clear warning messages.
- Supporting push-to-talk (hold to record, release to transcribe) and toggle modes.

## Overlay and Floating Controls

A lightweight, non-stealing overlay window provides immediate visual feedback on dictation state, audio levels, and live transcription drafts.

The floating widget maintains the following behavioral guarantees:
- The control is designed to preserve the target application's focus.
- The quiet control snaps to a display edge; the recording overlay has its own placement settings.
- Mouse clicks beside the collapsed control or through transparent margins pass directly to underlying windows.

[[src/TypeWhisper.Linux/Services/Hotkey/DeSetup/DesktopDetector.cs|Desktop detection]] selects notification-only mode on known tiling desktops or when `TYPEWHISPER_DISABLE_OVERLAYS=1`. This startup override suppresses overlays even when the saved floating-control setting is enabled. [[src/TypeWhisper.Linux/ViewModels/Sections/AppearanceSectionViewModel.cs|Appearance settings]] also hides overlay controls in that mode. Removing the override requires a restart.

[[src/TypeWhisper.Linux/Services/RecordingNotificationService.cs|Recording notifications]] report recording, processing, and terminal feedback only in notification mode. Overlay mode uses the floating controls instead; it does not also send recording notifications.

## Native Dependencies and Packaging

The Linux application packages required platform runtimes and desktop utilities for standard distributions.

Scripts in `scripts/` provide installation, packaging, and upgrade orchestration:
- `scripts/install-linux-app.sh` installs binaries to user or system prefixes.
- `scripts/upgrade-local-app.sh` builds and verifies the payload, then installs and restarts the app while preserving user profiles, models, and history.
- Packaging bundles shared dependencies, desktop launcher files, and icon assets.
