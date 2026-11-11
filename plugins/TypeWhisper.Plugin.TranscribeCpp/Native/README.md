# Native runtime

The plugin packages transcribe.cpp 0.4.1 for Linux x64. It uses the official `@transcribe-cpp/linux-x64-cpu-vulkan` artifact, not an installed T3 runtime. No Node process or inference server is required.

`runtime.json` pins the archive URL, SHA-256, C header contract, and hashes of every deployed file. `scripts/prepare-transcribe-native.py` checks the complete archive and file set before publishing it under the project's ignored `obj/` directory. The project copies those files to `native/linux-x64/` in the plugin output and independent plugin package. Keep the included upstream license notices with the libraries.

The artifact includes the base ggml libraries, dynamically selected CPU instruction-set modules, and the optional Vulkan module. CPU registration permits CPU modules only. A CPU worker must not load Vulkan or CUDA libraries. Vulkan requires the system Vulkan loader and a compatible hardware driver; the plugin does not redistribute kernel drivers.

The C# binding checks the runtime version and each used struct's native size and alignment before invoking its initializer. The artifact's header hash is `f99ad4169374b8f3`. Native initialization, probing, model loading, and decoding are restricted to the existing authenticated transcription worker. The assembly-specific library resolver uses this plugin's package directory. Native libraries stay loaded until that worker exits.

Every GPU request resolves a stable identity to a new process-local handle and requests that exact handle. A missing identity, software device, or mismatched loaded device is an error, not automatic native fallback. The host owns Auto retry policy.

## Optional CUDA

`cuda.json` pins the separate official Linux x64 CUDA artifact for the same engine and C header. The Settings download action verifies the archive and every library and license before publishing a complete runtime directory. It also prepares the CUDA 12 runtime and cuBLAS libraries through the shared provisioner. It does not download a kernel driver. Opening Settings, selecting Auto, discovery, and inference never start this download. Cancel keeps resumable downloads.

File readiness is not proof of execution. The preparation action does not change the requested mode, selected device, or loaded worker. Refresh devices checks baseline CPU/Vulkan and ready CUDA in separate disposable workers. A failed CUDA driver or missing optional files must not remove a working Vulkan path. Auto prefers ready native CUDA devices, then hardware Vulkan, then CPU. An explicit unavailable CUDA request fails and offers CPU retry.

Only a CUDA worker preloads the installed math libraries and uses the complete CUDA native build. It verifies all artifact hashes again and keeps its artifact lease until unload. CPU and Vulkan workers continue to use the baseline package, including its CPU dispatch modules. Different ggml builds are never mixed in one process. NVIDIA execution still requires compatible hardware and driver verification.

## Streaming behavior

The worker negotiates streaming transport separately from the selected model's capability. Session identity and sequence checks reject stale or reordered audio. Each bounded audio request waits for its reply before the sender continues, while the worker's control reader can still accept cancellation. A live inference failure does not retry only the failed chunk on CPU. The existing dictation flow must retry the retained complete recording instead.

Native sessions collect at most 500 ms before decoding and flush the remaining samples at EOF. Previews use the current raw hypothesis, including corrections. Each completed window produces one final result. Sessions rotate near their audio limit after at least 160 ms of quiet input. If uninterrupted speech reaches the hard boundary, the stream fails before native truncation and asks for complete-recording batch retry. It does not cut a live word and claim that the partial result is complete.

## Local model controls

Dictation settings can link an existing GGUF for a listed model. The file must match that catalog entry's exact size and SHA-256. Linking does not select the model or initialize native libraries. The external path stays visible, and Unlink removes only the app's receipt, not the source file. Custom license restrictions still apply.

Test model runs the public English fixture without cleanup, delivery, history, or usage updates. Another model uses a disposable worker and needs memory in addition to the model already loaded. A matching loaded model uses its current worker to avoid taking a second exclusive file lease. Testing does not save a default or provider selection. The result shows the backend and device confirmed for that test. Cancel ends the test, and recording cancels it automatically. The fixture's attribution ships in `Resources/ModelTest/README.md`.

Changing model storage unloads the active worker under the model-manager lock and rejects concurrent inference, model-file operations, and runtime preparation. Migration keeps source and destination lock sentinels held through the settings commit and cleanup. It never deletes their stable inodes. Foreign model leases refuse the move without changing settings. The plugin resolves the new asset root on subsequent file operations. Linked receipts move; their external weights and provider settings do not.

## Verification

Build with `just build-release` and run `just verify-plugins`. Ordinary tests use synthetic weights and do not download the model catalog.

An optional real-worker test uses an existing pinned Moonshine Streaming Medium Q8_0 GGUF read-only. It verifies its digest before linking it into a temporary store. It does not change the source file or the installed app.

```sh
export DOTNET_ROOT=/tmp/typewhisper-dotnet
export PATH="$DOTNET_ROOT:$PATH"
TYPEWHISPER_TRANSCRIBE_LIVE_MODEL=/absolute/path/moonshine-streaming-medium-Q8_0.gguf \
TYPEWHISPER_TRANSCRIBE_LIVE_VULKAN=1 \
dotnet test tests/TypeWhisper.PluginSystem.Tests -c Release --no-build \
  --filter FullyQualifiedName~TranscribeCppWorkerLiveTests \
  --logger 'console;verbosity=detailed'
```

To verify the exact optional CUDA archive without a GPU or native initialization:

```sh
TYPEWHISPER_TRANSCRIBE_CUDA_ARCHIVE=/absolute/path/transcribe-native-0.4.1-linux-x86_64-cuda.tar.gz \
dotnet test tests/TypeWhisper.PluginSystem.Tests -c Release --no-build \
  --filter FullyQualifiedName~OfficialCudaArchiveInstallsAndVerifiesWithoutLoadingNativeCode
```

The archive test installs into a temporary directory, verifies the complete pinned payload, and removes only that scratch directory. It does not prove CUDA execution.

Without the Vulkan flag, the model test uses CPU only. With it, the test requires a discovered hardware Vulkan device and refuses CPU fallback. It prints the loaded device and, where available, the worker's Linux DRM compute counter before and after inference. It checks batch and streaming results for the public fixture and a 95-second recording with an utterance after Moonshine's native input limit. Streaming must produce a nonempty preview, exactly one short-recording final, and two long-recording windows. This checks audio-window coverage, not continuous-speech boundary accuracy.

This artifact does not establish compatibility with ARM64 or every GPU. Other model architectures, long uninterrupted speech, distribution baselines, and GPU families need their own checks before making those claims.
