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
- **Local offline engines**: Needle Whistle, Sherpa-ONNX, Whisper.cpp, transcribe.cpp, Parakeet CTC, and Qwen3 STT.
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

### Pinned GGUF catalog and storage

[[plugins/TypeWhisper.Plugin.TranscribeCpp/TranscribeModelCatalog.cs|The GGUF catalog loader]] validates pinned ASR metadata without native initialization or network access. Its embedded catalog contains 59 reviewed file entries, not 59 verified executions.

[[plugins/TypeWhisper.Plugin.TranscribeCpp/Models/README.md|Catalog provenance]] records the source archive, engine compatibility, original publisher revisions, and reference measurement conditions. Original publisher license identifiers override conversion labels. Custom terms remain visible but block downloading, linking, and loading until reviewed.

[[plugins/TypeWhisper.Plugin.TranscribeCpp/TranscribeModelStore.cs|The model store]] uses the shared resumable downloader, full size and SHA-256 verification, and atomic publication. A verification receipt supports cheap readiness checks. Every load rehashes the file and holds a cross-process lease to prevent deletion or replacement by another store operation. Linked files retain external ownership and are never deleted or moved by the store.

[[tests/TypeWhisper.PluginSystem.Tests/TranscribeCppCatalogTests.cs|Catalog tests]] reject malformed paths, revisions, metadata, and duplicates. [[tests/TypeWhisper.PluginSystem.Tests/TranscribeCppModelStoreTests.cs|Storage tests]] cover resume, corruption, cancellation, ownership, symlinks, disk space, and model leases.

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

### Isolated GGUF batch inference

[[plugins/TypeWhisper.Plugin.TranscribeCpp/TranscribeCppPlugin.cs|The transcribe.cpp plugin]] keeps metadata and file management in the host and restricts native work to transcription workers. It provides batch inference and model-specific streaming on CPU or Vulkan.

[[plugins/TypeWhisper.Plugin.TranscribeCpp/TranscribeNativeRuntime.cs|The native adapter]] validates ABI layouts before native initializers, resolves exact backend and stable device identities, and checks the loaded device. CPU registration excludes GPU modules. Sessions retain their model handles, and cancellation settles native work before freeing either handle. Borrowed text is copied before session mutation or disposal. Dictionary terms use only the native vocabulary capability, not transcript instructions.

[[plugins/TypeWhisper.Plugin.TranscribeCpp/TranscribeAudio.cs|Audio adaptation]] validates RIFF chunks and converts PCM16 mono 16 kHz to native float samples. Bounded, non-overlapping windows prefer quiet boundaries and retain every sample. Native abort or output truncation is an error rather than partial success. This does not promise lossless word recognition at continuous-speech window cuts.

[[scripts/prepare-transcribe-native.py|Artifact preparation]] verifies the pinned archive and complete file hashes before publishing libraries and license notices. [[plugins/TypeWhisper.Plugin.TranscribeCpp/Native/runtime.json|The artifact pin]] covers CPU dispatch modules and optional Vulkan dependencies. The plugin requires no Node runtime, inference server, or T3 installation.

[[tests/TypeWhisper.PluginSystem.Tests/TranscribeCppNativeTests.cs|Native boundary tests]] cover host probe refusal, device exclusions, input validation, audio coverage, and incomplete results. [[tests/TypeWhisper.PluginSystem.Tests/TranscribeCppPluginTests.cs|Plugin lifetime tests]] check file leases, failed loads, cancellation before unload, repeated text, and prompt separation. [[tests/TypeWhisper.PluginSystem.Tests/TranscribeCppWorkerLiveTests.cs|The opt-in real-worker test]] checks packaged CPU and exact Vulkan execution, external-file preservation, CPU module exclusion, and an utterance beyond Moonshine's native audio limit.

### Isolated GGUF streaming

[[src/TypeWhisper.Linux/Services/Plugins/TranscriptionWorkers/IsolatedTranscriptionEngine.Streaming.cs|The streaming proxy]] forwards ordered, bounded PCM chunks through the authenticated worker. No native session object enters the host.

[[src/TypeWhisper.Linux/Services/Plugins/TranscriptionWorkers/TranscriptionWorkerServer.Streaming.cs|Worker sessions]] check session identity and sequence before accepting audio. Replies carry transcript updates for that operation, so the host applies backpressure without a second audio queue. The control reader remains independent of native work. Queue or update overflow is a fault, never dropped audio reported as success. Unload and worker replacement invalidate old proxies and their updates.

[[plugins/TypeWhisper.Plugin.TranscribeCpp/TranscribeCppPlugin.Streaming.cs|Native streaming]] buffers at most half a second before decoding and flushes the tail at EOF. It emits replaceable raw previews and one final per completed window. It does not commit cumulative prefixes as repeated final segments or remove repeated words. Near a model's audio limit it rotates at silence. Uninterrupted speech without a safe boundary faults before the hard limit, preserving full-recording batch retry. Cancellation settles native work before freeing its session or model.

Auto may retry a failed stream start on CPU before audio is sent. A feed or finalize fault ends the worker without replaying only that operation. [[src/TypeWhisper.Linux/Services/StreamingTranscriptionCoordinator.cs|The existing coordinator]] and dictation fallback retain responsibility for complete recordings, dropped capture audio, and slow senders.

[[tests/TypeWhisper.Linux.Tests/TranscriptionWorkerTests.cs|Real-worker tests]] cover negotiation, stale identities and updates, ordering, bounded chunks, corrected previews, repeated words, unload, cancellation, coordinator assembly, and slow-sender fallback eligibility. [[tests/TypeWhisper.PluginSystem.Tests/TranscribeCppWorkerLiveTests.cs|The opt-in packaged test]] checks CPU and exact Vulkan previews and two final windows across a 95-second recording. It does not establish continuous-speech boundary accuracy or other model-family support.

### Local model browser

[[src/TypeWhisper.Linux/ViewModels/Sections/DictationSectionViewModel.LocalModels.cs|The local model browser]] filters embedded catalogs without selecting a recognition language, saving settings, or probing native libraries.

Search matches model names, provider labels, and provider-qualified identities across the full catalog. Language filters require every checked language; undeclared languages do not match. Live-preview filtering requires known per-model support. Selected and busy cards remain visible even when they do not match. Filtering changes card visibility rather than replacing selector items. Interface-language changes defer filter-option replacement and preserve filters and operation progress.

[[src/TypeWhisper.Linux/ViewModels/Sections/LocalModelCard.cs|Model cards]] distinguish download size from memory requirements. They display known capabilities, publisher license terms, and reference results with their dataset, language, metric, conditions, and source. Unknown metadata remains unknown. Availability restrictions disable download and use, but downloaded files can still be removed. GPU compatibility is not inferred from catalog presence.

[[tests/TypeWhisper.Linux.Tests/DictationLocalModelCardsTests.cs|Browser tests]] cover intersections, unknown metadata, hidden search matches, selected and downloading cards, restrictions, and progress preservation across six interface languages. [[tests/TypeWhisper.UiTests/LocalModelCardListTests.cs|Rendered tests]] exercise search bindings and visibility without binding errors.

### Optional model metadata

[[src/TypeWhisper.PluginSDK/Models/PluginModelInfo.cs|Model metadata]] can declare languages, streaming, automatic language detection, publisher terms, availability restrictions, and reference benchmark provenance. Missing values remain unknown.

[[src/TypeWhisper.PluginSDK/Models/PluginModelBenchmark.cs|Reference benchmarks]] identify the dataset, language, metric, source, and measurement conditions. They do not prove local accuracy or GPU execution. [[tests/TypeWhisper.PluginSystem.Tests/TranscriptionHardwareSelectionTests.cs|Contract tests]] cover legacy defaults, serializable identities, exact device choice, and selection exclusions. [[tests/TypeWhisper.PluginSystem.Tests/ModelManagerServiceTests.cs|Model manager tests]] cover intent preservation, provider filtering, CPU probe avoidance, and discovery cancellation faults.
