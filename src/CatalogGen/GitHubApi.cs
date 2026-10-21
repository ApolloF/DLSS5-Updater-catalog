using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CatalogGen;

/// <summary>Source repository licences, read from the GitHub API once per repository and run.</summary>
public sealed class GitHubLicences(HttpClient http, string? token) : ILicenceSource
{
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> SpdxAsync(string repo, CancellationToken ct)
    {
        var lazy = _cache.GetOrAdd(repo, r => new Lazy<Task<string?>>(() => LookupAsync(r, ct)));
        return lazy.Value;
    }

    private async Task<string?> LookupAsync(string repo, CancellationToken ct)
    {
        using var req = GitHubRequest.Create(HttpMethod.Get, $"https://api.github.com/repos/{repo}", token);
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("license", out var l) && l.ValueKind == JsonValueKind.Object
            && l.TryGetProperty("spdx_id", out var id) && id.GetString() is { } spdx && spdx != "NOASSERTION")
            return spdx;
        return null;
    }
}

/// <summary>GitHub API JSON (releases lists), with the token so the run isn't held to the anonymous rate limit.</summary>
public sealed class GitHubText(HttpClient http, string? token) : ITextSource
{
    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = GitHubRequest.Create(HttpMethod.Get, url, token);
                using var res = await http.SendAsync(req, ct);
                res.EnsureSuccessStatusCode();
                return await res.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
        }
    }
}

internal static class GitHubRequest
{
    public static HttpRequestMessage Create(HttpMethod method, string url, string? token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }
}

/// <summary>
/// Release assets of the catalog repository: one release per month (<c>mirror-2026-10</c>), assets named by hash and never
/// overwritten, so a <c>mirrorUrl</c> keeps pointing at the exact bytes that were hashed.
/// </summary>
public sealed partial class GitHubMirror(HttpClient http, string repo, string token) : IMirror
{
    private sealed record Asset(long Id, string Name, string State, long Size, string Url);

    private sealed class MonthRelease
    {
        public long Id;
        public Dictionary<string, Asset> Assets = [];
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, Lazy<Task<MonthRelease>>> _releases = new();

    public static string AssetName(string sha256, string fileName) => $"{sha256[..16]}-{SafeChars().Replace(fileName, "_")}";

    public async Task<string> PublishAsync(string fileName, string sha256, string month, string path, CancellationToken ct)
    {
        var tag = $"mirror-{month}";
        var release = await _releases.GetOrAdd(tag, t => new Lazy<Task<MonthRelease>>(() => EnsureReleaseAsync(t, ct))).Value;
        var name = AssetName(sha256, fileName);
        var size = new FileInfo(path).Length;

        await release.Gate.WaitAsync(ct);
        try
        {
            if (release.Assets.TryGetValue(name, out var existing))
            {
                if (existing.State == "uploaded" && existing.Size == size) return existing.Url;
                // A half-finished upload of an earlier run: never a usable file, so removing it overwrites nothing.
                await DeleteAssetAsync(existing.Id, ct);
                release.Assets.Remove(name);
            }

            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
            using var content = new StreamContent(fs);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = size;
            using var req = GitHubRequest.Create(HttpMethod.Post,
                $"https://uploads.github.com/repos/{repo}/releases/{release.Id}/assets?name={Uri.EscapeDataString(name)}", token);
            req.Content = content;
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                throw new HttpRequestException($"upload of {name} returned {(int)res.StatusCode}: {Trim(await res.Content.ReadAsStringAsync(ct))}");
            var asset = ReadAsset(JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement);
            release.Assets[name] = asset;
            return asset.Url;
        }
        finally { release.Gate.Release(); }
    }

    private async Task<MonthRelease> EnsureReleaseAsync(string tag, CancellationToken ct)
    {
        using var get = GitHubRequest.Create(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/tags/{tag}", token);
        using var found = await http.SendAsync(get, ct);
        long id;
        if (found.StatusCode == HttpStatusCode.NotFound)
        {
            using var create = GitHubRequest.Create(HttpMethod.Post, $"https://api.github.com/repos/{repo}/releases", token);
            create.Content = new StringContent(JsonSerializer.Serialize(new
            {
                tag_name = tag,
                name = tag,
                body = "Immutable copies of upstream mod files, named by SHA-256 prefix. They belong to their authors under the licences listed in catalog.json; see the README.",
                make_latest = "false",
            }), Encoding.UTF8, "application/json");
            using var made = await http.SendAsync(create, ct);
            if (!made.IsSuccessStatusCode)
                throw new HttpRequestException($"creating release {tag} returned {(int)made.StatusCode}: {Trim(await made.Content.ReadAsStringAsync(ct))}");
            id = JsonDocument.Parse(await made.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("id").GetInt64();
        }
        else
        {
            found.EnsureSuccessStatusCode();
            id = JsonDocument.Parse(await found.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("id").GetInt64();
        }

        var release = new MonthRelease { Id = id };
        for (var page = 1; ; page++)
        {
            using var list = GitHubRequest.Create(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/{id}/assets?per_page=100&page={page}", token);
            using var res = await http.SendAsync(list, ct);
            res.EnsureSuccessStatusCode();
            var items = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.EnumerateArray().ToList();
            foreach (var a in items) { var asset = ReadAsset(a); release.Assets[asset.Name] = asset; }
            if (items.Count < 100) break;
        }
        return release;
    }

    private async Task DeleteAssetAsync(long assetId, CancellationToken ct)
    {
        using var req = GitHubRequest.Create(HttpMethod.Delete, $"https://api.github.com/repos/{repo}/releases/assets/{assetId}", token);
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode != HttpStatusCode.NotFound) res.EnsureSuccessStatusCode();
    }

    private static Asset ReadAsset(JsonElement a) => new(
        a.GetProperty("id").GetInt64(), a.GetProperty("name").GetString()!, a.GetProperty("state").GetString() ?? "",
        a.GetProperty("size").GetInt64(), a.GetProperty("browser_download_url").GetString()!);

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex SafeChars();
}
