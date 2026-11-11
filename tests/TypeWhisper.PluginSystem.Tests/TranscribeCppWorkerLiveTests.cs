extern alias TranscribeCpp;

using System.Globalization;
using Moq;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;
using TranscribeCpp::TypeWhisper.Plugin.TranscribeCpp;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.Processes;
using Xunit.Abstractions;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class TranscribeCppWorkerLiveTests(ITestOutputHelper output)
{
    [TranscribeLiveFact]
    public async Task PinnedRuntimeTranscribesPublicAudioOnExactCpuAndVulkanWithoutTouchingExternalWeights()
    {
        var root = RepositoryRoot();
        var external = Path.GetFullPath(Environment.GetEnvironmentVariable("TYPEWHISPER_TRANSCRIBE_LIVE_MODEL")!);
        var catalog = TranscribeModelCatalog.LoadEmbedded();
        var definition = catalog.Models.Single(model => model.Filename == Path.GetFileName(external));
        var package = Environment.GetEnvironmentVariable("TYPEWHISPER_TRANSCRIBE_LIVE_PACKAGE")
            ?? Path.Combine(root, "src/TypeWhisper.Linux/bin/Release/net10.0/Plugins/com.typewhisper.transcribe-cpp");
        var executable = Environment.GetEnvironmentVariable("TYPEWHISPER_TRANSCRIBE_LIVE_HOST")
            ?? Path.Combine(root, "src/TypeWhisper.Linux/bin/Release/net10.0/typewhisper");
        var scratch = Path.Combine(Path.GetTempPath(), "tw-gguf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        var before = new FileInfo(external).LastWriteTimeUtc;
        try
        {
            var assets = Path.Combine(scratch, "assets");
            using var client = new HttpClient();
            var store = new TranscribeModelStore(Path.Combine(assets, "models"), client);
            await store.LinkAsync(definition, external, CancellationToken.None);
            var loader = new PluginLoader(Path.Combine(scratch, "PluginData"));
            var loaded = loader.LoadPlugin(package) ?? throw new InvalidOperationException(string.Join("\n", loader.LastLoadFailures));
            var host = new Mock<IPluginHostServices>();
            host.SetupGet(value => value.PluginAssetDirectory).Returns(assets);
            await loaded.Instance.ActivateAsync(host.Object);
            var inner = Assert.IsAssignableFrom<ITranscriptionEngineRole>(loaded.Instance);
            inner.SelectModel(definition.Id);
            var logs = new List<string>();
            void Log(PluginLogLevel level, string text) { lock (logs) logs.Add($"{level}: {text}"); }
            var sockets = Path.Combine(scratch, "s");
            Directory.CreateDirectory(sockets);
            var launch = new TranscriptionWorkerLaunch(new ProcessCommand(executable, [TranscriptionWorkerServer.Argument]),
                sockets, package, Path.Combine(scratch, "PluginData"), assets, inner.GetTranscriptionSelectionId());
            var supervisor = new ProcessRunner();
            await using (var engine = IsolatedTranscriptionEngine.Create(inner, supervisor, launch, Log))
            {
                var useVulkan = Environment.GetEnvironmentVariable("TYPEWHISPER_TRANSCRIBE_LIVE_VULKAN") == "1";
                IReadOnlyList<TranscriptionDevice> devices = useVulkan
                    ? await engine.HardwareSelection!.DiscoverDevicesAsync(definition.Id, CancellationToken.None)
                    : [TranscriptionHardwareSelector.CpuDevice];
                output.WriteLine("Devices: " + System.Text.Json.JsonSerializer.Serialize(devices));
                var backends = useVulkan
                    ? new[] { TranscriptionAccelerationBackend.Cpu, TranscriptionAccelerationBackend.Vulkan }
                    : [TranscriptionAccelerationBackend.Cpu];
                var wav = await File.ReadAllBytesAsync(Path.Combine(root, "tests/fixtures/librispeech-1462-170145-0012.wav"));
                foreach (var backend in backends)
                {
                    var request = TranscriptionHardwareSelector.Resolve(TranscriptionHardwareSelector.ToPreference(backend), devices);
                    engine.SetAccelerationPreference(TranscriptionHardwareSelector.ToPreference(backend));
                    engine.HardwareSelection!.ConfigureHardware(request);
                    await engine.LoadModelAsync(definition.Id, CancellationToken.None);
                    Assert.True(engine.AccelerationStatus.IsModelLoaded);
                    Assert.Equal(backend, engine.AccelerationStatus.ActiveBackend);
                    Assert.Equal(request.Device.StableId, engine.AccelerationStatus.ActiveDevice!.StableId);
                    if (backend == TranscriptionAccelerationBackend.Cpu)
                    {
                        var libraries = await File.ReadAllTextAsync($"/proc/{engine.WorkerProcessId!.Value}/maps");
                        Assert.DoesNotContain("libggml-vulkan", libraries, StringComparison.Ordinal);
                        Assert.DoesNotContain("libvulkan.so", libraries, StringComparison.Ordinal);
                        Assert.DoesNotContain("libcuda.so", libraries, StringComparison.Ordinal);
                    }
                    var gpuBefore = ReadComputeCounters(engine.WorkerProcessId!.Value);
                    var result = await engine.TranscribeAsync(wav, "en", false, null, CancellationToken.None);
                    var gpuAfter = ReadComputeCounters(engine.WorkerProcessId!.Value);
                    output.WriteLine($"{backend}: {result.Text}; status={System.Text.Json.JsonSerializer.Serialize(engine.AccelerationStatus)}; compute before={gpuBefore}; after={gpuAfter}");
                    Assert.Contains("bent his face over her hair", result.Text, StringComparison.OrdinalIgnoreCase);
                    Assert.False(engine.UsesCpuFallback);
                    if (backend == TranscriptionAccelerationBackend.Vulkan && gpuAfter > 0)
                        Assert.True(gpuAfter > gpuBefore, "The AMD compute counter must increase during inference, not only model load.");
                    // A second utterance starts after Moonshine's 81.92 s native limit. A single
                    // native call silently omits it; the adapter must deliver BOTH audio windows.
                    var source = TranscribeAudio.ReadWave(wav);
                    var longSamples = new short[95 * 16_000];
                    var phrase = source.Select(sample => (short)Math.Round(sample * 32768)).ToArray();
                    phrase.CopyTo(longSamples, 0);
                    phrase.CopyTo(longSamples, 86 * 16_000);
                    var longResult = await engine.TranscribeAsync(TranscribeCppNativeTests.Wave(longSamples), "en", false, null, CancellationToken.None);
                    output.WriteLine($"{backend} 95-second recording: {longResult.Text}");
                    Assert.Equal(2, longResult.Text.Split("He bent", StringSplitOptions.None).Length - 1);
                    Assert.True(engine.SupportsStreaming);
                    var streamGpuBefore = ReadComputeCounters(engine.WorkerProcessId!.Value);
                    var shortUpdates = await StreamAsync(engine, phrase);
                    var shortFinal = Assert.Single(shortUpdates, update => update.IsFinal);
                    Assert.Contains("bent his face over her hair", shortFinal.Text, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains(shortUpdates, update => !update.IsFinal && !string.IsNullOrWhiteSpace(update.Text));
                    var streamGpuAfter = ReadComputeCounters(engine.WorkerProcessId!.Value);
                    if (backend == TranscriptionAccelerationBackend.Vulkan && streamGpuAfter > 0)
                        Assert.True(streamGpuAfter > streamGpuBefore, "Streaming must perform GPU compute, not only load weights.");
                    output.WriteLine($"{backend} streaming: {shortFinal.Text}; previews={shortUpdates.Count(update => !update.IsFinal)}; compute before={streamGpuBefore}; after={streamGpuAfter}");
                    var longUpdates = await StreamAsync(engine, longSamples);
                    var longFinal = string.Join("\n", longUpdates.Where(update => update.IsFinal).Select(update => update.Text));
                    output.WriteLine($"{backend} 95-second streaming: {longFinal}; windows={longUpdates.Count(update => update.IsFinal)}");
                    Assert.True(longUpdates.Count(update => update.IsFinal) >= 2);
                    Assert.Equal(2, longFinal.Split("He bent", StringSplitOptions.None).Length - 1);
                    await engine.UnloadModelAsync();
                    Assert.False(engine.AccelerationStatus.IsModelLoaded);
                    Assert.Null(engine.AccelerationStatus.ActiveDevice);
                }
            }
            await loaded.Instance.DeactivateAsync();
            loaded.Instance.Dispose();
            Assert.Equal(before, new FileInfo(external).LastWriteTimeUtc);
            await store.DeleteAsync(definition, CancellationToken.None);
            Assert.True(File.Exists(external));
            foreach (var line in logs) output.WriteLine(line);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private static async Task<List<StreamingTranscriptEvent>> StreamAsync(ITranscriptionEngineRole engine, short[] samples)
    {
        await using var stream = await engine.StartStreamingAsync("en", CancellationToken.None);
        var updates = new List<StreamingTranscriptEvent>();
        stream.TranscriptReceived += updates.Add;
        var pcm = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 2), samples[index]);
        for (var offset = 0; offset < pcm.Length; offset += 6400)
            await stream.SendAudioAsync(pcm.AsMemory(offset, Math.Min(6400, pcm.Length - offset)), CancellationToken.None);
        await stream.FinalizeAsync(CancellationToken.None);
        return updates;
    }

    private static long ReadComputeCounters(int pid)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles($"/proc/{pid}/fdinfo"))
        {
            try
            {
                foreach (var line in File.ReadLines(file))
                    if (line.StartsWith("drm-engine-compute:", StringComparison.Ordinal)
                        && long.TryParse(line.Split(':', 2)[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture, out var value))
                        total = Math.Max(total, value);
            }
            catch (IOException) { /* A native backend may close an fd while its snapshot is read. */ }
        }
        return total;
    }

    private static string RepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "TypeWhisper.slnx"))) return current.FullName;
        throw new DirectoryNotFoundException("Run from the repository checkout.");
    }
}

internal sealed class TranscribeLiveFactAttribute : FactAttribute
{
    public TranscribeLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TYPEWHISPER_TRANSCRIBE_LIVE_MODEL") is null)
            Skip = "Set TYPEWHISPER_TRANSCRIBE_LIVE_MODEL to an approved pinned GGUF for an isolated real worker check.";
    }
}
