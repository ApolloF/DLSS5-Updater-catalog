using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CatalogGen;

namespace CatalogGen.Tests;

/// <summary>A zip built in memory: name -> content, directories end with '/'.</summary>
public static class TestZip
{
    public static byte[] Make(params string[] names)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var name in names)
            {
                var entry = zip.CreateEntry(name);
                if (name.EndsWith('/')) continue;
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write("content of " + name);
            }
        }
        return ms.ToArray();
    }

    public static byte[] Pe(string tag) => Encoding.ASCII.GetBytes("MZ" + tag);
}

/// <summary>Serves bytes by URL. A URL that is not served answers HTTP 404, like a dead link.</summary>
public sealed class FakeFiles(string dir) : IFetcher
{
    public Dictionary<string, byte[]> Served { get; } = new(StringComparer.Ordinal);
    public List<string> Requested { get; } = [];

    public Task<FetchResult> FetchAsync(string url, FileEntry? previous, CancellationToken ct)
    {
        Requested.Add(url);
        if (!Served.TryGetValue(url, out var bytes)) return Task.FromResult<FetchResult>(new FetchResult.Failed("HTTP 404"));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (previous?.Sha256 == hash && previous.Size == bytes.Length && previous.ETag is not null)
            return Task.FromResult<FetchResult>(new FetchResult.Unchanged(previous.ETag, previous.LastModified));
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        return Task.FromResult<FetchResult>(new FetchResult.Fetched(path, hash, bytes.Length, "\"" + hash[..8] + "\"", null));
    }
}

public sealed class FakeLicenceSource(Dictionary<string, string?> byRepo) : ILicenceSource
{
    public List<string> Asked { get; } = [];

    public Task<string?> SpdxAsync(string repo, CancellationToken ct)
    {
        Asked.Add(repo);
        return Task.FromResult(byRepo.GetValueOrDefault(repo));
    }
}

public sealed class FakeMirrorSink : IMirror
{
    public List<string> Uploads { get; } = [];

    public Task<string> PublishAsync(string fileName, string sha256, string month, string path, CancellationToken ct)
    {
        Uploads.Add(fileName);
        return Task.FromResult($"https://mirror.test/mirror-{month}/{sha256[..16]}-{fileName}");
    }

    public Task SetNotesAsync(string month, string body, CancellationToken ct) => Task.CompletedTask;
}

public sealed class FakeText(Dictionary<string, string> pages) : ITextSource
{
    public Task<string> GetStringAsync(string url, CancellationToken ct) =>
        pages.TryGetValue(url, out var text) ? Task.FromResult(text) : throw new HttpRequestException("404 " + url);
}
