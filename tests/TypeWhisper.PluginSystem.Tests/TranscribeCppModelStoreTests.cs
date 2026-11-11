extern alias TranscribeCpp;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using TranscribeCpp::TypeWhisper.Plugin.TranscribeCpp;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class TranscribeCppModelStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tw-transcribe-store-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] s_weights = Encoding.UTF8.GetBytes("GGUF public synthetic test weights, not a real model");

    [Fact]
    public async Task ResumesPinnedDownloadAndPublishesOnlyAfterDigestVerification()
    {
        using var handler = new RangeHandler(s_weights);
        using var client = new HttpClient(handler);
        var store = new TranscribeModelStore(_root, client);
        var model = TestModel();
        var path = Path.Combine(_root, model.Filename);
        await File.WriteAllBytesAsync(path + ".partial", s_weights[..12]);
        Assert.False(store.IsReady(model));
        await store.DownloadAsync(model, null, CancellationToken.None);
        Assert.Equal(12, handler.RangeStart);
        Assert.Equal(model.DownloadUrl, handler.RequestUrl);
        Assert.True(store.IsReady(model));
        Assert.Equal(s_weights, await File.ReadAllBytesAsync(path));
        await using var lease = await store.AcquireAsync(model, CancellationToken.None);
        Assert.Equal(path, lease.Path);
    }

    [Fact]
    public async Task DigestMismatchDoesNotPublishAndRetryDiscardsCorruptPrefix()
    {
        using var handler = new RangeHandler(s_weights);
        using var client = new HttpClient(handler);
        var store = new TranscribeModelStore(_root, client);
        var model = TestModel();
        await File.WriteAllBytesAsync(Path.Combine(_root, model.Filename) + ".partial", new byte[12]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync(model, null, CancellationToken.None));
        Assert.False(store.IsReady(model));
        Assert.False(File.Exists(Path.Combine(_root, model.Filename)));
        await store.DownloadAsync(model, null, CancellationToken.None);
        Assert.True(store.IsReady(model));
    }

    [Fact]
    public async Task UnverifiedFullFileAndChangedVerifiedFileAreNotReady()
    {
        using var client = new HttpClient(new RangeHandler(s_weights));
        var store = new TranscribeModelStore(_root, client);
        var model = TestModel();
        var path = Path.Combine(_root, model.Filename);
        await File.WriteAllBytesAsync(path, s_weights);
        Assert.False(store.IsReady(model));
        await store.DownloadAsync(model, null, CancellationToken.None);
        Assert.True(store.IsReady(model));
        await File.AppendAllTextAsync(path, "corruption");
        Assert.False(store.IsReady(model));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await using var unused = await store.AcquireAsync(model, CancellationToken.None);
        });
    }

    [Fact]
    public async Task LoadingRehashesEvenWhenSizeAndTimestampAreUnchanged()
    {
        using var client = new HttpClient(new RangeHandler(s_weights));
        var store = new TranscribeModelStore(_root, client);
        var model = TestModel();
        await store.DownloadAsync(model, null, CancellationToken.None);
        var path = Path.Combine(_root, model.Filename);
        var timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllBytesAsync(path, new byte[s_weights.Length]);
        File.SetLastWriteTimeUtc(path, timestamp);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await using var unused = await store.AcquireAsync(model, CancellationToken.None);
        });
        Assert.False(store.IsReady(model));
    }

    [Fact]
    public async Task LinkedFileIsVerifiedAndDeleteRemovesOnlyTheLinkRecord()
    {
        using var client = new HttpClient(new RangeHandler(s_weights));
        var store = new TranscribeModelStore(_root, client);
        var external = Path.Combine(_root, "another-app.gguf");
        await File.WriteAllBytesAsync(external, s_weights);
        var model = TestModel();
        await store.LinkAsync(model, external, CancellationToken.None);
        await using (var lease = await store.AcquireAsync(model, CancellationToken.None))
        {
            Assert.Equal(external, lease.Path);
            Assert.True(lease.IsExternal);
        }
        await store.DeleteAsync(model, CancellationToken.None);
        Assert.Equal(s_weights, await File.ReadAllBytesAsync(external));
        Assert.False(store.IsReady(model));
    }

    [Fact]
    public async Task InvalidLinkDoesNotReplaceAnExistingVerifiedSelection()
    {
        using var client = new HttpClient(new RangeHandler(s_weights));
        var store = new TranscribeModelStore(_root, client);
        var model = TestModel();
        await store.DownloadAsync(model, null, CancellationToken.None);
        var external = Path.Combine(_root, "wrong.gguf");
        await File.WriteAllBytesAsync(external, new byte[s_weights.Length]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LinkAsync(model, external, CancellationToken.None));
        Assert.True(store.IsReady(model));
        await store.DeleteAsync(model, CancellationToken.None);
        Assert.True(File.Exists(external));
    }

    [Fact]
    public async Task LinkedFileModificationAndMissingFileInvalidateReadiness()
    {
        using var client = new HttpClient(new RangeHandler(s_weights));
        var store = new TranscribeModelStore(_root, client);
        var external = Path.Combine(_root, "external.gguf");
        await File.WriteAllBytesAsync(external, s_weights);
        var model = TestModel();
        await store.LinkAsync(model, external, CancellationToken.None);
        await File.AppendAllTextAsync(external, "changed");
        Assert.False(store.IsReady(model));
        File.Delete(external);
        Assert.False(store.IsReady(model));
        await store.DeleteAsync(model, CancellationToken.None);
    }

    [Fact]
    public async Task LoadedModelLeasePreventsDeleteAndOtherWritersUntilReleased()
    {
        using var client = new HttpClient(new RangeHandler(s_weights));
        var store = new TranscribeModelStore(_root, client);
        var model = TestModel();
        await store.DownloadAsync(model, null, CancellationToken.None);
        await using (await store.AcquireAsync(model, CancellationToken.None))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync(model, cts.Token));
            Assert.True(store.IsReady(model));
        }
        await store.DeleteAsync(model, CancellationToken.None);
        Assert.False(store.IsReady(model));
    }

    [Fact]
    public async Task CancellationPreservesPartialButNeverPublishesReadyModel()
    {
        using var handler = new RangeHandler(s_weights);
        using var client = new HttpClient(handler);
        var store = new TranscribeModelStore(_root, client);
        var model = TestModel();
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cts.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DownloadAsync(model, progress, cts.Token));
        Assert.False(store.IsReady(model));
        Assert.True(File.Exists(Path.Combine(_root, model.Filename) + ".partial"));
    }

    [Fact]
    public async Task InsufficientSpaceAndUnreviewedTermsFailBeforeHttp()
    {
        using var handler = new RangeHandler(s_weights);
        using var client = new HttpClient(handler);
        var store = new TranscribeModelStore(_root, client, _ => 0);
        await Assert.ThrowsAnyAsync<IOException>(() => store.DownloadAsync(TestModel(), null, CancellationToken.None));
        var restricted = TestModel() with { License = "other" };
        await Assert.ThrowsAsync<NotSupportedException>(() => store.DownloadAsync(restricted, null, CancellationToken.None));
        Assert.Null(handler.RequestUrl);
    }

    [Fact]
    public async Task DeleteNeverFollowsAnUnexpectedOwnedFileSymlink()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var client = new HttpClient(new RangeHandler(s_weights));
        var store = new TranscribeModelStore(_root, client);
        var model = TestModel();
        var external = Path.Combine(_root, "external.gguf");
        await File.WriteAllBytesAsync(external, s_weights);
        File.CreateSymbolicLink(Path.Combine(_root, model.Filename), external);
        Assert.False(store.IsReady(model));
        await store.DeleteAsync(model, CancellationToken.None);
        Assert.Equal(s_weights, await File.ReadAllBytesAsync(external));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacedReceiptOrModelSymlinkIsNotReadyAndNeverModifiesItsTarget(bool replaceReceipt)
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var client = new HttpClient(new RangeHandler(s_weights));
        var store = new TranscribeModelStore(_root, client);
        var model = TestModel();
        await store.DownloadAsync(model, null, CancellationToken.None);
        var external = Path.Combine(_root, "external.gguf");
        await File.WriteAllBytesAsync(external, s_weights);
        var replaced = Path.Combine(_root, model.Filename) + (replaceReceipt ? ".ready.json" : "");
        File.Delete(replaced);
        File.CreateSymbolicLink(replaced, external);
        Assert.False(store.IsReady(model));
        await store.DeleteAsync(model, CancellationToken.None);
        Assert.Equal(s_weights, await File.ReadAllBytesAsync(external));
    }

    [Fact]
    public async Task SymlinkedStagingFileIsRejectedWithoutWritingIntoAnotherAppsFile()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var handler = new RangeHandler(s_weights);
        using var client = new HttpClient(handler);
        var store = new TranscribeModelStore(_root, client);
        var model = TestModel();
        var external = Path.Combine(_root, "external.gguf");
        await File.WriteAllBytesAsync(external, s_weights);
        File.CreateSymbolicLink(Path.Combine(_root, model.Filename) + ".partial", external);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync(model, null, CancellationToken.None));
        Assert.Null(handler.RequestUrl);
        Assert.Equal(s_weights, await File.ReadAllBytesAsync(external));
    }

    private static TranscribeModel TestModel() => TranscribeModelCatalog.LoadEmbedded()
        .Get("handy-computer/moonshine-streaming-medium-gguf") with
        {
            Size = s_weights.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(s_weights)),
        };

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class InlineProgress(Action<double> action) : IProgress<double>
    {
        public void Report(double value) => action(value);
    }

    private sealed class RangeHandler(byte[] bytes) : HttpMessageHandler
    {
        public long? RangeStart { get; private set; }
        public string? RequestUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUrl = request.RequestUri!.ToString();
            RangeStart = request.Headers.Range?.Ranges.Single().From;
            var start = (int)(RangeStart ?? 0);
            var response = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes[start..]),
            };
            if (start > 0)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, bytes.Length - 1, bytes.Length);
            return Task.FromResult(response);
        }
    }
}
