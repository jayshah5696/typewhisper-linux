# transcribe.cpp package checks

These checks use Linux x64 packages and public audio. They do not install or restart the user's app. Use a scratch directory for all extracted packages, model receipts, and test settings. Container package installs must stay inside disposable containers.

## Independent plugin and app packages

Run `scripts/build-linux-packages.sh <host-version> <scratch-output>` in the release build environment. It produces the independent `transcribe-cpp-linux-x64-<host-version>.zip` with the app packages. The ZIP uses the same PluginSDK assembly version as that host. It does not contain its own PluginSDK assembly.

The packager verifies every baseline native file against `Native/runtime.json`, including all CPU dispatch modules and license notices. It rejects extra files, symlinks, corrupt libraries, and a different native header contract. It verifies the archived bytes before replacing the destination ZIP. CUDA libraries and model weights are not in this baseline ZIP.

For an existing deployed plugin directory, build the same ZIP directly:

```sh
uv run --no-project scripts/package-transcribe-plugin.py \
  /scratch/app/Plugins/com.typewhisper.transcribe-cpp /scratch/transcribe-cpp.zip
uv run --no-project python -m zipfile -e /scratch/transcribe-cpp.zip \
  /scratch/independent/Plugins/com.typewhisper.transcribe-cpp
just verify-plugins /scratch/app /scratch/independent/Plugins
just verify-plugins /scratch/app /scratch/app/Plugins
bash scripts/smoke-test-linux-packages.sh <host-version> <scratch-output>
```

The full smoke test checks all four app package formats, bundle identity, ELF dependencies and package-manager requirements. It installs, upgrades, starts, and removes packages only in disposable Ubuntu and Fedora containers. Build native dependencies in the release distribution, not a newer rolling distribution. A local Sherpa build can otherwise raise the whole app's GLIBC requirement above the release baseline. Do not lower package dependency metadata to hide that requirement.

## Packaged worker without GPU libraries

The opt-in worker test accepts these optional overrides:

```sh
TYPEWHISPER_TRANSCRIBE_LIVE_MODEL=/models/moonshine-streaming-medium-Q8_0.gguf \
TYPEWHISPER_TRANSCRIBE_LIVE_PACKAGE=/scratch/independent/Plugins/com.typewhisper.transcribe-cpp \
TYPEWHISPER_TRANSCRIBE_LIVE_HOST=/scratch/app/typewhisper \
dotnet test tests/TypeWhisper.PluginSystem.Tests -c Release --no-build \
  --filter FullyQualifiedName~TranscribeCppWorkerLiveTests \
  --logger 'console;verbosity=detailed'
```

Run this command in a disposable Ubuntu 24.04 container with the matching .NET SDK and ordinary .NET system dependencies. Mount the checkout, extracted app, independent plugin, and approved model read-only. Give the container its own writable `/tmp` and HOME. Do not pass `/dev/dri`, NVIDIA devices, or host GPU libraries into the container. Before the test, confirm `ldconfig -p` lists no Vulkan, CUDA, or cuBLAS libraries. Leave `TYPEWHISPER_TRANSCRIBE_LIVE_VULKAN` unset.

The test verifies the model's full catalog digest before loading it. It requires CPU batch and streaming results for the fixture, changing previews, and both utterances in a 95-second recording. The CPU worker's mapped libraries must exclude Vulkan and CUDA. It also verifies that the source model survives unchanged after unlinking the scratch receipt. This checks the actual packaged worker and managed binding, not just a native sample program.

## CPU instruction dispatch

`scripts/verification/transcribe_cpu_probe.c` checks the native CPU path under a constrained x64 CPU. Compile and run it only in a disposable container with GCC, libc development headers, libstdc++ and `qemu-user`. The container does not need GPU devices or GPU libraries.

Fetch the exact 0.4.1 header and verify it before compiling:

```sh
curl --fail --location \
  https://raw.githubusercontent.com/handy-computer/transcribe.cpp/v0.4.1/include/transcribe.h \
  --output /scratch/transcribe.h
printf '%s  %s\n' \
  6eeff40fa7d9d3b17eb021170fda4046b5f54794ddfe4295bcf62bbc6015f652 \
  /scratch/transcribe.h | sha256sum --check
```

Use the catalog's `moonshine-tiny-Q8_0.gguf` for this shorter emulation check. Its expected size is 35,466,912 bytes and SHA-256 is `2fd348d7b38f97d309cc3ec6848f3f57f537b80244950f07d2637e463f95a3a1`. Verify both before running. The model is separate from the plugin package.

Convert the approved fixture to mono float32 PCM using Python's standard library:

```python
import array
import sys
import wave

with wave.open("tests/fixtures/librispeech-1462-170145-0012.wav") as source:
    assert source.getnchannels() == 1 and source.getframerate() == 16000
    assert source.getsampwidth() == 2
    samples = array.array("h", source.readframes(source.getnframes()))
if sys.byteorder != "little":
    samples.byteswap()
pcm = array.array("f", (sample / 32768 for sample in samples))
if sys.byteorder != "little":
    pcm.byteswap()
with open("/scratch/fixture.f32", "wb") as target:
    pcm.tofile(target)
```

Inside the container, let `/native` be the read-only `native/linux-x64` directory from the independent ZIP. Compile the probe there, then run it with two CPU profiles:

```sh
gcc -O2 -I/scratch scripts/verification/transcribe_cpu_probe.c \
  -L/native -Wl,-rpath,/native -ltranscribe -o /scratch/cpu-probe
timeout 480 qemu-x86_64 -cpu qemu64 /scratch/cpu-probe \
  /native /models/moonshine-tiny-Q8_0.gguf /scratch/fixture.f32
timeout 480 qemu-x86_64 -cpu Nehalem /scratch/cpu-probe \
  /native /models/moonshine-tiny-Q8_0.gguf /scratch/fixture.f32
```

The probe prints CPUID features, the active backend, transcript, and selected CPU module mappings. It fails on native errors, a non-CPU backend, an empty or unrelated transcript, or a mapped GPU library. The qemu64 profile has SSE2 but no SSE4.2, AVX, or FMA. The Nehalem profile has SSE4.2 but no AVX. These checks verify dispatch and inference for those emulated CPU profiles, not performance or every physical x64 processor.

## Settings downloads and live dictation

With a published payload, run the user-path check on a machine with hardware Vulkan. It requires the hidden desktop dependencies described in the project verification skill and `xclip` on PATH. The command downloads the pinned Moonshine Streaming Medium model into its own instance. It also downloads an 11-second public JFK speech fixture from the pinned whisper.cpp tag and checks its SHA-256.

```sh
uv run --no-project scripts/verification/transcribe_user_paths.py \
  --out /scratch/new-gguf-user-check \
  --app-dir /scratch/app
```

The check clicks Download, cancels after a partial file exists, retries, verifies the entire model digest, and clicks Use model. It checks that downloading alone does not save a default model. It records only its private virtual microphone and reads delivered text from its private X11 clipboard.

Both `liveTranscriptionEnabled` and `liveTranscriptionStreamingEnabled` are enabled in this fixture. The second flag selects streaming rather than batch polling. The check reads changing overlay previews during CPU and Vulkan dictation. It then plays a 110-second recording containing distinct public utterances at the beginning and after 86 seconds. Both utterances must reach the clipboard. A final session kills only this isolated app's worker during capture and requires full-recording batch recovery. Diagnostic events must show three successful streaming finalizations and one failed stream followed by successful delivery. History must contain all four completed sessions.

Raw previews are replaceable hypotheses, not final recognition. Moonshine can emit incorrect words or unrelated text during prolonged silence. This was observed in the long recording; the authoritative final text contained both public utterances without that text. Do not describe this check as proof of transcript quality for all audio. Streaming snapshots replace obsolete hypotheses rather than preserving their incorrect prefixes. The existing final dictation normalization policy still collapses adjacent repeated phrases. This is why the long fixture uses two distinct utterances rather than repeating the same sentence.

Each session records transcription, post-processing, delivery, and stop-to-delivery times separately. A reused stream's `transcription_milliseconds` excludes the recognition performed during capture and its separate finalization stage. These correctness runs are not a CPU/Vulkan performance comparison.

For History recovery, use `recovery_smoke.py` with `--plugin com.typewhisper.transcribe-cpp --engine transcribe-cpp --model handy-computer/moonshine-streaming-medium-gguf` and a disposable copy of a verified downloaded model. Rebase the managed receipt path when copying it to a different asset root; never edit a user receipt. The normal load still verifies the complete file. The recovery check dictates public audio, expands the actual History row, reads Copy original and Copy new result back from the private clipboard, and checks dismissal, collapse and timeout behavior.

For upgrade regression, `verify_upgrade.py --out /scratch/new-upgrade-check` copies the existing installation's program files and upgrades only that scratch copy. It checks synthetic settings, History, model and recording sentinels, then verifies Needle file transcription and virtual-microphone dictation through the upgraded launcher. It never installs over or restarts the user's app.

## Verification boundaries

The available physical GPU is an AMD Radeon 780M integrated GPU. Its Vulkan execution is checked separately with the worker test's Vulkan flag and DRM compute counters. A GPU-free container and broken-driver simulation do not prove NVIDIA CUDA, Intel Vulkan, or AMD discrete GPU execution. Those remain hardware verification requirements. ARM64 is not advertised by this plugin.
