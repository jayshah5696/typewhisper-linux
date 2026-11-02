# Architecture

TypeWhisper Linux is structured into distinct .NET 10 assemblies and standalone plugins, separating domain abstractions, desktop presentation, plugin contracts, and automation.

The solution codebase is rooted in `src/` and tested across unit, integration, and UI test suites in `tests/`.

## System Overview

The application coordinates speech capture, transcription routing, text post-processing, and desktop input delivery across isolated functional layers.

```
+-------------------------------------------------------------+
|                      TypeWhisper.Linux                      |
|      (Avalonia Desktop UI, Linux Display & Input Host)      |
+------------------------------+------------------------------+
                               |
                               v
+-------------------------------------------------------------+
|                      TypeWhisper.Core                       |
|   (Audio Capture, Dictation Workflow, History, State)       |
+------------------------------+------------------------------+
                               |
                               v
+-------------------------------------------------------------+
|                   TypeWhisper.PluginSDK                     |
|         (ASR, LLM, TTS, Memory Provider Contracts)          |
+------------------------------+------------------------------+
                               |
                               v
+-------------------------------------------------------------+
|                          plugins/                           |
|   (Local Whisper, Sherpa, Cloud STT, LLM Cleanup, Memory)   |
+-------------------------------------------------------------+
```

## Core Subsystems

The [[src/TypeWhisper.Core]] library provides domain models, audio capture abstractions, transcription workflows, text transformation pipelines, and persistent storage.

Key responsibilities within [[src/TypeWhisper.Core]] include:
- Managing audio capture devices and buffer feeds.
- Orchestrating multi-stage dictation via [[dictation-pipeline]].
- Storing and querying dictation history and vocabulary overrides.
- Executing text replacement, formatting rules, and dictionary expansion.

## Plugin Architecture

External capabilities interface with the host application strictly through the shared [[src/TypeWhisper.PluginSDK]] contracts.

Plugins reside in the [[plugins]] directory and are packaged as standalone bundles containing their required dependencies. The host loads plugins dynamically at runtime, verifying binary compatibility against [[src/TypeWhisper.PluginSDK]] without permitting circular host dependencies or undocumented state sharing. See [[plugin-system]] for detailed lifecycle invariants.

## Desktop Integration

The [[src/TypeWhisper.Linux]] project hosts the desktop application using Avalonia UI and provides Linux display server integration.

It manages native windowing, floating control overlays, system tray integration, global hotkey registration, and clipboard manipulation across X11 and Wayland sessions. Detailed platform mechanisms are documented in [[desktop-linux]].

## CLI Interface

The [[src/TypeWhisper.Cli]] executable provides file transcription, control commands, and diagnostic inspection.

File transcription can run headlessly. Control commands communicate with the running desktop host; they do not replace that host's dictation lifecycle.

## Dashboard and settings

The desktop Dashboard shows model and shortcut readiness, setup actions, and recent activity before expandable usage statistics.

Readiness is based on runtime services and required setup checks. Failed required checks prevent a ready indication, and stale asynchronous refreshes cannot replace newer state. History, Profiles, and Plugins distinguish empty states, while Shortcuts and Dictionary put frequent controls first. Render tests cover six languages, two window sizes, and Light and Dark themes.

Profile selection is painted by the rounded profile tile. The list item's theme presenter stays transparent so the desktop accent does not leak into square corners or the spacing below the tile.
