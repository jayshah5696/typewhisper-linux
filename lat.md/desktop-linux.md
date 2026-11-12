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

[[src/TypeWhisper.Linux/Services/Hotkey/DeSetup/DictationShortcutSpecFactory.cs|Shortcut validation]] rejects Ctrl+Alt+F1 through F12 because Linux reserves those chords for virtual-terminal switching. Dynamic prompt and profile bindings use the same restriction in [[src/TypeWhisper.Linux/Services/HotkeyService.cs|HotkeyService]].

## Overlay and Floating Controls

A lightweight, non-stealing overlay window provides immediate visual feedback on dictation state, audio levels, and live transcription drafts.

The floating widget in [[src/TypeWhisper.Linux/Views/DictationOverlayWindow.axaml.cs|DictationOverlayWindow]] maintains the following behavioral guarantees:
- The control is designed to preserve the target application's focus.
- The quiet control snaps to a display edge via [[src/TypeWhisper.Linux/Services/OverlayDocking.cs|OverlayDocking]]; when floating control docking is disabled, [[src/TypeWhisper.Linux/Views/DictationOverlayWindow.axaml.cs|DictationOverlayWindow]] tracks window-manager moves and persists settled custom coordinates.
- Mouse clicks beside the collapsed control or through transparent margins pass directly to underlying windows via [[src/TypeWhisper.Linux/Services/X11InputPassthrough.cs]].

[[src/TypeWhisper.Linux/Services/Hotkey/DeSetup/DesktopDetector.cs|Desktop detection]] selects notification-only mode on known tiling desktops or when `TYPEWHISPER_DISABLE_OVERLAYS=1`. This startup override suppresses overlays even when the saved floating-control setting is enabled. [[src/TypeWhisper.Linux/ViewModels/Sections/AppearanceSectionViewModel.cs|Appearance settings]] also hides overlay controls in that mode. Removing the override requires a restart.

[[src/TypeWhisper.Linux/Services/RecordingNotificationService.cs|Recording notifications]] report recording, processing, and terminal feedback only in notification mode. Overlay mode uses the floating controls instead; it does not also send recording notifications.

## Audio device changes

The microphone preview follows changes to the system default input without restarting the app or interrupting a recording.

[[src/TypeWhisper.Linux/Services/AudioRecordingService.cs|AudioRecordingService]] releases a preview stream before refreshing PortAudio's device table and then reopens it on the current default. Changes during recording wait until capture ends. [[src/TypeWhisper.Linux/Services/AudioPlaybackService.cs|AudioPlaybackService]] holds its native audio reference only while a stream exists, so idle playback does not prevent device discovery.

[[src/TypeWhisper.Linux/Services/DefaultDeviceWatcher.cs|Default-device monitoring]] excludes changes caused by opening the app's own preview stream, preventing an Automatic microphone preview from repeatedly reopening itself.

## Interface language changes

Changing the interface language refreshes displayed labels and status messages while preserving current operations and unsaved plugin settings.

[[src/TypeWhisper.Core/Services/DefaultItemNames.cs|Default item names]] retain a stable identity so translated built-in profile and prompt names can follow the interface language without replacing user names. [[src/TypeWhisper.Linux/ViewModels/Sections/AboutSectionViewModel.cs|About status messages]] retain their localization keys and arguments for redraws. [[tests/TypeWhisper.UiTests/PluginDraftLocalizationTests.cs|Plugin draft tests]] verify that language changes preserve edited settings and their saved baseline.

[[src/TypeWhisper.Linux/ViewModels/Sections/ProfilesSectionViewModel.cs|The profile editor]] preserves drafts while built-in names change language and clears selection when the selected profile no longer exists. [[tests/TypeWhisper.UiTests/ProfilesSmallWindowTests.cs|Profile layout tests]] exercise setup controls across all six languages after shared profile changes.

## Native Dependencies and Packaging

The Linux application packages required platform runtimes and desktop utilities for standard distributions.

Scripts in `scripts/` provide installation, packaging, and upgrade orchestration:
- `scripts/install-linux-app.sh` installs binaries to user or system prefixes.
- `scripts/upgrade-local-app.sh` builds and verifies the payload, then installs and restarts the app while preserving user profiles, models, and history.
- Packaging bundles shared dependencies, desktop launcher files, and icon assets.
