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

[[src/TypeWhisper.Linux/Services/ModelManagerService.cs|Model reuse]] checks the engine's current selection against the selection captured after loading. Saving plugin settings can change that selection. Reuse also requires matching acceleration and native device preferences.

### Pinned GGUF catalog and storage

[[plugins/TypeWhisper.Plugin.TranscribeCpp/TranscribeModelCatalog.cs|The GGUF catalog loader]] validates pinned ASR metadata without native initialization or network access. Its embedded catalog contains 59 reviewed file entries, not 59 verified executions.

[[plugins/TypeWhisper.Plugin.TranscribeCpp/Models/README.md|Catalog provenance]] records the source archive, engine compatibility, original publisher revisions, and reference measurement conditions. Original publisher license identifiers override conversion labels. Custom terms remain visible but block downloading, linking, and loading until reviewed.

[[plugins/TypeWhisper.Plugin.TranscribeCpp/TranscribeModelStore.cs|The model store]] uses the shared resumable downloader, full size and SHA-256 verification, and atomic publication. A verification receipt supports cheap readiness checks. Every load rehashes the file and holds a cross-process lease to prevent deletion or replacement by another store operation. Linked files retain external ownership and are never deleted or moved by the store.

[[tests/TypeWhisper.PluginSystem.Tests/TranscribeCppCatalogTests.cs|Catalog tests]] reject malformed paths, revisions, metadata, and duplicates. [[tests/TypeWhisper.PluginSystem.Tests/TranscribeCppModelStoreTests.cs|Storage tests]] cover resume, corruption, cancellation, ownership, symlinks, disk space, and model leases.

### Verified external model links

[[src/TypeWhisper.PluginSDK/ITranscriptionModelLinking.cs|Optional linking]] verifies an exact catalog artifact while preserving external ownership. Providers without the capability keep ordinary download behavior.

[[src/TypeWhisper.Linux/Services/ModelManagerService.ModelLinking.cs|The host]] uses the model lock and per-model file slot. [[src/TypeWhisper.Linux/Services/Plugins/TranscriptionWorkers/IsolatedTranscriptionEngine.ModelLinking.cs|The isolation proxy]] rejects replacement of a loaded model before touching the host file store. No native initialization or provider selection is needed to link a file.

[[src/TypeWhisper.Linux/ViewModels/Sections/DictationSectionViewModel.ModelLinking.cs|Settings linking controls]] use a local GGUF file picker and show verification progress, cancellation, failures, and the external source path. Linked models say Unlink in both removal controls and confirmation dialogs. A stale link remains removable when its source disappears. The source is never moved or deleted. Linking does not change the default model, and recording cancels an in-progress link to release the model lock.

[[tests/TypeWhisper.PluginSystem.Tests/TranscribeCppPluginTests.cs|Plugin tests]] check verified readiness, ownership, unloaded-only replacement, and no native initialization or settings writes. [[tests/TypeWhisper.Linux.Tests/DictationLocalModelCardsTests.cs|Control tests]] cover link readiness, cancellation, inline failures, unlink wording, and unchanged selection.

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

### Manual device controls

[[src/TypeWhisper.Linux/ViewModels/Sections/DictationSectionViewModel.Hardware.cs|Device controls]] keep the requested mode separate from preparation and confirmed execution. Opening Settings does not discover devices or save settings.

Explicit refresh uses a disposable discovery worker with a deadline shorter than the worker boundary. Model changes cancel discovery and reject stale results. Choices exclude unsupported, unavailable, and software devices. Collection changes are deferred and occur only when relevant state changes. Unchanged provider model lists preserve selector items and discovered devices.

[[src/TypeWhisper.Core/Models/AppSettings.cs|Hardware settings]] store stable device identities per provider and explicit backend. Auto and CPU ignore saved GPU identities. Missing saved devices remain visible only for their provider and backend. [[src/TypeWhisper.Linux/Services/ModelManagerService.cs|Model loading]] includes the requested identity in its cache comparison and invalidates the old configuration before a worker replacement can fail. Explicit choices never silently revert to CPU. The CPU retry action loads the same downloaded model without probing GPU drivers.

Confirmed backend and device appear only for a loaded execution. Fallback causes remain separate, including after unload. [[src/TypeWhisper.Linux/Services/HttpApiService.cs|The diagnostic API]] reports the requested stable identity separately from the active device. [[tests/TypeWhisper.Linux.Tests/DictationLocalModelCardsTests.cs|Control tests]] cover explicit discovery, stale results, deferred localization, unchanged provider notifications, truthful unloaded status, and CPU retry. [[tests/TypeWhisper.Core.Tests/Models/AppSettingsTests.cs|Settings tests]] cover old files, null maps, persistence, and preserving other provider and backend choices.

### Isolated native device selection

[[src/TypeWhisper.Linux/Services/Plugins/TranscriptionWorkers/IsolatedTranscriptionEngine.Hardware.cs|The worker hardware proxy]] discovers devices in a disposable process. Discovery does not replace a loaded model worker or report active execution.

[[src/TypeWhisper.Linux/Services/Plugins/TranscriptionWorkers/TranscriptionWorkerProtocol.cs|The authenticated protocol]] negotiates native device selection. [[src/TypeWhisper.Linux/Services/Plugins/TranscriptionWorkers/TranscriptionWorkerServer.cs|The worker]] requires one hardware configuration before loading and rejects loading after discovery in that process. This protects runtimes whose backend registration cannot change after initialization.

The proxy keeps original request intent. An Auto GPU load or inference failure gets one CPU retry in a fresh worker with the same model, audio, language, and prompt. An explicit GPU failure ends the worker and asks for an explicit CPU retry. Missing weights, unsupported requests, and caller cancellation do not trigger fallback. Status retains the cause separately and clears active-device facts after unload or worker loss. Legacy providers keep their existing crash retries and pause policy.

[[tests/TypeWhisper.Linux.Tests/TranscriptionWorkerTests.cs|Worker tests]] use real child processes to check discovery isolation, capability rejection, native faults, load errors, bounded CPU retry, explicit GPU and CPU failures, cancellation, timeout origin, reconfiguration, and legacy recovery.

### Isolated GGUF batch inference

[[plugins/TypeWhisper.Plugin.TranscribeCpp/TranscribeCppPlugin.cs|The transcribe.cpp plugin]] keeps metadata and file management in the host and restricts native work to transcription workers. It provides batch inference and model-specific streaming on CPU or Vulkan, with separately prepared CUDA support.

[[plugins/TypeWhisper.Plugin.TranscribeCpp/TranscribeNativeRuntime.cs|The native adapter]] validates ABI layouts before native initializers, resolves exact backend and stable device identities, and checks the loaded device. CPU registration excludes GPU modules. Sessions retain their model handles, and cancellation settles native work before freeing either handle. Borrowed text is copied before session mutation or disposal. Dictionary terms use only the native vocabulary capability, not transcript instructions.

[[plugins/TypeWhisper.Plugin.TranscribeCpp/TranscribeAudio.cs|Audio adaptation]] validates RIFF chunks and converts PCM16 mono 16 kHz to native float samples. It accepts ffmpeg pipe output only through explicit unknown-length RIFF and final data sentinels. Finite chunk lengths, format fields, and complete PCM16 samples remain checked. Bounded, non-overlapping windows prefer quiet boundaries and retain every sample. Native abort or output truncation is an error rather than partial success. This does not promise lossless word recognition at continuous-speech window cuts.

[[scripts/prepare-transcribe-native.py|Artifact preparation]] verifies the pinned archive and complete file hashes before publishing libraries and license notices. [[plugins/TypeWhisper.Plugin.TranscribeCpp/Native/runtime.json|The artifact pin]] covers CPU dispatch modules and optional Vulkan dependencies. The plugin requires no Node runtime, inference server, or T3 installation.

[[tests/TypeWhisper.PluginSystem.Tests/TranscribeCppNativeTests.cs|Native boundary tests]] cover host probe refusal, device exclusions, input validation, audio coverage, and incomplete results. [[tests/TypeWhisper.PluginSystem.Tests/TranscribeCppPluginTests.cs|Plugin lifetime tests]] check file leases, failed loads, cancellation before unload, repeated text, and prompt separation. [[tests/TypeWhisper.PluginSystem.Tests/TranscribeCppWorkerLiveTests.cs|The opt-in real-worker test]] checks packaged CPU and exact Vulkan execution, external-file preservation, CPU module exclusion, and an utterance beyond Moonshine's native audio limit.

### GGUF package verification

[[scripts/package-transcribe-plugin.py#build_package|The independent plugin packager]] verifies the complete native closure before publishing the ZIP. App packaging invokes it after pruning the final plugin payload.

The ZIP contains the entry point, dependency metadata, pinned CPU/Vulkan libraries, CPU dispatch modules, and license notices. It excludes a private PluginSDK and optional CUDA files. [[scripts/package-transcribe-plugin.py#validate_contents|Content validation]] checks the archived bytes against the reviewed pin and header contract, including changes during packaging. [[tests/verification/test_transcribe_plugin_package.py|Package tests]] cover corruption, missing notices, stale files, symlinks, entry points, and atomic failure.

[[scripts/verification/transcribe-cpp.md|Package verification]] combines the actual host loader, distribution container smoke checks, and opt-in worker tests against extracted packages. [[scripts/verification/transcribe_cpu_probe.c|The CPU probe]] checks native inference and mapped modules under constrained QEMU CPU profiles without GPU libraries. These checks do not establish GPU-family compatibility or performance.

### Explicit CUDA preparation

[[plugins/TypeWhisper.Plugin.TranscribeCpp/TranscribeCppPlugin.Cuda.cs|CUDA preparation]] downloads and verifies files only after the Settings action. It does not initialize native libraries in the host or change the requested mode or active worker.

[[plugins/TypeWhisper.Plugin.TranscribeCpp/Native/cuda.json|The CUDA artifact pin]] identifies a complete official build, including licenses, for the same header contract as the baseline package. [[plugins/TypeWhisper.Plugin.TranscribeCpp/TranscribeCudaArtifact.cs|The installer]] checks archive and file hashes, stages the complete closure, and publishes readiness atomically. Native use rehashes it under a retained file lease. Canceled transfers remain resumable; partial files never count as installed.

[[plugins/Shared/Cuda/CudaRuntimeProvisioner.cs|The shared provisioner]] prepares CUDA 12 runtime and cuBLAS files without host preloading. Only a CUDA worker checks the driver and preloads existing libraries. It never downloads during discovery or inference. Baseline CPU/Vulkan and ready CUDA discovery use separate disposable workers so a broken CUDA driver cannot poison Vulkan. Auto prefers eligible CUDA, hardware Vulkan, then CPU. Explicit GPU failures never silently change mode.

[[src/TypeWhisper.Linux/ViewModels/Sections/DictationSectionViewModel.cs|Settings controls]] support cancellation and distinguish ready files from confirmed execution. Preparation preserves selection and settings. [[tests/TypeWhisper.PluginSystem.Tests/TranscribeCppCudaTests.cs|Artifact tests]] cover malformed archives, corruption, leases, cancellation, and opt-in offline verification of the official archive. [[tests/TypeWhisper.Linux.Tests/IsolatedCudaDiscoveryTests.cs|Discovery tests]] cover unavailable CUDA without losing Vulkan. [[tests/TypeWhisper.Linux.Tests/DictationLocalModelCardsTests.cs|Control tests]] check explicit preparation and Vulkan raw tests without CUDA files.

### Model storage coordination

[[src/TypeWhisper.Linux/Services/ModelManagerService.Storage.cs|Storage changes]] hold the execution lock through unloading, copying, settings commit, and cleanup. Active leases or file operations refuse the move before settings change.

[[src/TypeWhisper.Linux/Services/ModelManagerService.Cuda.cs|Explicit runtime preparation]] also reserves a file-operation slot. Model storage moves and runtime clearing cannot race that preparation. Failed unload stops deletion or migration rather than treating a still-loaded model as released.

[[src/TypeWhisper.Core/Services/LocalModelStorageService.cs|Asset migration]] includes GGUF models and per-plugin runtimes but excludes provider settings. Existing source and destination sentinel locks remain held through commit and cleanup. Sentinel inodes are never copied over or unlinked. A foreign lease refuses migration without a settings change. External link receipts move without copying or changing their source weights.

[[plugins/TypeWhisper.Plugin.TranscribeCpp/TranscribeCppPlugin.cs|The plugin]] rebinds its model store and optional CUDA paths to the current host asset directory after unload. Worker launch resolves that directory per process start. [[tests/TypeWhisper.Core.Tests/Services/LocalModelStorageServiceTests.cs|Migration tests]] cover receipts, source ownership, foreign leases, retained sentinels, settings, and failure safety. [[tests/TypeWhisper.PluginSystem.Tests/ModelManagerServiceTests.cs|Manager tests]] cover lease and operation conflicts, failed copy recovery, and runtime preparation races.

### Isolated GGUF streaming

[[src/TypeWhisper.Linux/Services/Plugins/TranscriptionWorkers/IsolatedTranscriptionEngine.Streaming.cs|The streaming proxy]] forwards ordered, bounded PCM chunks through the authenticated worker. No native session object enters the host.

[[src/TypeWhisper.Linux/Services/Plugins/TranscriptionWorkers/TranscriptionWorkerServer.Streaming.cs|Worker sessions]] check session identity and sequence before accepting audio. Replies carry transcript updates for that operation, so the host applies backpressure without a second audio queue. The control reader remains independent of native work. Queue or update overflow is a fault, never dropped audio reported as success. Unload and worker replacement invalidate old proxies and their updates.

[[plugins/TypeWhisper.Plugin.TranscribeCpp/TranscribeCppPlugin.Streaming.cs|Native streaming]] buffers at most half a second before decoding and flushes the tail at EOF. It emits replaceable raw previews and one final per completed window. It does not commit cumulative prefixes as repeated final segments or remove repeated words. Near a model's audio limit it rotates at silence. Uninterrupted speech without a safe boundary faults before the hard limit, preserving full-recording batch retry. Cancellation settles native work before freeing its session or model.

Auto may retry a failed stream start on CPU before audio is sent. A feed or finalize fault ends the worker without replaying only that operation. [[src/TypeWhisper.Linux/Services/StreamingTranscriptionCoordinator.cs|The existing coordinator]] and dictation fallback retain responsibility for complete recordings, dropped capture audio, and slow senders.

[[tests/TypeWhisper.Linux.Tests/TranscriptionWorkerTests.cs|Real-worker tests]] cover negotiation, stale identities and updates, ordering, bounded chunks, corrected previews, repeated words, unload, cancellation, coordinator assembly, and slow-sender fallback eligibility. [[tests/TypeWhisper.PluginSystem.Tests/TranscribeCppWorkerLiveTests.cs|The opt-in packaged test]] checks CPU and exact Vulkan previews and two final windows across a 95-second recording. It does not establish continuous-speech boundary accuracy or other model-family support.

[[scripts/verification/transcribe_user_paths.py|The user-path check]] downloads through Settings, cancels and retries, selects the model, and records only a private virtual microphone. It verifies changing CPU/Vulkan overlay previews, both distinct public utterances in a long recording, worker-loss batch fallback, diagnostic finalization outcomes, History entries and final clipboard read-back. [[dictation-pipeline#Streaming preview snapshots|Streaming snapshots]] replace corrected or retracted drafts. Raw silence hypotheses can still be wrong even when final text is correct.

### Local model browser

[[src/TypeWhisper.Linux/ViewModels/Sections/DictationSectionViewModel.LocalModels.cs|The local model browser]] filters embedded catalogs without selecting a recognition language, saving settings, or probing native libraries.

Search matches model names, provider labels, and provider-qualified identities across the full catalog. Language filters require every checked language; undeclared languages do not match. Live-preview filtering requires known per-model support. Selected and busy cards remain visible even when they do not match. Filtering changes card visibility rather than replacing selector items. Interface-language changes defer filter-option replacement and preserve filters and operation progress.

[[src/TypeWhisper.Linux/ViewModels/Sections/LocalModelCard.cs|Model cards]] distinguish download size from memory requirements. They display known capabilities, publisher license terms, and reference results with their dataset, language, metric, conditions, and source. Unknown metadata remains unknown. Availability restrictions disable download and use, but downloaded files can still be removed. GPU compatibility is not inferred from catalog presence.

[[tests/TypeWhisper.Linux.Tests/DictationLocalModelCardsTests.cs|Browser tests]] cover intersections, unknown metadata, hidden search matches, selected and downloading cards, restrictions, and progress preservation across six interface languages. [[tests/TypeWhisper.UiTests/LocalModelCardListTests.cs|Rendered tests]] exercise search bindings and visibility without binding errors.

### Raw local model tests

[[src/TypeWhisper.Linux/Services/ModelManagerService.Testing.cs|Raw model tests]] use the public English LibriSpeech fixture under the model lease lock. They never enter cleanup, delivery, history, dictionary usage, learning, or statistics processing.

Only isolated providers can test without mirroring temporary selection into host settings. Another model runs in a disposable worker with a private execution preference and current native device resolution. Auto keeps its bounded CPU fallback policy. The live model is not evicted to make memory available for a test. A matching loaded model instead uses its existing worker, avoiding a second exclusive file lease. Different hardware for that same loaded file requires an explicit reload or unload.

[[src/TypeWhisper.Linux/Services/Plugins/TranscriptionWorkers/IsolatedTranscriptionEngine.Testing.cs|The worker test operation]] names its target directly and never calls the host selection or load-mirroring methods. Worker setting writes remain memory-only. Results report the device confirmed for that test, separately from ongoing execution. Missing on-demand CUDA runtimes require explicit preparation rather than test-triggered provisioning. Vulkan tests do not require optional CUDA files.

[[src/TypeWhisper.Linux/ViewModels/Sections/DictationSectionViewModel.ModelTesting.cs|Test controls]] show raw text or an error, pin the busy card through filters, and disable conflicting file operations. Recording cancels the test. A private deadline is a failure, not caller cancellation or partial success. Interface-language changes refresh labels without replacing the operation result. [[tests/fixtures/README.md|Fixture attribution]] ships with the app.

[[tests/TypeWhisper.Linux.Tests/TranscriptionWorkerTests.cs|Real-worker tests]] cover temporary selection isolation, settings preservation, same-model reuse, Auto fallback, faults, and cancellation. [[tests/TypeWhisper.Linux.Tests/DictationLocalModelCardsTests.cs|Control tests]] cover lease conflicts, recording cancellation, raw results, and no default changes. [[tests/TypeWhisper.UiTests/LocalModelCardListTests.cs|Rendered controls]] bind both cancellation actions to the generic cancel label rather than the download action.

### Optional model metadata

[[src/TypeWhisper.PluginSDK/Models/PluginModelInfo.cs|Model metadata]] can declare languages, streaming, automatic language detection, publisher terms, availability restrictions, and reference benchmark provenance. Missing values remain unknown.

[[src/TypeWhisper.PluginSDK/Models/PluginModelBenchmark.cs|Reference benchmarks]] identify the dataset, language, metric, source, and measurement conditions. They do not prove local accuracy or GPU execution. [[tests/TypeWhisper.PluginSystem.Tests/TranscriptionHardwareSelectionTests.cs|Contract tests]] cover legacy defaults, serializable identities, exact device choice, and selection exclusions. [[tests/TypeWhisper.PluginSystem.Tests/ModelManagerServiceTests.cs|Model manager tests]] cover intent preservation, provider filtering, CPU probe avoidance, and discovery cancellation faults.
