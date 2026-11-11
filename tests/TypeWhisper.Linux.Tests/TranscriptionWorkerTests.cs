using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.Processes;
using TypeWhisper.ProcessTestChild;
using TypeWhisper.TranscriptionWorkerProbe;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Starts real workers through the app's own executable, as a package does, with a probe engine
///     that crashes, hangs or fails on request.
/// </summary>
public sealed class TranscriptionWorkerTests : IAsyncLifetime
{
    private static readonly byte[] s_audio = [1, 2, 3, 4];

    // Short to stay under the 108-byte Unix socket path limit.
    private readonly string _root = Path.Join(Path.GetTempPath(), "tww-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _logs = [];
    private readonly ProcessRunner _runner = new();
    private readonly List<IsolatedTranscriptionEngine> _engines = [];
    private WorkerProbePlugin _inner = null!;

    private string PackageDirectory => Path.Join(_root, "package");
    private string PluginDataRoot => Path.Join(_root, "data");
    private string SocketDirectory => Path.Join(_root, "s");
    private string SettingsPath => Path.Join(PluginDataRoot, WorkerProbePlugin.Id, "settings.json");

    private static string AppExecutable => Path.Join(AppContext.BaseDirectory, "typewhisper");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(PackageDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
        Directory.CreateDirectory(
            SocketDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );
#pragma warning restore CA1416
        var probeAssembly = typeof(WorkerProbePlugin).Assembly.Location;
        File.Copy(probeAssembly, Path.Join(PackageDirectory, Path.GetFileName(probeAssembly)));
        File.WriteAllText(
            Path.Join(PackageDirectory, PluginManifest.FileName),
            JsonSerializer.Serialize(new PluginManifest
            {
                Id = WorkerProbePlugin.Id,
                Name = "Worker probe",
                Version = "1.0.0",
                IsLocal = true,
                AssemblyName = Path.GetFileName(probeAssembly),
                PluginClass = typeof(WorkerProbePlugin).FullName!,
            })
        );
        // The host-side instance only provides metadata and the selection; it never transcribes.
        _inner = new WorkerProbePlugin();
        _inner.SelectModel("small");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var engine in _engines)
        {
            await engine.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private TranscriptionWorkerLaunch Launch(ProcessCommand? executable = null)
    {
        return new TranscriptionWorkerLaunch(
            executable ?? new ProcessCommand(AppExecutable, [TranscriptionWorkerServer.Argument]),
            SocketDirectory,
            PackageDirectory,
            PluginDataRoot,
            Path.Join(_root, "assets"),
            WorkerProbePlugin.Id
        );
    }

    private IsolatedTranscriptionEngine Engine(TranscriptionWorkerLaunch? launch = null)
    {
        var engine = IsolatedTranscriptionEngine.Create(_inner, _runner, launch ?? Launch(), Log);
        _engines.Add(engine);
        return engine;
    }

    private void Log(PluginLogLevel level, string message)
    {
        lock (_logs)
        {
            _logs.Add(message);
        }
    }

    private string[] Logs()
    {
        lock (_logs)
        {
            return [.. _logs];
        }
    }

    private static Dictionary<string, string> Fields(PluginTranscriptionResult result)
    {
        return result.Text.Split(';').Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part[1]);
    }

    private static bool IsRunning(int processId)
    {
        // A zombie still has a /proc entry; only a live process counts.
        var status = Path.Join("/proc", processId.ToString(CultureInfo.InvariantCulture), "stat");
        try
        {
            var stat = File.ReadAllText(status);
            return stat[(stat.LastIndexOf(')') + 2)..][0] != 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task WaitUntilGoneAsync(int processId)
    {
        var stopwatch = Stopwatch.StartNew();
        while (IsRunning(processId))
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Process {processId} is still running.");
            await Task.Delay(50);
        }
    }

    private async Task<IsolatedTranscriptionEngine> HardwareEngineAsync(TranscriptionWorkerLaunch? launch = null)
    {
        _inner.EnableHardwareSelection = true;
        await File.WriteAllTextAsync(SettingsPath, """{ "probe-hardware": "enabled" }""");
        return Engine(launch);
    }

    private static void ConfigureGpu(IsolatedTranscriptionEngine engine, TranscriptionAccelerationPreference requested)
    {
        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        Assert.IsAssignableFrom<ITranscriptionHardwareSelection>(engine.HardwareSelection).ConfigureHardware(
            new TranscriptionHardwareRequest(requested,
                new TranscriptionDevice(TranscriptionAccelerationBackend.NvidiaCuda, "pci:0000:01:00.0",
                    "GPU", TranscriptionDeviceKind.DiscreteGpu)));
    }

    [Fact]
    public async Task RawModelTestUsesDisposableExecutionWithoutChangingTheLiveModelOrSettings()
    {
        var engine = await HardwareEngineAsync();
        ConfigureGpu(engine, TranscriptionAccelerationPreference.Auto);
        await engine.LoadModelAsync("small", CancellationToken.None);
        var livePid = engine.WorkerProcessId;
        var before = await File.ReadAllBytesAsync(SettingsPath);
        var request = new TranscriptionHardwareRequest(TranscriptionAccelerationPreference.Cpu,
            TranscriptionHardwareSelector.CpuDevice);

        var result = await engine.TestModelAsync("large", s_audio, "en",
            TranscriptionAccelerationPreference.Cpu, request, CancellationToken.None);

        var fields = Fields(result.Transcription);
        var testPid = int.Parse(fields["pid"], CultureInfo.InvariantCulture);
        Assert.Equal("large", fields["model"]);
        Assert.Equal("large", fields["selected"]);
        Assert.Equal("False", fields["translate"]);
        Assert.Equal("", fields["prompt"]);
        Assert.Equal("cpu", result.Acceleration.ActiveDevice?.StableId);
        Assert.False(IsRunning(testPid));
        Assert.Equal(livePid, engine.WorkerProcessId);
        Assert.Equal("small", engine.SelectedModelId);
        Assert.Equal(TranscriptionAccelerationPreference.NvidiaCuda, engine.AccelerationPreference);
        Assert.Equal(before, await File.ReadAllBytesAsync(SettingsPath));
        Assert.Equal("small", Fields(await engine.TranscribeAsync(s_audio, "en", false, null, CancellationToken.None))["model"]);
    }

    [Fact]
    public async Task RawModelTestReusesSameLoadedModelWithoutTakingASecondFileLease()
    {
        var engine = await HardwareEngineAsync();
        ConfigureGpu(engine, TranscriptionAccelerationPreference.Auto);
        await engine.LoadModelAsync("small", CancellationToken.None);
        var pid = engine.WorkerProcessId;
        var hardware = new TranscriptionHardwareRequest(TranscriptionAccelerationPreference.Auto,
            new(TranscriptionAccelerationBackend.NvidiaCuda, "pci:0000:01:00.0", "Same GPU", TranscriptionDeviceKind.DiscreteGpu));
        var result = await engine.TestModelAsync("small", s_audio, "en",
            TranscriptionAccelerationPreference.NvidiaCuda, hardware, CancellationToken.None);
        Assert.Equal(pid, int.Parse(Fields(result.Transcription)["pid"], CultureInfo.InvariantCulture));
        Assert.Equal(pid, engine.WorkerProcessId);
        Assert.Equal("small", engine.SelectedModelId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.TestModelAsync("small", s_audio, "en",
            TranscriptionAccelerationPreference.Cpu, new(TranscriptionAccelerationPreference.Cpu,
                TranscriptionHardwareSelector.CpuDevice), CancellationToken.None));
        Assert.Equal(pid, engine.WorkerProcessId);
    }

    [Fact]
    public async Task RawModelTestAutoFallbackIsReportedWithoutChangingTheLiveGpu()
    {
        var engine = await HardwareEngineAsync();
        ConfigureGpu(engine, TranscriptionAccelerationPreference.Auto);
        await engine.LoadModelAsync("small", CancellationToken.None);
        var pid = engine.WorkerProcessId;
        var request = new TranscriptionHardwareRequest(TranscriptionAccelerationPreference.Auto,
            new(TranscriptionAccelerationBackend.NvidiaCuda, "pci:0000:01:00.0", "GPU", TranscriptionDeviceKind.DiscreteGpu));
        var result = await engine.TestModelAsync("large", s_audio, "fail-on-gpu",
            TranscriptionAccelerationPreference.NvidiaCuda, request, CancellationToken.None);
        Assert.Equal("cpu", result.Acceleration.ActiveDevice?.StableId);
        Assert.NotNull(result.Acceleration.FallbackReason);
        Assert.Equal(pid, engine.WorkerProcessId);
        Assert.False(engine.UsesCpuFallback);
        Assert.Equal(TranscriptionAccelerationBackend.NvidiaCuda, engine.AccelerationStatus.ActiveBackend);
    }

    [Fact]
    public async Task RawModelTestFailureAndCancellationLeaveLiveExecutionUsable()
    {
        var engine = Engine();
        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        await engine.LoadModelAsync("small", CancellationToken.None);
        var pid = engine.WorkerProcessId;
        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.TestModelAsync("broken", s_audio, "en",
            TranscriptionAccelerationPreference.Cpu, null, CancellationToken.None));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TestModelAsync("large", s_audio, "slow",
            TranscriptionAccelerationPreference.Cpu, null, cancel.Token));
        Assert.Equal(pid, engine.WorkerProcessId);
        Assert.Equal("small", engine.SelectedModelId);
        Assert.Equal("small", Fields(await engine.TranscribeAsync(s_audio, "en", false, null, CancellationToken.None))["model"]);
    }

    [Fact]
    public async Task HardwareDiscoveryUsesADisposableWorkerWithoutReplacingTheLoadedWorker()
    {
        var engine = await HardwareEngineAsync();
        var hardware = Assert.IsAssignableFrom<ITranscriptionHardwareSelection>(engine.HardwareSelection);
        var devices = await hardware.DiscoverDevicesAsync("small", CancellationToken.None);
        var device = Assert.Single(devices, item => item.Backend == TranscriptionAccelerationBackend.NvidiaCuda);
        var discoveryPid = int.Parse(device.DisplayName["GPU in ".Length..], CultureInfo.InvariantCulture);
        Assert.NotEqual(Environment.ProcessId, discoveryPid);
        Assert.False(IsRunning(discoveryPid));
        Assert.Null(engine.WorkerProcessId);
        Assert.False(engine.AccelerationStatus.IsModelLoaded);

        ConfigureGpu(engine, TranscriptionAccelerationPreference.Auto);
        var result = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        var workerPid = engine.WorkerProcessId;
        Assert.Equal("Auto", Fields(result)["requested"]);
        Assert.Equal("pci:0000:01:00.0", Fields(result)["device"]);
        await hardware.DiscoverDevicesAsync("small", CancellationToken.None);
        Assert.Equal(workerPid, engine.WorkerProcessId);
        Assert.True(engine.AccelerationStatus.IsModelLoaded);
    }

    [Theory]
    [InlineData("crash-on-gpu")]
    [InlineData("fail-on-gpu")]
    public async Task HardwareAutoRetriesRetainedAudioOnceOnCpuInAFreshWorker(string failure)
    {
        var engine = await HardwareEngineAsync();
        ConfigureGpu(engine, TranscriptionAccelerationPreference.Auto);
        await engine.LoadModelAsync("small", CancellationToken.None);
        var gpuPid = engine.WorkerProcessId!.Value;

        var result = await engine.TranscribeAsync(s_audio, failure, false, "keep prompt", CancellationToken.None);

        Assert.Equal("Cpu", Fields(result)["preference"]);
        Assert.Equal("Auto", Fields(result)["requested"]);
        Assert.Equal("cpu", Fields(result)["device"]);
        Assert.Equal("4", Fields(result)["length"]);
        Assert.Equal("keep prompt", Fields(result)["prompt"]);
        Assert.NotEqual(gpuPid, engine.WorkerProcessId);
        Assert.False(IsRunning(gpuPid));
        Assert.True(engine.UsesCpuFallback);
        Assert.True(engine.AccelerationStatus.IsModelLoaded);
        Assert.Equal("cpu", engine.AccelerationStatus.ActiveDevice?.StableId);
        Assert.NotNull(engine.AccelerationStatus.FallbackReason);
        Assert.Single(Logs(), line => line.Contains("retrying once on CPU", StringComparison.Ordinal));

        await engine.UnloadModelAsync();
        Assert.False(engine.AccelerationStatus.IsModelLoaded);
        Assert.Null(engine.AccelerationStatus.ActiveDevice);
    }

    [Fact]
    public async Task HardwareAutoRetriesGpuLoadErrorOnCpuButKeepsTheCause()
    {
        var engine = await HardwareEngineAsync();
        await File.WriteAllTextAsync(SettingsPath,
            """{ "probe-hardware": "enabled", "probe-gpu-load-error": "enabled" }""");
        ConfigureGpu(engine, TranscriptionAccelerationPreference.Auto);

        await engine.LoadModelAsync("small", CancellationToken.None);

        Assert.True(engine.AccelerationStatus.IsModelLoaded);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, engine.AccelerationStatus.ActiveBackend);
        Assert.Contains("out of memory", engine.AccelerationStatus.FallbackReason, StringComparison.Ordinal);
        Assert.Equal(TranscriptionAccelerationPreference.NvidiaCuda, engine.AccelerationPreference);
    }

    [Theory]
    [InlineData("crash-on-gpu")]
    [InlineData("fail-on-gpu")]
    public async Task HardwareExplicitGpuNeverSilentlyRetriesOnCpu(string failure)
    {
        var engine = await HardwareEngineAsync();
        ConfigureGpu(engine, TranscriptionAccelerationPreference.NvidiaCuda);
        await engine.LoadModelAsync("small", CancellationToken.None);
        var gpuPid = engine.WorkerProcessId!.Value;

        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            engine.TranscribeAsync(s_audio, failure, false, null, CancellationToken.None));

        Assert.Contains("CPU", error.Message, StringComparison.Ordinal);
        Assert.False(engine.UsesCpuFallback);
        Assert.Null(engine.WorkerProcessId);
        Assert.False(IsRunning(gpuPid));
        Assert.False(engine.AccelerationStatus.IsModelLoaded);
        Assert.Null(engine.AccelerationStatus.ActiveDevice);
        Assert.DoesNotContain(Logs(), line => line.Contains("retrying once on CPU", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HardwareAutoDoesNotRetryCallerCancellationOrUnsupportedRequests()
    {
        var engine = await HardwareEngineAsync();
        ConfigureGpu(engine, TranscriptionAccelerationPreference.Auto);
        await engine.LoadModelAsync("small", CancellationToken.None);
        var gpuPid = engine.WorkerProcessId;
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            engine.TranscribeAsync(s_audio, "fail", false, null, CancellationToken.None));
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.TranscribeAsync(s_audio, "slow", false, null, canceled.Token));
        Assert.False(engine.UsesCpuFallback);
        Assert.Equal(gpuPid, engine.WorkerProcessId);
    }

    [Fact]
    public async Task HardwareAutoStopsAfterOneFailedCpuRetry()
    {
        var engine = await HardwareEngineAsync();
        ConfigureGpu(engine, TranscriptionAccelerationPreference.Auto);
        await Assert.ThrowsAsync<TranscriptionWorkerFaultedException>(() =>
            engine.TranscribeAsync(s_audio, "crash", false, null, CancellationToken.None));
        var attempts = await File.ReadAllLinesAsync(Path.Join(PluginDataRoot, WorkerProbePlugin.Id, "hardware-attempts"));
        Assert.Equal(2, attempts.Length);
        Assert.EndsWith(";NvidiaCuda;4", attempts[0], StringComparison.Ordinal);
        Assert.EndsWith(";Cpu;4", attempts[1], StringComparison.Ordinal);
        Assert.NotEqual(attempts[0].Split(';')[0], attempts[1].Split(';')[0]);
        Assert.Null(engine.WorkerProcessId);
        Assert.False(engine.AccelerationStatus.IsModelLoaded);
        Assert.NotNull(engine.AccelerationStatus.FallbackReason);
    }

    [Fact]
    public async Task HardwareExplicitCpuDoesNotRetryACrash()
    {
        var engine = await HardwareEngineAsync();
        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        engine.HardwareSelection!.ConfigureHardware(new TranscriptionHardwareRequest(
            TranscriptionAccelerationPreference.Cpu, TranscriptionHardwareSelector.CpuDevice));
        await Assert.ThrowsAsync<TranscriptionWorkerFaultedException>(() =>
            engine.TranscribeAsync(s_audio, "crash", false, null, CancellationToken.None));
        var attempts = await File.ReadAllLinesAsync(Path.Join(PluginDataRoot, WorkerProbePlugin.Id, "hardware-attempts"));
        Assert.EndsWith(";Cpu;4", Assert.Single(attempts), StringComparison.Ordinal);
        Assert.False(engine.UsesCpuFallback);
        Assert.False(engine.AccelerationStatus.IsModelLoaded);
    }

    [Fact]
    public async Task HardwareAutoDoesNotRetryMissingWeights()
    {
        var engine = await HardwareEngineAsync();
        ConfigureGpu(engine, TranscriptionAccelerationPreference.Auto);
        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.LoadModelAsync("broken", CancellationToken.None));
        Assert.False(engine.UsesCpuFallback);
        Assert.Null(engine.AccelerationStatus.FallbackReason);
    }

    [Fact]
    public async Task HardwareChoiceReconfigurationClearsFallbackAndRestartsTheWorker()
    {
        var engine = await HardwareEngineAsync();
        ConfigureGpu(engine, TranscriptionAccelerationPreference.Auto);
        await engine.TranscribeAsync(s_audio, "fail-on-gpu", false, null, CancellationToken.None);
        var cpuPid = engine.WorkerProcessId!.Value;
        ConfigureGpu(engine, TranscriptionAccelerationPreference.NvidiaCuda);
        Assert.False(engine.AccelerationStatus.IsModelLoaded);
        Assert.Null(engine.AccelerationStatus.FallbackReason);
        var result = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        Assert.Equal("NvidiaCuda", Fields(result)["preference"]);
        Assert.Equal("NvidiaCuda", Fields(result)["requested"]);
        Assert.False(engine.UsesCpuFallback);
        Assert.NotEqual(cpuPid, engine.WorkerProcessId);
        Assert.False(IsRunning(cpuPid));
    }

    [Fact]
    public async Task HardwareDiscoveryCancellationLeavesLoadedWorkerUsable()
    {
        var engine = await HardwareEngineAsync();
        ConfigureGpu(engine, TranscriptionAccelerationPreference.Auto);
        await engine.LoadModelAsync("small", CancellationToken.None);
        var gpuPid = engine.WorkerProcessId;
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var hardware = Assert.IsAssignableFrom<ITranscriptionHardwareSelection>(engine.HardwareSelection);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            hardware.DiscoverDevicesAsync("slow-discovery", canceled.Token));
        Assert.Equal(gpuPid, engine.WorkerProcessId);
        Assert.False(engine.UsesCpuFallback);
        await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
    }

    [Fact]
    public async Task HardwareDiscoveryTimeoutDoesNotBecomeCallerCancellationOrFallback()
    {
        var engine = await HardwareEngineAsync(Launch() with { RequestTimeout = TimeSpan.FromMilliseconds(500) });
        ConfigureGpu(engine, TranscriptionAccelerationPreference.Auto);
        await engine.LoadModelAsync("small", CancellationToken.None);
        var gpuPid = engine.WorkerProcessId;
        await Assert.ThrowsAsync<TranscriptionWorkerRequestTimeoutException>(() =>
            engine.HardwareSelection!.DiscoverDevicesAsync("slow-discovery", CancellationToken.None));
        Assert.False(engine.UsesCpuFallback);
        Assert.Equal(gpuPid, engine.WorkerProcessId);
        await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
    }

    [Fact]
    public async Task HardwareConfigurationRejectsWorkersWithoutTheNegotiatedCapability()
    {
        var engine = await HardwareEngineAsync(Launch(FakeWorker("answer")));
        ConfigureGpu(engine, TranscriptionAccelerationPreference.Auto);

        await Assert.ThrowsAsync<NotSupportedException>(() => engine.LoadModelAsync("small", CancellationToken.None));
        Assert.False(engine.UsesCpuFallback);
        Assert.Null(engine.WorkerProcessId);
    }

    [Fact]
    public async Task HardwareWorkerRejectsLoadingAfterDiscoveryAndRepeatedConfiguration()
    {
        await HardwareEngineAsync();
        var hardware = new TranscriptionHardwareRequest(TranscriptionAccelerationPreference.Cpu,
            TranscriptionHardwareSelector.CpuDevice);
        await using var probe = await TranscriptionWorkerProcess.StartAsync(_runner, Launch(),
            TranscriptionAccelerationPreference.Cpu, null, Log, CancellationToken.None);
        Assert.Contains(TranscriptionWorkerCapabilities.HardwareSelection, probe.Capabilities);
        var discovered = await probe.SendAsync(new TranscriptionWorkerMessage
        {
            Command = TranscriptionWorkerCommands.DiscoverDevices, ModelId = "small",
        }, default, CancellationToken.None);
        Assert.NotEmpty(discovered.Devices!);
        var configureAfterDiscovery = await probe.SendAsync(new TranscriptionWorkerMessage
        {
            Command = TranscriptionWorkerCommands.ConfigureHardware, HardwareRequest = hardware,
        }, default, CancellationToken.None);
        Assert.NotNull(configureAfterDiscovery.Error);
        var loadAfterDiscovery = await probe.SendAsync(new TranscriptionWorkerMessage
        {
            Command = TranscriptionWorkerCommands.Load, ModelId = "small",
        }, default, CancellationToken.None);
        Assert.NotNull(loadAfterDiscovery.Error);
        Assert.Null(loadAfterDiscovery.State?.LoadedModelId);

        await using var worker = await TranscriptionWorkerProcess.StartAsync(_runner, Launch(),
            TranscriptionAccelerationPreference.Cpu, null, Log, CancellationToken.None);
        var request = new TranscriptionWorkerMessage
        {
            Command = TranscriptionWorkerCommands.ConfigureHardware, HardwareRequest = hardware,
        };
        Assert.Null((await worker.SendAsync(request, default, CancellationToken.None)).Error);
        Assert.NotNull((await worker.SendAsync(request, default, CancellationToken.None)).Error);
    }

    [Fact]
    public void LegacyProvidersDoNotExposeTheNewHardwareCapability()
    {
        Assert.Null(Engine().HardwareSelection);
    }

    [Fact]
    public async Task TranscribesInAWorkerWithTheHostSelectionAndFullResult()
    {
        var engine = Engine();

        var result = await engine.TranscribeAsync(s_audio, "en", true, "terms", CancellationToken.None);

        var fields = Fields(result);
        Assert.NotEqual(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), fields["pid"]);
        Assert.Equal(engine.WorkerProcessId?.ToString(CultureInfo.InvariantCulture), fields["pid"]);
        Assert.Equal("small", fields["model"]);
        Assert.Equal("small", fields["selected"]);
        Assert.Equal("4", fields["length"]);
        Assert.Equal("True", fields["translate"]);
        Assert.Equal("terms", fields["prompt"]);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(1.5, result.DurationSeconds);
        Assert.Equal(0.25f, result.NoSpeechProbability);
        Assert.Equal(new VocabularyTokenTiming("probe", 0.25, 0.5), Assert.Single(result.TokenTimings));
        Assert.Equal(
            new PluginTranscriptionSegment("probe", 0, 1) { NoSpeechProbability = 0.25f },
            Assert.Single(result.Segments)
        );
        // Logs travel separately from answers and may arrive after them.
        await WaitForLogAsync("activated in " + fields["pid"]);
        await WaitForLogAsync("loaded small in " + fields["pid"]);

        var hinted = await engine.TranscribeWithLanguageHintsAsync(s_audio, ["de", "en"], false, null, CancellationToken.None);
        Assert.Equal("hints:de,en", Fields(hinted)["language"]);
        var streamed = await engine.TranscribeStreamingWithLanguageHintsAsync(
            s_audio, ["de", "en"], false, null, _ => true, CancellationToken.None);
        Assert.Equal("hints:de,en", Fields(streamed)["language"]);
        Assert.Equal(fields["pid"], Fields(hinted)["pid"]);
    }

    [Fact]
    public async Task TheWorkerReadsSettingsOnceAndNeverWritesThem()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "probe-setting": "from-host", "secret:token": "sealed" }""");
        var before = await File.ReadAllBytesAsync(SettingsPath);
        var engine = Engine();

        var first = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        var second = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);

        Assert.Equal("from-host", Fields(first)["setting"]);
        // The worker's own write stays in its memory.
        Assert.Equal("written-by-worker", Fields(second)["setting"]);
        Assert.Equal(before, await File.ReadAllBytesAsync(SettingsPath));
    }

    [Fact]
    public async Task EngineErrorsKeepTheirKindAndTheWorker()
    {
        var engine = Engine();
        await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        var processId = engine.WorkerProcessId;

        var refused = await Assert.ThrowsAsync<NotSupportedException>(() =>
            engine.TranscribeAsync(s_audio, "fail", false, null, CancellationToken.None));
        Assert.Equal("The probe refused this audio.", refused.Message);

        _inner.SelectModel("broken");
        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));
        Assert.Equal(processId, engine.WorkerProcessId);
    }

    [Fact]
    public async Task ActivationFailureIsThePluginsErrorNotACrash()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "probe-fail-activation": "refuse" }""");
        var engine = Engine();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.LoadModelAsync("small", CancellationToken.None));

        Assert.Equal("The probe refused to activate.", error.Message);
        Assert.Null(engine.WorkerProcessId);

        // An I/O kind must not be mistaken for the connection failing while the worker starts.
        await File.WriteAllTextAsync(SettingsPath, """{ "probe-fail-activation": "missing" }""");
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => engine.LoadModelAsync("small", CancellationToken.None));
        Assert.Equal("The probe runtime is missing.", missing.Message);
    }

    [Fact]
    public async Task ANativeCrashEndsOnlyTheWorkerAndTheRequestIsRetriedInANewOne()
    {
        var engine = Engine();
        await engine.LoadModelAsync("small", CancellationToken.None);
        var first = engine.WorkerProcessId!.Value;

        var result = await engine.TranscribeAsync(s_audio, "crash-once", false, null, CancellationToken.None);

        Assert.NotEqual(first.ToString(CultureInfo.InvariantCulture), Fields(result)["pid"]);
        Assert.Equal("small", Fields(result)["model"]);
        Assert.False(IsRunning(first));
        Assert.Contains(Logs(), line => line.Contains("1 in a row", StringComparison.Ordinal)
                                        && line.Contains("exit code 139", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatedCrashesPauseTheEngineUntilTheModelIsLoadedAgain()
    {
        var engine = Engine();
        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);

        var paused = await Assert.ThrowsAsync<TranscriptionWorkerFaultedException>(() =>
            engine.TranscribeAsync(s_audio, "crash", false, null, CancellationToken.None));

        Assert.Contains("paused it briefly", paused.Message, StringComparison.Ordinal);
        var crash = Assert.IsType<TranscriptionWorkerCrashedException>(paused.InnerException);
        Assert.Equal(128 + 11, crash.ExitCode); // SIGSEGV
        Assert.Equal(
            IsolatedTranscriptionEngine.MaximumAttempts,
            Logs().Count(line => line.Contains("worker crashed", StringComparison.Ordinal))
        );
        // Paused: no worker starts, even for audio that would work.
        await Assert.ThrowsAsync<TranscriptionWorkerFaultedException>(() =>
            engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));
        Assert.Null(engine.WorkerProcessId);

        await engine.LoadModelAsync("small", CancellationToken.None);
        var result = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        Assert.Equal("small", Fields(result)["model"]);
    }

    [Fact]
    public async Task RepeatedCrashesOnTheGraphicsCardSwitchTheEngineToTheCpu()
    {
        var changes = 0;
        var engine = IsolatedTranscriptionEngine.Create(_inner, _runner, Launch(), Log, () => Interlocked.Increment(ref changes));
        _engines.Add(engine);
        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        var result = await engine.TranscribeAsync(s_audio, "crash-on-gpu", false, null, CancellationToken.None);

        Assert.Equal("Cpu", Fields(result)["preference"]);
        Assert.True(engine.UsesCpuFallback);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, engine.AccelerationStatus.ActiveBackend);
        Assert.Contains("switched it to the CPU", engine.AccelerationStatus.Detail, StringComparison.Ordinal);
        Assert.Equal(1, changes);
        // Loading the same choice again keeps the fallback; a new choice tries the card again.
        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        Assert.True(engine.UsesCpuFallback);
        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        Assert.False(engine.UsesCpuFallback);
        var retried = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        Assert.Equal("NvidiaCuda", Fields(retried)["preference"]);
    }

    [Fact]
    public async Task AHungCallWithAWorkingHeartbeatHitsTheTotalDeadline()
    {
        var engine = Engine(Launch() with
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(100), RequestTimeout = TimeSpan.FromSeconds(2),
        });
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId!.Value;
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TranscriptionWorkerRequestTimeoutException>(() =>
            engine.TranscribeAsync(s_audio, "hang", false, null, CancellationToken.None));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15));
        await WaitUntilGoneAsync(processId);
        Assert.Single(Logs(), line => line.Contains("exceeded the transcribe deadline", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFrozenWorkerIsEndedAfterTheInactivityWindow()
    {
        var engine = Engine(Launch() with
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(200),
            RequestInactivityTimeout = TimeSpan.FromSeconds(2),
            RequestTimeout = Timeout.InfiniteTimeSpan,
        });
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId!.Value;

        await Assert.ThrowsAsync<TranscriptionWorkerUnresponsiveException>(() =>
            engine.TranscribeAsync(s_audio, "freeze", false, null, CancellationToken.None));

        await WaitUntilGoneAsync(processId);
        Assert.Null(engine.WorkerProcessId);
    }

    [Fact]
    public async Task CancellingAHungRequestEndsTheWorkerWithinTheGrace()
    {
        var engine = Engine(Launch() with { CancellationGrace = TimeSpan.FromMilliseconds(500) });
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId!.Value;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.TranscribeAsync(s_audio, "hang", false, null, cancellation.Token));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        await WaitUntilGoneAsync(processId);
    }

    [Fact]
    public async Task ACancellationTheEngineHonoursKeepsTheWorker()
    {
        var engine = Engine();
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.TranscribeAsync(s_audio, "slow", false, null, cancellation.Token));

        Assert.Equal(processId, engine.WorkerProcessId);
        var result = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        Assert.Equal(processId?.ToString(CultureInfo.InvariantCulture), Fields(result)["pid"]);
    }

    [Fact]
    public async Task UnloadingStopsARunningRequestAndTheWorker()
    {
        var engine = Engine(Launch() with { CancellationGrace = TimeSpan.FromMilliseconds(500) });
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId!.Value;
        var hung = engine.TranscribeAsync(s_audio, "hang", false, null, CancellationToken.None);
        await Task.Delay(300);

        await engine.UnloadModelAsync();

        var stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => hung);
        Assert.Contains("stopped before it finished", stopped.Message, StringComparison.Ordinal);
        await WaitUntilGoneAsync(processId);
        Assert.Null(engine.WorkerProcessId);
        var result = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        Assert.Equal("small", Fields(result)["model"]);
    }

    [Fact]
    public async Task UnloadingAlsoStopsRequestsQueuedBehindTheRunningOne()
    {
        var engine = Engine(Launch() with { CancellationGrace = TimeSpan.FromMilliseconds(500) });
        await engine.LoadModelAsync("small", CancellationToken.None);
        var running = engine.TranscribeAsync(s_audio, "hang", false, null, CancellationToken.None);
        await Task.Delay(300);
        var queued = engine.TranscribeAsync(s_audio, "hang", false, null, CancellationToken.None);
        var elapsed = Stopwatch.StartNew();

        await engine.UnloadModelAsync();

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => running);
        var stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        Assert.Contains("stopped before it finished", stopped.Message, StringComparison.Ordinal);
        Assert.Null(engine.WorkerProcessId);
    }

    [Fact]
    public void LanguageSelectionSupportComesFromTheInProcessEngine()
    {
        var inner = new Moq.Mock<ITranscriptionEngineRole>();
        inner.As<ITranscriptionLanguageSelectionCapabilities>()
            .SetupGet(engine => engine.AutomaticDetectionSupport)
            .Returns(LanguageSelectionSupport.Supported);
        inner.As<ITranscriptionLanguageSelectionCapabilities>()
            .SetupGet(engine => engine.ExplicitSelectionSupport)
            .Returns(LanguageSelectionSupport.Unsupported);

        var wrapped = IsolatedTranscriptionEngine.Create(inner.Object, _runner, Launch(), Log);
        var unknown = Engine();

        Assert.Equal(LanguageSelectionSupport.Supported, wrapped.AutomaticDetectionSupport);
        Assert.Equal(LanguageSelectionSupport.Unsupported, wrapped.ExplicitSelectionSupport);
        // The host then drops an explicit language for an auto-detect-only model, as it does in process.
        Assert.True(wrapped.ResolveEffectiveSelection(LanguageSelection.Explicit("de")).IsAutomatic);
        Assert.Equal(LanguageSelectionSupport.Unknown, unknown.ExplicitSelectionSupport);
    }

    [Fact]
    public async Task DeletingAModelOnlyEndsTheWorkerThatLoadedIt()
    {
        var engine = Engine();
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId;

        await engine.DeleteModelAsync("large", CancellationToken.None);
        Assert.Equal(processId, engine.WorkerProcessId);

        await engine.DeleteModelAsync("small", CancellationToken.None);
        Assert.Null(engine.WorkerProcessId);
    }

    [Fact]
    public async Task AnAccelerationChangeStartsAWorkerWithTheNewPreference()
    {
        var engine = Engine();
        var first = Fields(await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));

        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        var second = Fields(await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));

        Assert.Equal("Auto", first["preference"]);
        Assert.Equal("Cpu", second["preference"]);
        Assert.NotEqual(first["pid"], second["pid"]);
        Assert.False(IsRunning(int.Parse(first["pid"], CultureInfo.InvariantCulture)));
        Assert.Equal($"Probe Cpu in {second["pid"]}", engine.AccelerationStatus.DisplayText);
    }

    [Fact]
    public async Task AnExplicitComputeBackendReachesEveryNewWorkerUntilThePreferenceChanges()
    {
        var engine = Engine();
        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);

        await engine.ConfigureComputeBackendAsync("cuda");
        var configured = Fields(await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));
        // The same preference again still supersedes the explicit backend, so the worker is replaced.
        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        var superseded = Fields(await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));

        Assert.Equal("cuda", configured["backend"]);
        Assert.Equal("", superseded["backend"]);
        Assert.NotEqual(configured["pid"], superseded["pid"]);
    }

    [Fact]
    public async Task AWorkerThatStopsReadingAudioIsEndedAfterTheInactivityWindow()
    {
        var engine = Engine(Launch() with
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(200),
            RequestInactivityTimeout = TimeSpan.FromSeconds(2),
            RequestTimeout = Timeout.InfiniteTimeSpan,
        });
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId!.Value;
        await _runner.RunOneShotAsync(
            new ProcessCommand("kill", ["-STOP", processId.ToString(CultureInfo.InvariantCulture)]),
            new ProcessOneShotOptions(TimeSpan.FromSeconds(10))
        );

        // Far more than a socket buffer holds, so the upload itself blocks.
        await Assert.ThrowsAsync<TranscriptionWorkerUnresponsiveException>(() =>
            engine.TranscribeAsync(new byte[32 << 20], null, false, null, CancellationToken.None));

        await WaitUntilGoneAsync(processId);
        Assert.Contains(Logs(), line => line.Contains("did not accept the transcribe request's audio", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EndingAWorkerAlsoEndsTheProcessesItStarted()
    {
        var engine = Engine(Launch() with { CancellationGrace = TimeSpan.FromMilliseconds(500) });
        await engine.LoadModelAsync("small", CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.TranscribeAsync(s_audio, "spawn-and-hang", false, null, cancellation.Token));

        await WaitUntilGoneAsync(await WaitForSpawnedAsync());
    }

    [Fact]
    public async Task DisposingEndsTheWorkerAndRefusesLaterRequests()
    {
        var engine = Engine();
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId!.Value;

        await engine.DisposeAsync();

        await WaitUntilGoneAsync(processId);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));
    }

    [Fact]
    public async Task AWorkerThatExitsBeforeConnectingIsReportedWithItsExitCode()
    {
        var engine = Engine(Launch(FakeWorker("exit-before-connect")));

        var faulted = await Assert.ThrowsAsync<TranscriptionWorkerFaultedException>(() =>
            engine.LoadModelAsync("small", CancellationToken.None));

        Assert.Equal(7, Assert.IsType<TranscriptionWorkerCrashedException>(faulted.InnerException).ExitCode);
    }

    [Fact]
    public async Task AWorkerThatHangsWhileStartingIsEndedAndNotRetried()
    {
        // The worker arguments become ignored positional parameters of the shell.
        var engine = Engine(Launch(new ProcessCommand("sh", ["-c", "sleep 30", "sh"])) with { StartupTimeout = TimeSpan.FromSeconds(1) });
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TranscriptionWorkerUnresponsiveException>(() =>
            engine.LoadModelAsync("small", CancellationToken.None));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Single(Logs(), line => line.Contains("worker crashed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASocketDirectoryTooDeepForASocketPathStillWorks()
    {
        var deep = Path.Join(_root, new string('d', 60), new string('e', 60));
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
        Directory.CreateDirectory(deep, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        var engine = Engine(Launch() with { SocketDirectory = deep });

        var result = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);

        Assert.Equal("small", Fields(result)["model"]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(deep));
    }

    [Fact]
    public async Task APeerWithoutTheLaunchTokenIsRejected()
    {
        var engine = Engine(Launch(FakeWorker("wrong-token")));

        await Assert.ThrowsAsync<InvalidDataException>(() => engine.LoadModelAsync("small", CancellationToken.None));
        Assert.Null(engine.WorkerProcessId);
    }

    [Theory]
    [InlineData("oversized-header")]
    [InlineData("garbage-header")]
    [InlineData("payload")]
    public async Task AFrameAWorkerMustNotSendEndsIt(string behavior)
    {
        var engine = Engine(Launch(FakeWorker(behavior)));

        var faulted = await Assert.ThrowsAsync<TranscriptionWorkerFaultedException>(() =>
            engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));

        Assert.IsType<TranscriptionWorkerCrashedException>(faulted.InnerException);
        Assert.Null(engine.WorkerProcessId);
        Assert.Contains(Logs(), line => line.Contains("invalid frame", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAnswerForAnotherRequestIsIgnored()
    {
        var engine = Engine(Launch(FakeWorker("stale-then-answer")));

        var result = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);

        Assert.Equal("fake", result.Text);
    }

    [Fact]
    public async Task AWorkerAndWhatItStartedEndWhenItsHostDisconnectsOrSendsGarbage()
    {
        foreach (var disconnect in new[] { true, false })
        {
            await using var raw = await RawWorker.StartAsync(this, Environment.ProcessId);
            var spawned = await raw.SpawnAndHangAsync();
            if (disconnect)
            {
                raw.Connection.Dispose();
            }
            else
            {
                await raw.Stream.WriteAsync(new byte[] { 255, 255, 255, 127, 0, 0, 0, 0 });
            }

            await raw.Session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilGoneAsync(spawned);
        }
    }

    [Fact]
    public async Task AWorkerAndWhatItStartedEndWhenItsParentExits()
    {
        // The worker's parent is a shell that exits once the worker connected; the worker is then
        // re-parented and must notice on its own while a request hangs and the socket stays open.
        await using var raw = await RawWorker.StartAsync(this, parentProcessId: null);
        var spawned = await raw.SpawnAndHangAsync();

        raw.ReleaseParent();

        await WaitUntilGoneAsync(raw.WorkerProcessId);
        await WaitUntilGoneAsync(spawned);
    }

    [Fact]
    public async Task ACrashedWorkersProcessesEndWithIt()
    {
        var engine = Engine();
        await engine.LoadModelAsync("small", CancellationToken.None);
        var worker = engine.WorkerProcessId!.Value;
        var request = engine.TranscribeAsync(s_audio, "spawn-and-hang", false, null, CancellationToken.None);
        var spawned = await WaitForSpawnedAsync();

        // Killed from outside, the worker leaves no tree for the host to walk.
        Process.GetProcessById(worker).Kill();

        await WaitUntilGoneAsync(spawned);
        // The retry hangs in a new worker; stopping the engine ends it.
        await engine.UnloadModelAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => request);
    }

    [Fact]
    public async Task AnIdleWorkersProcessesEndWhenItCrashes()
    {
        var engine = Engine();
        await engine.TranscribeAsync(s_audio, "spawn", false, null, CancellationToken.None);
        var spawned = await WaitForSpawnedAsync();

        // Nothing calls the engine afterwards; the exit alone must end what the worker started.
        Process.GetProcessById(engine.WorkerProcessId!.Value).Kill();

        await WaitUntilGoneAsync(spawned);
    }

    [Fact]
    public async Task ThePluginManagerRunsSelectedEnginesInWorkersForOneActivation()
    {
        var isolation = new TranscriptionIsolation(
            new ProcessCommand(AppExecutable, [TranscriptionWorkerServer.Argument]),
            new HashSet<string> { WorkerProbePlugin.Id },
            () => SocketDirectory
        );
        using var manager = PluginManager(isolation);
        Assert.True(await manager.LoadPluginFromDirectoryAsync(PackageDirectory, activate: true));

        var engine = Assert.IsType<IsolatedTranscriptionEngine>(Assert.Single(manager.TranscriptionEngines));
        engine.SelectModel("small");
        var result = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        var processId = engine.WorkerProcessId!.Value;
        Assert.Equal(processId.ToString(CultureInfo.InvariantCulture), Fields(result)["pid"]);

        // Callers compare engines by reference, so a rebuild keeps the adapter.
        typeof(PluginManager)
            .GetMethod("RebuildCapabilityIndices", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, null);
        Assert.Same(engine, Assert.Single(manager.TranscriptionEngines));

        // The worker read the settings once; a changed setting gets a worker that reads it again.
        Assert.Equal(
            Fields(await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None))["pid"],
            Fields(result)["pid"]
        );
        var hostServices = (Dictionary<string, PluginHostServices>)typeof(PluginManager)
            .GetField("_hostServices", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;
        var services = hostServices[WorkerProbePlugin.Id];
        services.SetSetting("probe-setting", "changed-by-host");
        var reread = Fields(await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));
        Assert.Equal("changed-by-host", reread["setting"]);
        Assert.NotEqual(Fields(result)["pid"], reread["pid"]);
        processId = engine.WorkerProcessId!.Value;

        await manager.DisablePluginAsync(WorkerProbePlugin.Id);
        await WaitUntilGoneAsync(processId);
        Assert.Empty(manager.TranscriptionEngines);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));

        await manager.EnablePluginAsync(WorkerProbePlugin.Id);
        Assert.NotSame(engine, Assert.IsType<IsolatedTranscriptionEngine>(Assert.Single(manager.TranscriptionEngines)));
    }

    [Fact]
    public async Task EnginesStayInProcessWhenIsolationIsOffOrNotSelected()
    {
        foreach (var isolation in new[]
                 {
                     new TranscriptionIsolation(null, new HashSet<string> { WorkerProbePlugin.Id }, () => SocketDirectory),
                     new TranscriptionIsolation(
                         new ProcessCommand(AppExecutable, [TranscriptionWorkerServer.Argument]),
                         new HashSet<string> { "com.typewhisper.whisper-cpp" },
                         () => SocketDirectory
                     ),
                 })
        {
            using var manager = PluginManager(isolation);
            Assert.True(await manager.LoadPluginFromDirectoryAsync(PackageDirectory, activate: true));

            Assert.IsNotType<IsolatedTranscriptionEngine>(Assert.Single(manager.TranscriptionEngines));
        }
    }

    [Fact]
    public void TheWorkerIsTheAppExecutableOrTheDotnetHostWithTheAppAssembly()
    {
        var packaged = TranscriptionIsolation.ResolveExecutable("/opt/typewhisper/typewhisper", null)!;
        Assert.Equal("/opt/typewhisper/typewhisper", packaged.FileName);
        Assert.Equal([TranscriptionWorkerServer.Argument], packaged.Arguments);

        var development = TranscriptionIsolation.ResolveExecutable("/usr/lib/dotnet/dotnet", "/src/TypeWhisper.Linux.dll")!;
        Assert.Equal("/usr/lib/dotnet/dotnet", development.FileName);
        Assert.Equal(["/src/TypeWhisper.Linux.dll", TranscriptionWorkerServer.Argument], development.Arguments);

        Assert.Null(TranscriptionIsolation.ResolveExecutable("/usr/lib/dotnet/dotnet", null));
        Assert.Null(TranscriptionIsolation.ResolveExecutable(null, "/src/TypeWhisper.Linux.dll"));
        // The test runner hosts the app assembly here, and it has no worker mode.
        Assert.False(TranscriptionIsolation.CreateDefault().IsEnabled);
    }

    private PluginManager PluginManager(TranscriptionIsolation isolation)
    {
        var profiles = new Moq.Mock<IProfileService>();
        profiles.SetupGet(service => service.Profiles).Returns([]);
        return new PluginManager(
            new PluginLoader(PluginDataRoot),
            new PluginEventBus(),
            new Moq.Mock<IActiveWindowService>().Object,
            profiles.Object,
            TestPluginManagerFactory.CreateSettings(new AppSettings()).Object,
            [],
            transcriptionIsolation: isolation
        );
    }

    private async Task WaitForLogAsync(string expected)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!Logs().Contains(expected))
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Log line '{expected}' did not arrive.");
            await Task.Delay(50);
        }
    }

    private async Task<int> WaitForSpawnedAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var spawned = Logs().FirstOrDefault(line => line.StartsWith("spawned ", StringComparison.Ordinal));
            if (spawned is not null)
            {
                return int.Parse(spawned["spawned ".Length..], CultureInfo.InvariantCulture);
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "The probe did not start its process.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task AWorkerStartedForAnotherParentExitsAtOnce()
    {
        var result = await _runner.RunOneShotAsync(
            new ProcessCommand(
                AppExecutable,
                [
                    .. Arguments(Path.Join(SocketDirectory, "unused.sock"), parentProcessId: 1),
                ],
                new Dictionary<string, string> { [TranscriptionWorkerServer.TokenEnvironmentVariable] = "token" }
            ),
            new ProcessOneShotOptions(TimeSpan.FromSeconds(30))
        );

        Assert.Equal(TranscriptionWorkerServer.ExitParentGone, result.ExitCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--socket /tmp/s")]
    [InlineData("--socket /tmp/s --socket /tmp/t")]
    public async Task InvalidArgumentsExitWithAUsageError(string extra)
    {
        var result = await _runner.RunOneShotAsync(
            new ProcessCommand(
                AppExecutable,
                [TranscriptionWorkerServer.Argument, .. extra.Split(' ', StringSplitOptions.RemoveEmptyEntries)]
            ),
            new ProcessOneShotOptions(TimeSpan.FromSeconds(30))
        );

        Assert.Equal(TranscriptionWorkerServer.ExitInvalidArguments, result.ExitCode);
        Assert.Contains("invalid transcription worker arguments", result.StandardErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentsRoundTripAndRejectUnsafeValues()
    {
        var arguments = new TranscriptionWorkerArguments(
            "/run/user/1000/typewhisper/worker.sock",
            42,
            "/opt/plugin",
            "/data",
            "/assets",
            "engine.local",
            TranscriptionAccelerationPreference.Cpu,
            TimeSpan.FromMilliseconds(250)
        );

        Assert.True(TranscriptionWorkerArguments.TryParse(
            [TranscriptionWorkerServer.Argument, .. arguments.ToArguments()],
            out var parsed
        ));
        Assert.Equal(arguments, parsed);
        Assert.False(TranscriptionWorkerArguments.TryParse(
            [TranscriptionWorkerServer.Argument, .. (arguments with { SelectionId = "../x" }).ToArguments()],
            out _
        ));
        Assert.False(TranscriptionWorkerArguments.TryParse(
            [TranscriptionWorkerServer.Argument, .. (arguments with { ParentProcessId = 0 }).ToArguments()],
            out _
        ));
        Assert.False(TranscriptionWorkerArguments.TryParse(
            [TranscriptionWorkerServer.Argument, .. (arguments with { HeartbeatInterval = TimeSpan.Zero }).ToArguments()],
            out _
        ));
        Assert.False(TranscriptionWorkerArguments.TryParse(["--other", .. arguments.ToArguments()], out _));
    }

    [Fact]
    public void RequestBudgetsScaleWithAudioAndKeepLoadingSeparate()
    {
        var launch = Launch();
        var transcribe = new TranscriptionWorkerMessage { Command = TranscriptionWorkerCommands.Transcribe };

        Assert.Equal(TimeSpan.FromSeconds(120), launch.TimeoutFor(transcribe, 0));
        // One minute of 16 kHz PCM16 allows ten minutes more.
        Assert.Equal(TimeSpan.FromSeconds(720), launch.TimeoutFor(transcribe, 60 * 16000 * 2));
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            launch.TimeoutFor(new TranscriptionWorkerMessage { Command = TranscriptionWorkerCommands.Load }, 0)
        );
        Assert.Equal(
            Timeout.InfiniteTimeSpan,
            (launch with { RequestTimeout = Timeout.InfiniteTimeSpan }).TimeoutFor(transcribe, 0)
        );
        Assert.True(launch.TimeoutFor(transcribe, TranscriptionWorkerProtocol.MaximumAudioBytes) > TimeSpan.FromHours(90));
    }

    private async Task<IsolatedTranscriptionEngine> StreamingEngineAsync(TranscriptionWorkerLaunch? launch = null)
    {
        _inner.EnableStreaming = true;
        await File.WriteAllTextAsync(SettingsPath, """{ "probe-streaming": "enabled" }""");
        return Engine(launch);
    }

    [Fact]
    public async Task StreamingReplacesCorrectedPreviewsAndPreservesRepeatedWordsInOneFinal()
    {
        ITranscriptionEngineRole engine = await StreamingEngineAsync();
        Assert.True(engine.SupportsStreaming);
        await using var session = await engine.StartStreamingWithLanguageHintsAndPromptAsync(["en"], "context", CancellationToken.None);
        var events = new List<StreamingTranscriptEvent>();
        session.TranscriptReceived += events.Add;
        await session.SendAudioAsync(s_audio, CancellationToken.None);
        await session.SendAudioAsync(s_audio, CancellationToken.None);
        await session.FinalizeAsync(CancellationToken.None);
        Assert.Equal(["very bad", "very very good", "very very good"], events.Select(update => update.Text));
        Assert.Equal([false, false, true], events.Select(update => update.IsFinal));
        Assert.Equal("en", events[^1].DetectedLanguage);
    }

    [Fact]
    public async Task RealCoordinatorAssemblesOnlyTheFinalWindowAndKeepsCorrectedPreviews()
    {
        var engine = await StreamingEngineAsync();
        var previews = new List<string>();
        await using var coordinator = new StreamingTranscriptionCoordinator(engine, LanguageSelection.Explicit("en"), [],
            1, (_, text) => previews.Add(text), error => throw new InvalidOperationException("Unexpected stream fault.", error));
        await coordinator.StartAsync(CancellationToken.None);
        coordinator.AcceptAudioFrame([0.1f, 0.2f], 16000);
        coordinator.AcceptAudioFrame([0.3f, 0.4f], 16000);
        Assert.Equal("very very good", await coordinator.FinalizeAsync(CancellationToken.None));
        Assert.Equal(["very bad", "very very good", "very very good"], previews);
        Assert.False(coordinator.Faulted);
        Assert.False(coordinator.HasDroppedAudio);
    }

    [Fact]
    public async Task SlowWorkerMakesLiveResultIneligibleAndAllowsCompleteRecordingBatchFallback()
    {
        var engine = await StreamingEngineAsync(Launch() with { CancellationGrace = TimeSpan.FromMilliseconds(150) });
        await File.WriteAllTextAsync(SettingsPath, """{ "probe-streaming": "enabled", "probe-stream-behavior": "slow" }""");
        var coordinator = new StreamingTranscriptionCoordinator(engine, LanguageSelection.Explicit("en"), [],
            1, (_, _) => { }, _ => { }, finalizeSenderTimeout: TimeSpan.FromMilliseconds(100),
            finalizeSessionTimeout: TimeSpan.FromMilliseconds(500));
        await coordinator.StartAsync(CancellationToken.None);
        coordinator.AcceptAudioFrame([0.1f, 0.2f], 16000);
        await Assert.ThrowsAsync<TimeoutException>(() => coordinator.FinalizeAsync(CancellationToken.None));
        await coordinator.DisposeAsync();
        var result = await engine.TranscribeAsync(new byte[32000], "en", false, null, CancellationToken.None);
        Assert.Equal("32000", Fields(result)["length"]);
    }

    [Fact]
    public async Task StreamingCapabilityIsRequiredBeforeSendingAStartToAnOlderWorker()
    {
        var engine = await StreamingEngineAsync(Launch(FakeWorker("normal")));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            ((ITranscriptionEngineRole)engine).StartStreamingAsync("en", CancellationToken.None));
    }

    [Fact]
    public async Task StreamingDoesNotAllowBatchOrModelReplacementUntilSessionEnds()
    {
        var engine = await StreamingEngineAsync();
        await using var session = await ((ITranscriptionEngineRole)engine).StartStreamingAsync("en", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.LoadModelAsync("large", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));
        await session.FinalizeAsync(CancellationToken.None);
        await engine.LoadModelAsync("large", CancellationToken.None);
        var pid = engine.WorkerProcessId;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAudioAsync(s_audio, CancellationToken.None));
        Assert.Equal(pid, engine.WorkerProcessId);
    }

    [Fact]
    public async Task StaleWorkerUpdatesCannotPublishAFinalOrLeaveTheWorkerReusable()
    {
        var engine = await StreamingEngineAsync(Launch(FakeWorker("stale-stream-update")));
        await using var session = await ((ITranscriptionEngineRole)engine).StartStreamingAsync("en", CancellationToken.None);
        var updates = new List<StreamingTranscriptEvent>();
        session.TranscriptReceived += updates.Add;
        await Assert.ThrowsAsync<InvalidDataException>(() => session.SendAudioAsync(s_audio, CancellationToken.None));
        Assert.Empty(updates);
        Assert.Null(engine.WorkerProcessId);
    }

    [Fact]
    public async Task StreamPayloadsAreSplitIntoBoundedOrderedRequests()
    {
        var engine = await StreamingEngineAsync();
        await using var session = await ((ITranscriptionEngineRole)engine).StartStreamingAsync("en", CancellationToken.None);
        var updates = new List<StreamingTranscriptEvent>();
        session.TranscriptReceived += updates.Add;
        await session.SendAudioAsync(new byte[2 * TranscriptionWorkerProtocol.MaximumStreamChunkBytes + 2], CancellationToken.None);
        Assert.Equal(3, updates.Count);
        Assert.DoesNotContain(updates, value => value.IsFinal);
    }

    [Fact]
    public async Task StreamingCancellationReachesNativeWorkWithoutBlockingTheControlReader()
    {
        var engine = await StreamingEngineAsync(Launch() with { CancellationGrace = TimeSpan.FromMilliseconds(150) });
        await using var session = await ((ITranscriptionEngineRole)engine).StartStreamingAsync("hang", CancellationToken.None);
        var pid = engine.WorkerProcessId!.Value;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAudioAsync(s_audio, cancel.Token));
        await session.DisposeAsync();
        await WaitUntilGoneAsync(pid);
        Assert.Null(engine.WorkerProcessId);
    }

    [Fact]
    public async Task ConcurrentStreamDisposalWaitsForTheSameInFlightNativeWork()
    {
        var engine = await StreamingEngineAsync(Launch() with { CancellationGrace = TimeSpan.FromSeconds(1) });
        var session = await ((ITranscriptionEngineRole)engine).StartStreamingAsync("hang", CancellationToken.None);
        var feeding = session.SendAudioAsync(s_audio, CancellationToken.None);
        await WaitForLogAsync("stream feed entered");
        var first = session.DisposeAsync().AsTask();
        var second = session.DisposeAsync().AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        await Task.WhenAll(first, second);
        await Assert.ThrowsAsync<InvalidOperationException>(() => feeding);
        Assert.Null(engine.WorkerProcessId);
    }

    [Fact]
    public async Task UnloadingAnIdleStreamInvalidatesItWithoutWaitingForMoreAudio()
    {
        var engine = await StreamingEngineAsync();
        await using var session = await ((ITranscriptionEngineRole)engine).StartStreamingAsync("en", CancellationToken.None);
        await engine.UnloadModelAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAudioAsync(s_audio, CancellationToken.None));
        Assert.Null(engine.WorkerProcessId);
    }

    [Fact]
    public async Task WorkerRejectsStaleSessionIdentityAndOutOfOrderAudio()
    {
        _inner.EnableStreaming = true;
        await File.WriteAllTextAsync(SettingsPath, """{ "probe-streaming": "enabled" }""");
        await using var worker = await TranscriptionWorkerProcess.StartAsync(_runner, Launch(), TranscriptionAccelerationPreference.Cpu, null, Log, CancellationToken.None);
        var start = await worker.SendAsync(new TranscriptionWorkerMessage
        {
            Command = "stream-start", ModelId = "small", SessionId = "first", Sequence = 0,
        }, default, CancellationToken.None);
        Assert.Null(start.Error);
        foreach (var (id, sequence) in new[] { ("stale", 1L), ("first", 2L) })
        {
            var rejected = await worker.SendAsync(new TranscriptionWorkerMessage
            {
                Command = "stream-feed", SessionId = id, Sequence = sequence,
            }, s_audio, CancellationToken.None);
            Assert.NotNull(rejected.Error);
        }
        var valid = await worker.SendAsync(new TranscriptionWorkerMessage
        {
            Command = "stream-feed", SessionId = "first", Sequence = 1,
        }, s_audio, CancellationToken.None);
        Assert.Null(valid.Error);
    }

    [Fact]
    public async Task AutoNeverRetriesOnlyTheFailedStreamingChunkOnCpu()
    {
        var engine = await HardwareEngineAsync();
        _inner.EnableStreaming = true;
        await File.WriteAllTextAsync(SettingsPath, """{ "probe-streaming": "enabled", "probe-hardware": "enabled" }""");
        ConfigureGpu(engine, TranscriptionAccelerationPreference.Auto);
        await using var session = await ((ITranscriptionEngineRole)engine).StartStreamingAsync("fail-on-gpu", CancellationToken.None);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => session.SendAudioAsync(s_audio, CancellationToken.None));
        Assert.False(engine.UsesCpuFallback);
        Assert.Null(engine.WorkerProcessId);
        Assert.DoesNotContain(Logs(), line => line.Contains("retrying once on CPU", StringComparison.Ordinal));
    }

    private static ProcessCommand FakeWorker(string behavior)
    {
        return new ProcessCommand(
            "dotnet",
            [typeof(ProcessTestChildMarker).Assembly.Location, "fake-transcription-worker", behavior]
        );
    }

    private string[] Arguments(string socketPath, int parentProcessId)
    {
        return
        [
            TranscriptionWorkerServer.Argument,
            .. new TranscriptionWorkerArguments(
                socketPath,
                parentProcessId,
                PackageDirectory,
                PluginDataRoot,
                Path.Join(_root, "assets"),
                WorkerProbePlugin.Id,
                TranscriptionAccelerationPreference.Auto
            ).ToArguments(),
        ];
    }

    /// <summary>A real worker with the test as its host, for scenarios the host class prevents.</summary>
    private sealed class RawWorker : IAsyncDisposable
    {
        private readonly string? _releaseFile;

        private RawWorker(IPluginProcessSession session, Socket connection, int workerProcessId, string? releaseFile)
        {
            Session = session;
            Connection = connection;
            Stream = new NetworkStream(connection);
            WorkerProcessId = workerProcessId;
            _releaseFile = releaseFile;
        }

        internal IPluginProcessSession Session { get; }
        internal Socket Connection { get; }
        internal NetworkStream Stream { get; }
        internal int WorkerProcessId { get; }

        /// <summary>Starts the worker directly, or behind a shell that stays until released when no parent is given.</summary>
        internal static async Task<RawWorker> StartAsync(TranscriptionWorkerTests test, int? parentProcessId)
        {
            var socketPath = Path.Join(test.SocketDirectory, $"raw-{Guid.NewGuid():N}.sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            var environment = new Dictionary<string, string>
            {
                [TranscriptionWorkerServer.TokenEnvironmentVariable] = "token",
            };
            string? releaseFile = null;
            ProcessCommand command;
            if (parentProcessId is { } parent)
            {
                command = new ProcessCommand(AppExecutable, test.Arguments(socketPath, parent), environment);
            }
            else
            {
                releaseFile = Path.Join(test._root, $"release-{Guid.NewGuid():N}");
                // The shell passes its own pid as the worker's parent, then waits for the release file.
                const string script = """
                                      "$0" "$@" --parent-pid $$ &
                                      while [ ! -e "$RELEASE" ]; do sleep 0.05; done
                                      """;
                var arguments = test.Arguments(socketPath, 1).ToList();
                var parentIndex = arguments.IndexOf("--parent-pid");
                arguments.RemoveRange(parentIndex, 2);
                environment["RELEASE"] = releaseFile;
                command = new ProcessCommand("sh", ["-c", script, AppExecutable, .. arguments], environment);
            }

            var session = test._runner.StartSession(
                    command,
                    new ProcessSessionOptions(StandardError: ProcessSessionOutputMode.Discard)
                ).Session
                ?? throw new InvalidOperationException("The worker did not start.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(timeout.Token);
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }

            var raw = new RawWorker(session, connection, PeerProcessId(connection), releaseFile);
            try
            {
                TranscriptionWorkerFrame? hello;
                // The probe logs while it activates; those frames may come first.
                do
                {
                    hello = await TranscriptionWorkerProtocol.ReadAsync(raw.Stream, 0, timeout.Token);
                } while (hello?.Message.Type == TranscriptionWorkerMessageTypes.Log);

                Assert.Equal(TranscriptionWorkerMessageTypes.Hello, hello?.Message.Type);
                Assert.Equal("token", hello?.Message.Token);
                Assert.Null(hello?.Message.Error);
                return raw;
            }
            catch
            {
                await raw.DisposeAsync();
                throw;
            }
        }

        /// <summary>Sends a request that starts a process in the worker and hangs; returns that process id.</summary>
        internal async Task<int> SpawnAndHangAsync()
        {
            await TranscriptionWorkerProtocol.WriteAsync(
                Stream,
                new TranscriptionWorkerMessage
                {
                    Type = TranscriptionWorkerMessageTypes.Request,
                    Id = 1,
                    Command = TranscriptionWorkerCommands.Transcribe,
                    Language = "spawn-and-hang",
                },
                new byte[] { 0 },
                CancellationToken.None
            );
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (await TranscriptionWorkerProtocol.ReadAsync(Stream, 0, timeout.Token) is { } frame)
            {
                if (frame.Message.LogMessage is { } text && text.StartsWith("spawned ", StringComparison.Ordinal))
                {
                    return int.Parse(text["spawned ".Length..], CultureInfo.InvariantCulture);
                }
            }

            throw new EndOfStreamException("The worker ended before it started a process.");
        }

        internal void ReleaseParent()
        {
            File.WriteAllText(_releaseFile!, "");
        }

        private static int PeerProcessId(Socket socket)
        {
            // Linux: struct ucred { pid_t pid; uid_t uid; gid_t gid; } under SOL_SOCKET/SO_PEERCRED.
            var credentials = new byte[12];
            socket.GetRawSocketOption(1, 17, credentials);
            return BitConverter.ToInt32(credentials, 0);
        }

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            Connection.Dispose();
            await Session.DisposeAsync();
            if (IsRunning(WorkerProcessId))
            {
                Process.GetProcessById(WorkerProcessId).Kill();
            }
        }
    }
}
