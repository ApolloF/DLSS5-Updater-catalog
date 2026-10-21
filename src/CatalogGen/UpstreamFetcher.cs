using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace CatalogGen;

/// <summary>
/// Downloads upstream binaries politely: conditional requests (ETag / Last-Modified) when a previous build is known,
/// and the body is only read when the size or ETag changed. Rejects anything that isn't a PE file (HTML error pages).
/// </summary>
public sealed class UpstreamFetcher(HttpClient http, string tempDir, TimeSpan? retryDelay = null) : IFetcher
{
    private const int Attempts = 3;

    public static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        }) { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DLSS5-Updater-catalog", "1"));
        return http;
    }

    public async Task<FetchResult> FetchAsync(string url, FileEntry? previous, CancellationToken ct)
    {
        FetchResult last = new FetchResult.Failed("no attempt made");
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                last = await TryOnceAsync(url, previous, ct);
                if (last is not FetchResult.Failed { Reason: var r } || !IsTransient(r)) return last;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                last = new FetchResult.Failed(ex.Message);
            }
            if (attempt < Attempts) await Task.Delay((retryDelay ?? TimeSpan.FromSeconds(5)) * attempt, ct);
        }
        return last;
    }

    private static bool IsTransient(string reason) => reason.StartsWith("HTTP 5") || reason.StartsWith("HTTP 429");

    private async Task<FetchResult> TryOnceAsync(string url, FileEntry? previous, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (previous?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag)) req.Headers.IfNoneMatch.Add(tag);
        if (previous?.LastModified is { } lm && DateTimeOffset.TryParse(lm, out var since)) req.Headers.IfModifiedSince = since;

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var newTag = res.Headers.ETag?.ToString();
        var modified = res.Content.Headers.LastModified?.UtcDateTime.ToString("R");

        if (res.StatusCode == HttpStatusCode.NotModified) return new FetchResult.Unchanged(newTag ?? previous?.ETag, modified ?? previous?.LastModified);
        if (!res.IsSuccessStatusCode) return new FetchResult.Failed($"HTTP {(int)res.StatusCode}");

        // Some hosts ignore conditional headers: same ETag and size is still "unchanged", without reading the body.
        if (previous is { Sha256: not null } && newTag is not null && newTag == previous.ETag && res.Content.Headers.ContentLength == previous.Size)
            return new FetchResult.Unchanged(newTag, modified ?? previous.LastModified);

        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var sha = SHA256.Create();
            long size = 0;
            var head = new byte[2];
            var headLen = 0;
            await using (var src = await res.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    for (var i = 0; i < read && headLen < 2; i++) head[headLen++] = buffer[i];
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    size += read;
                }
            }
            sha.TransformFinalBlock([], 0, 0);

            if (headLen < 2 || head[0] != 'M' || head[1] != 'Z')
            {
                File.Delete(path);
                return new FetchResult.Failed("not a Windows executable (no MZ header, probably an HTML page)");
            }
            if (res.Content.Headers.ContentLength is { } expected && expected != size)
            {
                File.Delete(path);
                return new FetchResult.Failed($"incomplete download ({size} of {expected} bytes)");
            }
            return new FetchResult.Fetched(path, Convert.ToHexString(sha.Hash!).ToLowerInvariant(), size, newTag, modified);
        }
        catch
        {
            try { File.Delete(path); }
            catch (IOException) { }
            throw;
        }
    }
}
