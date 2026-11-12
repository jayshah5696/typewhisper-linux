using System.Security.Cryptography;
using System.Text.Json;
using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Plugin.TranscribeCpp;

internal sealed class TranscribeModelStore
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly HttpClient _client;
    private readonly Func<string, long?>? _spaceProbe;

    public TranscribeModelStore(string root, HttpClient client, Func<string, long?>? spaceProbe = null)
    {
        _root = Path.GetFullPath(root);
        _client = client;
        _spaceProbe = spaceProbe;
        Directory.CreateDirectory(_root);
    }

    // This is a cheap UI check of a previously verified receipt. Loading always rehashes the file.
    public bool IsReady(TranscribeModel model)
    {
        TranscribeModelCatalog.ValidateModel(model);
        var receipt = ReadReceipt(model);
        return receipt is not null && MatchesFile(receipt);
    }

    public async Task DownloadAsync(TranscribeModel model, IProgress<double>? progress, CancellationToken ct)
    {
        EnsureAvailable(model);
        await using var lease = await LockAsync(model, ct).ConfigureAwait(false);
        if (IsReady(model))
        {
            progress?.Report(1);
            return;
        }
        var destination = OwnedPath(model);
        var partial = destination + ".partial";
        RejectSymlink(partial);
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        DownloadSpace.EnsureAvailable(destination, Math.Max(0, model.Size - existing), model.Name, _spaceProbe);
        // A stale receipt must never validate an incomplete replacement, even after a crash.
        File.Delete(ReceiptPath(model));
        await ResilientDownloader.DownloadToFileAsync(
            _client, model.DownloadUrl, destination, model.Size, TimeSpan.FromSeconds(30), true,
            bytes => progress?.Report(Math.Min(1, (double)bytes / model.Size)),
            path => VerifyFile(model, path, ct), ct,
            new DownloadSpaceRequirement(model.Name, 0, _spaceProbe)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await PublishReceiptAsync(model, destination, false, ct).ConfigureAwait(false);
        progress?.Report(1);
    }

    public string? GetLinkedPath(TranscribeModel model)
    {
        TranscribeModelCatalog.ValidateModel(model);
        return ReadReceipt(model) is { IsExternal: true } receipt ? receipt.Path : null;
    }

    public async Task LinkAsync(TranscribeModel model, string externalPath, CancellationToken ct)
    {
        EnsureAvailable(model);
        var path = Path.GetFullPath(externalPath);
        if (new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true) is { } target)
            path = target.FullName;
        if (path == OwnedPath(model))
            throw new ArgumentException("Use download to manage a file in this model's owned location.", nameof(externalPath));
        await using var lease = await LockAsync(model, ct).ConfigureAwait(false);
        await VerifyFileAsync(model, path, ct).ConfigureAwait(false);
        await PublishReceiptAsync(model, path, true, ct).ConfigureAwait(false);
        // Only our own destination can be removed. Never move, chmod, or delete the linked target.
        File.Delete(OwnedPath(model));
    }

    public async Task DeleteAsync(TranscribeModel model, CancellationToken ct)
    {
        TranscribeModelCatalog.ValidateModel(model);
        await using var lease = await LockAsync(model, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        File.Delete(ReceiptPath(model));
        File.Delete(OwnedPath(model));
        File.Delete(OwnedPath(model) + ".partial");
    }

    public async Task<TranscribeModelLease> AcquireAsync(TranscribeModel model, CancellationToken ct)
    {
        EnsureAvailable(model);
        var lease = await LockAsync(model, ct).ConfigureAwait(false);
        try
        {
            var receipt = ReadReceipt(model)
                ?? throw new FileNotFoundException($"Download or link the verified {model.Name} model first.");
            try
            {
                await VerifyFileAsync(model, receipt.Path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                File.Delete(ReceiptPath(model));
                throw;
            }
            return new TranscribeModelLease(receipt.Path, receipt.IsExternal, lease);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string OwnedPath(TranscribeModel model) => Path.Combine(_root, model.Filename);
    private string ReceiptPath(TranscribeModel model) => OwnedPath(model) + ".ready.json";
    private Task<FileStream> LockAsync(TranscribeModel model, CancellationToken ct) =>
        InterProcessFileLock.AcquireAsync(OwnedPath(model) + ".lock", ct);

    private static void EnsureAvailable(TranscribeModel model)
    {
        TranscribeModelCatalog.ValidateModel(model);
        if (model.UnavailableReason is { } reason)
            throw new NotSupportedException(reason);
    }

    private VerifiedModelReceipt? ReadReceipt(TranscribeModel model)
    {
        try
        {
            var path = ReceiptPath(model);
            RejectSymlink(path);
            if (!File.Exists(path))
                return null;
            using var stream = File.OpenRead(path);
            if (stream.Length > 16 * 1024)
                return null;
            var receipt = JsonSerializer.Deserialize<VerifiedModelReceipt>(stream, s_json);
            return receipt is not null && receipt.ModelId == model.Id && receipt.Revision == model.Revision
                && receipt.Sha256 == model.Sha256 && receipt.Size == model.Size
                && Path.IsPathFullyQualified(receipt.Path)
                && (receipt.IsExternal || receipt.Path == OwnedPath(model))
                ? receipt : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static bool MatchesFile(VerifiedModelReceipt receipt)
    {
        try
        {
            RejectSymlink(receipt.Path);
            var info = new FileInfo(receipt.Path);
            return info.Exists && info.Length == receipt.Size && info.LastWriteTimeUtc.Ticks == receipt.LastWriteUtcTicks;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task PublishReceiptAsync(TranscribeModel model, string path, bool external, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var receipt = new VerifiedModelReceipt(model.Id, model.Revision, model.Sha256, model.Size,
            path, info.LastWriteTimeUtc.Ticks, external);
        var destination = ReceiptPath(model);
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, receipt, s_json, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    private static async Task VerifyFileAsync(TranscribeModel model, string path, CancellationToken ct)
    {
        RejectSymlink(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != model.Size)
            throw new InvalidDataException($"{model.Name} has an unexpected file size. Download it again.");
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        if (!Convert.ToHexStringLower(hash).Equals(model.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException($"{model.Name} failed SHA-256 verification. Download it again.");
    }

    private static void VerifyFile(TranscribeModel model, string path, CancellationToken ct)
    {
        RejectSymlink(path);
        using var stream = File.OpenRead(path);
        if (stream.Length != model.Size)
            throw new InvalidDataException($"{model.Name} has an unexpected file size.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        ct.ThrowIfCancellationRequested();
        if (!Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(model.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException($"{model.Name} failed SHA-256 verification.");
    }

    private static void RejectSymlink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidDataException("A managed model or receipt cannot be a symbolic link. Use the verified link action.");
    }

    private sealed record VerifiedModelReceipt(string ModelId, string Revision, string Sha256, long Size,
        string Path, long LastWriteUtcTicks, bool IsExternal);
}

internal sealed class TranscribeModelLease(string path, bool isExternal, FileStream lease) : IAsyncDisposable
{
    public string Path { get; } = path;
    public bool IsExternal { get; } = isExternal;
    public ValueTask DisposeAsync() => lease.DisposeAsync();
}
