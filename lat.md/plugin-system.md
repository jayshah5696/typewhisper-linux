# Plugin System

TypeWhisper features a modular plugin architecture that decouples speech recognition, LLM text cleanup, text-to-speech, and memory persistence from the host application.

All plugins live under the [[plugins]] directory and are loaded dynamically into isolated plugin contexts.

## PluginSDK Boundary

The [[src/TypeWhisper.PluginSDK]] package defines the contracts, models, and interfaces that all plugins must implement.

Critical invariants for the SDK boundary include:
- **Binary compatibility**: The installed host application and all bundled plugins must compile against and target the exact same PluginSDK version.
- **Error preservation**: When a plugin loader encounters assembly or dependency resolution failures, it must preserve the underlying loader exception for diagnostic reporting.
- **Isolation**: Plugins must not reference internal host types or cross-reference other plugin assemblies directly.

## ASR and Transcription Providers

Speech recognition plugins convert PCM audio byte streams into raw transcript tokens with timestamps and confidence scores.

Supported transcription plugins include:
- **Local offline engines**: Needle Whistle, Sherpa-ONNX, Whisper.cpp, Parakeet CTC, and Qwen3 STT.
- **Cloud services**: Deepgram, AssemblyAI, Gladia, Soniox, Speechmatics, Google Cloud STT, and Cloudflare ASR.

## LLM and Processing Providers

Text processing plugins enhance raw transcripts through grammar correction, filler-word stripping, tone adaptation, or arbitrary prompt execution.

Processing providers implement single-turn and streaming transformation APIs. Built-in plugins include OpenAI, Anthropic Claude, Google Gemini, Groq, Cerebras, Cohere, Fireworks, Meta, OpenRouter, and xAI.

## Catalog and Settings Transactions

The plugin catalog aggregates metadata, configuration schemas, and deployment state across all bundled and local plugins.

Key rules for catalog and settings handling:
- Opening the plugin settings dialog must not trigger network catalog refreshes or mutate persistent settings.
- Explicit user refresh preserves in-progress form edits and persists only dirty fields.
- Provider refreshes are bounded by strict deadlines and cancel immediately if credentials change or a newer refresh begins.

## Acceleration and Native Runtimes

Plugins utilizing hardware acceleration must verify native library availability and GPU compatibility before binding execution backends.

When hardware acceleration (e.g. Vulkan, WebGPU, or ROCm/MIGraphX) is requested:
- The plugin probes the native loader and device environment first.
- Legacy providers keep their existing CPU fallback behavior and report its reason. Providers using optional native device selection distinguish Auto from an explicit GPU choice.
- UI elements distinguish between requested acceleration, preparing state, and the active execution backend.

### Optional native device selection

The optional [[src/TypeWhisper.PluginSDK/ITranscriptionHardwareSelection.cs|hardware capability]] separates provider-native discovery from legacy backend settings. Providers without it keep their existing policy.

[[src/TypeWhisper.Linux/Services/ModelManagerService.cs|Model loading]] gives discovery a deadline and skips it for an explicit CPU request. It filters devices against the provider's declared backends. [[src/TypeWhisper.Linux/Services/TranscriptionHardwareSelector.cs|The host selector]] prefers ready CUDA, hardware Vulkan, then CPU for Auto. It excludes software and unknown devices, prefers discrete GPUs, and matches explicit devices by backend and stable identity. An unavailable explicit GPU choice fails with a CPU retry instruction.

[[src/TypeWhisper.PluginSDK/Models/TranscriptionDevice.cs|The hardware request]] retains the original Auto or explicit intent while the legacy setter receives a concrete backend. [[src/TypeWhisper.PluginSDK/Models/TranscriptionAccelerationStatus.cs|Execution status]] can report loaded state, a confirmed device, and a fallback reason separately. [[src/TypeWhisper.Linux/Services/HttpApiService.cs|The diagnostic API]] omits active backend and device facts for a confirmed unloaded model. Legacy unknown loaded state preserves existing API behavior.

### Isolated native device selection

[[src/TypeWhisper.Linux/Services/Plugins/TranscriptionWorkers/IsolatedTranscriptionEngine.Hardware.cs|The worker hardware proxy]] discovers devices in a disposable process. Discovery does not replace a loaded model worker or report active execution.

[[src/TypeWhisper.Linux/Services/Plugins/TranscriptionWorkers/TranscriptionWorkerProtocol.cs|The authenticated protocol]] negotiates native device selection. [[src/TypeWhisper.Linux/Services/Plugins/TranscriptionWorkers/TranscriptionWorkerServer.cs|The worker]] requires one hardware configuration before loading and rejects loading after discovery in that process. This protects runtimes whose backend registration cannot change after initialization.

The proxy keeps original request intent. An Auto GPU load or inference failure gets one CPU retry in a fresh worker with the same model, audio, language, and prompt. An explicit GPU failure ends the worker and asks for an explicit CPU retry. Missing weights, unsupported requests, and caller cancellation do not trigger fallback. Status retains the cause separately and clears active-device facts after unload or worker loss. Legacy providers keep their existing crash retries and pause policy.

[[tests/TypeWhisper.Linux.Tests/TranscriptionWorkerTests.cs|Worker tests]] use real child processes to check discovery isolation, capability rejection, native faults, load errors, bounded CPU retry, explicit GPU and CPU failures, cancellation, timeout origin, reconfiguration, and legacy recovery.

### Optional model metadata

[[src/TypeWhisper.PluginSDK/Models/PluginModelInfo.cs|Model metadata]] can declare languages, streaming, automatic language detection, publisher terms, availability restrictions, and reference benchmark provenance. Missing values remain unknown.

[[src/TypeWhisper.PluginSDK/Models/PluginModelBenchmark.cs|Reference benchmarks]] identify the dataset, language, metric, source, and measurement conditions. They do not prove local accuracy or GPU execution. [[tests/TypeWhisper.PluginSystem.Tests/TranscriptionHardwareSelectionTests.cs|Contract tests]] cover legacy defaults, serializable identities, exact device choice, and selection exclusions. [[tests/TypeWhisper.PluginSystem.Tests/ModelManagerServiceTests.cs|Model manager tests]] cover intent preservation, provider filtering, CPU probe avoidance, and discovery cancellation faults.
