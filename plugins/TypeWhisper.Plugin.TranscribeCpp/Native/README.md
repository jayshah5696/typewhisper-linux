# Native runtime

The plugin packages transcribe.cpp 0.4.1 for Linux x64. It uses the official `@transcribe-cpp/linux-x64-cpu-vulkan` artifact, not an installed T3 runtime. No Node process or inference server is required.

`runtime.json` pins the archive URL, SHA-256, C header contract, and hashes of every deployed file. `scripts/prepare-transcribe-native.py` checks the complete archive and file set before publishing it under the project's ignored `obj/` directory. The project copies those files to `native/linux-x64/` in the plugin output and independent plugin package. Keep the included upstream license notices with the libraries.

The artifact includes the base ggml libraries, dynamically selected CPU instruction-set modules, and the optional Vulkan module. CPU registration permits CPU modules only. A CPU worker must not load Vulkan or CUDA libraries. Vulkan requires the system Vulkan loader and a compatible hardware driver; the plugin does not redistribute kernel drivers.

The C# binding checks the runtime version and each used struct's native size and alignment before invoking its initializer. The artifact's header hash is `f99ad4169374b8f3`. Native initialization, probing, model loading, and decoding are restricted to the existing authenticated transcription worker. The assembly-specific library resolver uses this plugin's package directory. Native libraries stay loaded until that worker exits.

Every GPU request resolves a stable identity to a new process-local handle and requests that exact handle. A missing identity, software device, or mismatched loaded device is an error, not automatic native fallback. The host owns Auto retry policy.

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

Without the Vulkan flag, the test uses CPU only. With it, the test requires a discovered hardware Vulkan device and refuses CPU fallback. It prints the loaded device and, where available, the worker's Linux DRM compute counter before and after inference. It also checks a 95-second recording with an utterance after Moonshine's native input limit. This checks audio-window coverage, not continuous-speech boundary accuracy.

This artifact does not establish compatibility with ARM64 or every GPU. Other model architectures, long uninterrupted speech, distribution baselines, and GPU families need their own checks before making those claims.
