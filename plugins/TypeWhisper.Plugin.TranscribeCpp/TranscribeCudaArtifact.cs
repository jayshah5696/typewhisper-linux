using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Plugin.TranscribeCpp;

/// <summary>The optional CUDA build is installed as a complete, separately verified native closure.</summary>
internal sealed class TranscribeCudaArtifact(string root, HttpClient client, TranscribeCudaArtifact.Pin? pin = null)
{
    internal sealed record FilePin(string Sha256, long Size);
    internal sealed record Pin(string Url, string ArchiveSha256, long ArchiveSize, string Prefix, Dictionary<string, FilePin> Files);
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private readonly Pin _pin = pin ?? LoadPin();
    private readonly string _root = Path.GetFullPath(root);
    internal string DirectoryPath => Path.Combine(_root, "cuda-0.4.1");
    private string Receipt => Path.Combine(DirectoryPath, ".ready");
    private string Archive => Path.Combine(_root, "cuda-0.4.1.tar.gz");
    private string LockPath => Path.Combine(_root, "cuda-0.4.1.lock");

    internal bool IsInstalled
    {
        get
        {
            try
            {
                RejectSymlinks(Receipt);
                return File.Exists(Receipt) && File.ReadAllText(Receipt) == _pin.ArchiveSha256
                    && _pin.Files.All(item => HasSize(item.Key, item.Value.Size));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    private bool HasSize(string name, long size)
    {
        var path = Path.Combine(DirectoryPath, name);
        RejectSymlinks(path);
        return File.Exists(path) && new FileInfo(path).Length == size;
    }

    internal async Task InstallAsync(IProgress<double>? progress, CancellationToken ct)
    {
        await using var lease = await LockAsync(ct).ConfigureAwait(false);
        if (IsInstalled) { progress?.Report(1); return; }
        RejectSymlinks(DirectoryPath);
        RejectSymlinks(Archive);
        RejectSymlinks(Archive + ".partial");
        DownloadSpace.EnsureAvailable(_root, _pin.ArchiveSize + _pin.Files.Values.Sum(value => value.Size), "transcribe.cpp CUDA runtime");
        await ResilientDownloader.DownloadToFileAsync(client, _pin.Url, Archive, _pin.ArchiveSize,
            TimeSpan.FromSeconds(30), true, bytes => progress?.Report(0.8 * bytes / _pin.ArchiveSize),
            path => VerifyFileAsync(path, new(_pin.ArchiveSha256, _pin.ArchiveSize), ct).GetAwaiter().GetResult(), ct).ConfigureAwait(false);
        var staging = Path.Combine(_root, "cuda-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await using var file = File.OpenRead(Archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.Name.StartsWith(_pin.Prefix + "/", StringComparison.Ordinal)
                    ? entry.Name[(_pin.Prefix.Length + 1)..] : "";
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    || name.Length == 0 || name.Split('/').Any(part => part is "" or "." or "..") || name.Contains('\\')
                    || !_pin.Files.TryGetValue(name, out var expected) || !seen.Add(name) || entry.Length != expected.Size)
                    throw new InvalidDataException("Unexpected member in the pinned CUDA artifact: " + entry.Name);
                var destination = Path.Combine(staging, name);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using (var output = File.Create(destination))
                {
                    if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, ct).ConfigureAwait(false);
                }
                await VerifyFileAsync(destination, expected, ct).ConfigureAwait(false);
            }
            if (seen.Count != _pin.Files.Count) throw new InvalidDataException("The CUDA artifact is missing required libraries or licenses.");
            using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "contract.json"), ct).ConfigureAwait(false));
            if (contract.RootElement.GetProperty("version").GetString() != "0.4.1"
                || contract.RootElement.GetProperty("header_hash").GetString() != "f99ad4169374b8f3")
                throw new InvalidDataException("The optional CUDA artifact does not match the pinned native binding.");
            await File.WriteAllTextAsync(Path.Combine(staging, ".ready"), _pin.ArchiveSha256, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
            Directory.Move(staging, DirectoryPath);
            File.Delete(Archive);
            progress?.Report(1);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    internal async Task<FileStream> AcquireAsync(CancellationToken ct)
    {
        RejectSymlinks(LockPath);
        Directory.CreateDirectory(_root);
        var lease = await InterProcessFileLock.AcquireSharedAsync(LockPath, ct).ConfigureAwait(false);
        try
        {
            if (!IsInstalled) throw new InvalidOperationException("Download the optional transcribe.cpp CUDA runtime explicitly, or retry on CPU or Vulkan.");
            try
            {
                foreach (var (name, expected) in _pin.Files)
                    await VerifyFileAsync(Path.Combine(DirectoryPath, name), expected, ct).ConfigureAwait(false);
            }
            catch (InvalidDataException) { File.Delete(Receipt); throw; }
            return lease;
        }
        catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal async Task ClearAsync(CancellationToken ct)
    {
        await using var lease = await LockAsync(ct).ConfigureAwait(false);
        RejectSymlinks(DirectoryPath);
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        RejectSymlinks(Archive);
        RejectSymlinks(Archive + ".partial");
        File.Delete(Archive);
        File.Delete(Archive + ".partial");
    }

    private Task<FileStream> LockAsync(CancellationToken ct)
    {
        RejectSymlinks(LockPath);
        Directory.CreateDirectory(_root);
        return InterProcessFileLock.AcquireAsync(LockPath, ct);
    }
    private static async Task VerifyFileAsync(string path, FilePin expected, CancellationToken ct)
    {
        RejectSymlinks(path);
        await using var file = File.OpenRead(path);
        if (file.Length != expected.Size
            || Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)) != expected.Sha256)
            throw new InvalidDataException("The CUDA artifact SHA-256 or size does not match the reviewed pin: " + Path.GetFileName(path));
    }
    private static void RejectSymlinks(string path)
    {
        for (var current = new FileInfo(path) as FileSystemInfo; current is not null;
             current = Path.GetDirectoryName(current.FullName) is { } parent ? new DirectoryInfo(parent) : null)
        {
            if (current.LinkTarget is not null) throw new IOException("CUDA artifact paths must not contain symbolic links.");
        }
    }
    private static Pin LoadPin()
    {
        using var stream = typeof(TranscribeCudaArtifact).Assembly.GetManifestResourceStream("TypeWhisper.Plugin.TranscribeCpp.Native.cuda.json")
            ?? throw new InvalidDataException("The optional CUDA artifact pin is missing.");
        return JsonSerializer.Deserialize<Pin>(stream, s_json) ?? throw new InvalidDataException("The optional CUDA artifact pin is invalid.");
    }
}
