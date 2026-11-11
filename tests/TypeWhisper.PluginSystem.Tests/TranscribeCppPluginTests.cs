using System.Security.Cryptography;
using Moq;
using TypeWhisper.Plugin.TranscribeCpp;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class TranscribeCppPluginTests
{
    [Fact]
    public async Task LoadedModelKeepsVerifiedFileLeaseUntilNativeModelIsFreed()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Plugin.LoadModelAsync(fixture.Definition.Id, CancellationToken.None);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, fixture.Runtime.InitializedBackend);
        Assert.True(fixture.Runtime.External);
        Assert.True(fixture.Plugin.AccelerationStatus.IsModelLoaded);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.DeleteAsync(fixture.Definition, cancellation.Token));
        Assert.True(fixture.Store.IsReady(fixture.Definition));
        await fixture.Plugin.UnloadModelAsync();
        Assert.True(fixture.Runtime.Model.Disposed);
        Assert.False(fixture.Plugin.AccelerationStatus.IsModelLoaded);
        Assert.Null(fixture.Plugin.AccelerationStatus.ActiveDevice);
        await fixture.Store.DeleteAsync(fixture.Definition, CancellationToken.None);
        Assert.True(File.Exists(fixture.External));
    }

    [Fact]
    public async Task FailedNativeLoadReleasesFileLeaseAndDoesNotClaimActiveExecution()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Runtime.Failure = new InvalidOperationException("native load failed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Plugin.LoadModelAsync(fixture.Definition.Id, CancellationToken.None));
        Assert.False(fixture.Plugin.AccelerationStatus.IsModelLoaded);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await fixture.Store.DeleteAsync(fixture.Definition, deadline.Token);
    }

    [Fact]
    public async Task CancellationWaitsForNativeWorkBeforeFreeingSessionOrModel()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var settle = new ManualResetEventSlim();
        fixture.Runtime.Model.Session.Work = token =>
        {
            entered.SetResult();
            settle.Wait(CancellationToken.None);
            token.ThrowIfCancellationRequested();
        };
        using var cancellation = new CancellationTokenSource();
        var running = fixture.Plugin.TranscribeAsync(TranscribeCppNativeTests.Wave([1, 2, 3]), "en", false, null, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var unloading = fixture.Plugin.UnloadModelAsync();
        try
        {
            Assert.False(running.IsCompleted);
            Assert.False(unloading.IsCompleted);
            Assert.False(fixture.Runtime.Model.Disposed);
            Assert.False(fixture.Runtime.Model.Session.Disposed);
        }
        finally { settle.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await unloading;
        Assert.True(fixture.Runtime.Model.Session.Disposed);
        Assert.True(fixture.Runtime.Model.Disposed);
    }

    [Fact]
    public async Task DeactivationIsNotReportedAsCallerCancellationAndDoesNotFreeInFlightNativeWork()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var settle = new ManualResetEventSlim();
        fixture.Runtime.Model.Session.Work = token =>
        {
            entered.SetResult();
            settle.Wait(CancellationToken.None);
            token.ThrowIfCancellationRequested();
        };
        var running = fixture.Plugin.TranscribeAsync(TranscribeCppNativeTests.Wave([1, 2, 3]), "en", false, null, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopping = fixture.Plugin.DeactivateAsync();
        try
        {
            Assert.False(stopping.IsCompleted);
            Assert.False(fixture.Runtime.Model.Disposed);
        }
        finally { settle.Set(); }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => running);
        Assert.Contains("deactivated", error.Message, StringComparison.OrdinalIgnoreCase);
        await stopping;
        Assert.True(fixture.Runtime.Model.Disposed);
    }

    [Fact]
    public async Task BoundedWindowsRetainRepeatedTextAndForwardOnlyStructuredDictionaryTerms()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Runtime.Model.Session.MaxAudioMs = 100;
        var input = TranscribeCppNativeTests.Wave(Enumerable.Repeat((short)1000, 5000).ToArray());
        var prompt = PluginTranscriptionPrompt.Encode("context, not a dictionary term", ["TypeWhisper"]);
        var result = await fixture.Plugin.TranscribeAsync(input, "en", false, prompt, CancellationToken.None);
        Assert.True(fixture.Runtime.Model.Session.Calls > 1);
        Assert.Equal(5000, fixture.Runtime.Model.Session.Samples);
        Assert.Equal(string.Join("\n", Enumerable.Repeat("very very good", fixture.Runtime.Model.Session.Calls)), result.Text);
        Assert.Equal("context, not a dictionary term", fixture.Runtime.Model.Session.Context);
        Assert.Equal(["TypeWhisper"], fixture.Runtime.Model.Session.Terms);
        Assert.True(fixture.Runtime.Model.Session.Disposed);
        fixture.Host.Verify(value => value.SetSetting(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task InvalidLanguageOrTranslationDoesNotLoadWeightsOrCallNative()
    {
        await using var fixture = await Fixture.CreateAsync();
        var wav = TranscribeCppNativeTests.Wave([1, 2]);
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Plugin.TranscribeAsync(wav, "gu", false, null, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Plugin.TranscribeAsync(wav, "en", true, null, CancellationToken.None));
        Assert.Null(fixture.Runtime.InitializedBackend);
        Assert.Equal(0, fixture.Runtime.Model.Session.Calls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "tw-gguf-plugin-" + Guid.NewGuid().ToString("N"));
        internal string External => Path.Combine(Root, "external.gguf");
        internal Mock<IPluginHostServices> Host { get; } = new();
        internal FakeRuntime Runtime { get; } = new();
        internal TranscribeCppPlugin Plugin { get; private set; } = null!;
        internal TranscribeModelStore Store { get; private set; } = null!;
        internal TranscribeModel Definition { get; private set; } = null!;
        private readonly HttpClient _client = new();

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.Root);
            var bytes = "synthetic GGUF file"u8.ToArray();
            await File.WriteAllBytesAsync(fixture.External, bytes);
            var catalog = TranscribeModelCatalog.LoadEmbedded();
            fixture.Definition = catalog.Models.First(model => model.Architecture == "moonshine_streaming") with
                { Size = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Languages = ["en"], SupportsTranslation = false };
            fixture.Store = new TranscribeModelStore(Path.Combine(fixture.Root, "models"), fixture._client);
            await fixture.Store.LinkAsync(fixture.Definition, fixture.External, CancellationToken.None);
            fixture.Host.SetupGet(value => value.PluginAssetDirectory).Returns(fixture.Root);
            fixture.Host.Setup(value => value.GetSetting<string>("selectedModel")).Returns(fixture.Definition.Id);
            fixture.Plugin = new TranscribeCppPlugin(() => fixture.Runtime, catalog with { Models = [fixture.Definition] });
            await fixture.Plugin.ActivateAsync(fixture.Host.Object);
            fixture.Plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
            fixture.Plugin.ConfigureHardware(new(TranscriptionAccelerationPreference.Cpu, fixture.Runtime.Model.Device));
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await Plugin.DeactivateAsync();
            Plugin.Dispose();
            _client.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakeRuntime : ITranscribeNativeRuntime
    {
        internal TranscriptionAccelerationBackend? InitializedBackend { get; private set; }
        internal bool External { get; private set; }
        internal InvalidOperationException? Failure { get; set; }
        internal FakeModel Model { get; } = new();
        public void Initialize(TranscriptionAccelerationBackend? backend) => InitializedBackend = backend;
        public IReadOnlyList<TranscriptionDevice> DiscoverDevices() => [Model.Device];
        public ITranscribeNativeModel Load(string path, TranscribeModel definition, TranscriptionDevice device, bool external)
        {
            if (Failure is not null) throw Failure;
            External = external;
            return Model;
        }
        public void Dispose() { }
    }
    private sealed class FakeModel : ITranscribeNativeModel
    {
        internal bool Disposed { get; private set; }
        internal FakeSession Session { get; } = new();
        public TranscriptionDevice Device { get; } = new(TranscriptionAccelerationBackend.Cpu, "cpu", "Test CPU", TranscriptionDeviceKind.Cpu);
        public ITranscribeNativeSession CreateSession() => Session;
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeSession : ITranscribeNativeSession
    {
        public long MaxAudioMs { get; set; }
        internal Action<CancellationToken>? Work { get; set; }
        internal bool Disposed { get; private set; }
        internal int Calls { get; private set; }
        internal int Samples { get; private set; }
        internal string? Context { get; private set; }
        internal IReadOnlyList<string>? Terms { get; private set; }
        public NativeTranscript Run(float[] samples, string? language, bool translate, string? context, IReadOnlyList<string> vocabulary, CancellationToken ct)
        {
            Calls++;
            Samples += samples.Length;
            Context = context;
            Terms = vocabulary;
            Work?.Invoke(ct);
            return new("very very good", "en");
        }
        public void Dispose() => Disposed = true;
    }
}
