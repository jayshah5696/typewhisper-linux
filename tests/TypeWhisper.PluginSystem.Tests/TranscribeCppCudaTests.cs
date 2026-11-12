extern alias TranscribeCpp;

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using TranscribeCpp::TypeWhisper.Plugin.TranscribeCpp;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class TranscribeCppCudaTests
{
    [PinnedCudaArchiveFact]
    public async Task OfficialCudaArchiveInstallsAndVerifiesWithoutLoadingNativeCode()
    {
        var root = Path.Combine(Path.GetTempPath(), "tw-official-cuda-" + Guid.NewGuid().ToString("N"));
        var archive = Environment.GetEnvironmentVariable("TYPEWHISPER_TRANSCRIBE_CUDA_ARCHIVE")!;
        using var client = new HttpClient(new Handler(await File.ReadAllBytesAsync(archive)));
        var artifact = new TranscribeCudaArtifact(root, client);
        try
        {
            await artifact.InstallAsync(null, CancellationToken.None);
            await using (var lease = await artifact.AcquireAsync(CancellationToken.None))
            {
                Assert.True(artifact.IsInstalled);
                Assert.True(File.Exists(Path.Combine(artifact.DirectoryPath, "licenses", "LICENSE-ggml")));
            }
            Assert.DoesNotContain("libtranscribe.so", await File.ReadAllTextAsync("/proc/self/maps"), StringComparison.Ordinal);
        }
        finally
        {
            await artifact.ClearAsync(CancellationToken.None);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class PinnedCudaArchiveFactAttribute : FactAttribute
    {
        public PinnedCudaArchiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPEWHISPER_TRANSCRIBE_CUDA_ARCHIVE")))
                Skip = "Set TYPEWHISPER_TRANSCRIBE_CUDA_ARCHIVE to the official pinned archive for offline verification.";
        }
    }

    [Fact]
    public async Task ExplicitInstallVerifiesClosureAndKeepsNativeLibrariesOutOfTheHost()
    {
        using var fixture = new Fixture();
        Assert.False(fixture.Artifact.IsInstalled);
        Assert.Equal(0, fixture.Handler.Requests);
        await fixture.Artifact.InstallAsync(null, CancellationToken.None);
        Assert.True(fixture.Artifact.IsInstalled);
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.Equal(fixture.Files["libggml-cuda.so"], await File.ReadAllBytesAsync(Path.Combine(fixture.Artifact.DirectoryPath, "libggml-cuda.so")));
        await using (var lease = await fixture.Artifact.AcquireAsync(CancellationToken.None))
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Artifact.ClearAsync(cancel.Token));
        }
        await fixture.Artifact.ClearAsync(CancellationToken.None);
        Assert.False(fixture.Artifact.IsInstalled);
        Assert.DoesNotContain("libggml-cuda.so", await File.ReadAllTextAsync("/proc/self/maps"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("missing")]
    [InlineData("unexpected")]
    [InlineData("traversal")]
    [InlineData("symlink")]
    public async Task InvalidArchiveNeverPublishesReadyState(string failure)
    {
        using var fixture = new Fixture(failure);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Artifact.InstallAsync(null, CancellationToken.None));
        Assert.False(fixture.Artifact.IsInstalled);
        Assert.False(Directory.Exists(fixture.Artifact.DirectoryPath));
    }

    [Fact]
    public async Task MultipleWorkersCanReadTheSameCudaArtifactButClearStillWaitsForEveryLease()
    {
        using var fixture = new Fixture();
        await fixture.Artifact.InstallAsync(null, CancellationToken.None);
        await using var first = await fixture.Artifact.AcquireAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await using var second = await fixture.Artifact.AcquireAsync(deadline.Token);
        using var canceledClear = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Artifact.ClearAsync(canceledClear.Token));
        Assert.True(fixture.Artifact.IsInstalled);
    }

    [Fact]
    public async Task SameSizeCorruptionIsRejectedBeforeNativeUse()
    {
        using var fixture = new Fixture();
        await fixture.Artifact.InstallAsync(null, CancellationToken.None);
        var path = Path.Combine(fixture.Artifact.DirectoryPath, "libggml-cuda.so");
        await File.WriteAllBytesAsync(path, new byte[fixture.Files["libggml-cuda.so"].Length]);
        Assert.True(fixture.Artifact.IsInstalled); // UI checks are cheap; native use always hashes.
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Artifact.AcquireAsync(CancellationToken.None));
        Assert.False(fixture.Artifact.IsInstalled);
        await fixture.Artifact.InstallAsync(null, CancellationToken.None);
        await using var lease = await fixture.Artifact.AcquireAsync(CancellationToken.None);
        Assert.True(fixture.Artifact.IsInstalled);
    }

    [Fact]
    public async Task CancelDoesNotPublishOrDiscardAnExistingGoodRuntime()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Artifact.InstallAsync(null, cancel.Token));
        Assert.False(fixture.Artifact.IsInstalled);
        await fixture.Artifact.InstallAsync(null, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Artifact.InstallAsync(null, cancel.Token));
        Assert.True(fixture.Artifact.IsInstalled);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tw-cuda-artifact-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _client;
        internal Dictionary<string, byte[]> Files { get; } = new()
        {
            ["contract.json"] = "{\"version\":\"0.4.1\",\"header_hash\":\"f99ad4169374b8f3\"}"u8.ToArray(),
            ["libggml-cuda.so"] = "synthetic native module"u8.ToArray(),
            ["licenses/LICENSE"] = "synthetic license"u8.ToArray(),
        };
        internal Handler Handler { get; }
        internal TranscribeCudaArtifact Artifact { get; }
        internal Fixture(string? failure = null)
        {
            using var archive = new MemoryStream();
            using (var gzip = new GZipStream(archive, CompressionMode.Compress, leaveOpen: true))
            using (var writer = new TarWriter(gzip, leaveOpen: true))
            {
                foreach (var (name, bytes) in Files)
                {
                    if (failure == "missing" && name == "libggml-cuda.so") continue;
                    var actual = failure == "traversal" ? "../escape" : name;
                    var entry = new PaxTarEntry(TarEntryType.RegularFile, "bundle/" + actual) { DataStream = new MemoryStream(bytes) };
                    writer.WriteEntry(entry);
                }
                if (failure == "unexpected") writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "bundle/extra") { DataStream = new MemoryStream([1]) });
                if (failure == "symlink") writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "bundle/link") { LinkName = "outside" });
            }
            var payload = archive.ToArray();
            Handler = new Handler(payload);
            _client = new HttpClient(Handler);
            Artifact = new TranscribeCudaArtifact(_root, _client, new TranscribeCudaArtifact.Pin(
                "https://example.invalid/cuda.tgz", failure == "digest" ? new string('0', 64) : Hash(payload), payload.Length, "bundle",
                Files.ToDictionary(item => item.Key, item => new TranscribeCudaArtifact.FilePin(Hash(item.Value), item.Value.Length))));
        }
        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        public void Dispose() { _client.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
    internal sealed class Handler(byte[] payload) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }
}
