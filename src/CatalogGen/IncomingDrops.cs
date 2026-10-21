using System.Net;
using System.Text.Json;

namespace CatalogGen;

public sealed record IncomingAsset(long Id, string Name, string Url);

/// <summary>The <c>incoming</c> release: raw files the owner drops in, picked up (and removed) by the next run.</summary>
public interface IIncoming
{
    Task<List<IncomingAsset>> ListAsync(CancellationToken ct);

    Task DeleteAsync(long assetId, CancellationToken ct);
}

/// <summary>A downloaded, identified file from <c>incoming</c>. <see cref="Handled"/> once its content is mirrored (or already was), so the asset can go.</summary>
public sealed class Drop(IncomingAsset asset, string path, string sha256, long size, Identified identified) : IDisposable
{
    public IncomingAsset Asset { get; } = asset;
    public string Path { get; } = path;
    public string Sha256 { get; } = sha256;
    public long Size { get; } = size;
    public Identified Identified { get; } = identified;
    public bool Handled { get; set; }

    public void Dispose()
    {
        try { File.Delete(Path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public static class IncomingDrops
{
    /// <summary>Downloads and identifies every file in <c>incoming</c>. Files that are none of the known binaries stay there; each gets a warning.</summary>
    public static async Task<List<Drop>> ReadAsync(IIncoming incoming, IFetcher fetcher, List<string> warnings, CancellationToken ct)
    {
        var drops = new List<Drop>();
        foreach (var asset in await incoming.ListAsync(ct))
        {
            var result = await fetcher.FetchAsync(asset.Url, null, ct);
            if (result is not FetchResult.Fetched fetched)
            {
                warnings.Add($"incoming/{asset.Name}: left in place, {(result as FetchResult.Failed)?.Reason ?? "could not be read"}");
                continue;
            }
            var identified = NeuralIdentify.Identify(fetched.Path, asset.Name);
            if (identified is null)
            {
                File.Delete(fetched.Path);
                warnings.Add($"incoming/{asset.Name}: not nvngx_dlssnr.dll, a Lecram add-on or renodx-dlss.addon64; left in place");
                continue;
            }
            drops.Add(new Drop(asset, fetched.Path, fetched.Sha256, fetched.Size, identified));
        }
        return drops;
    }
}

public sealed class GitHubIncoming(HttpClient http, string repo, string token) : IIncoming
{
    public const string Tag = "incoming";
    private long? _releaseId;

    public async Task<List<IncomingAsset>> ListAsync(CancellationToken ct)
    {
        var id = await EnsureReleaseAsync(ct);
        var assets = new List<IncomingAsset>();
        for (var page = 1; ; page++)
        {
            using var req = GitHubRequest.Create(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/{id}/assets?per_page=100&page={page}", token);
            using var res = await http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            var items = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.EnumerateArray().ToList();
            // An upload that is still running is not a file yet.
            assets.AddRange(items.Where(a => a.GetProperty("state").GetString() == "uploaded").Select(a => new IncomingAsset(
                a.GetProperty("id").GetInt64(), a.GetProperty("name").GetString()!, a.GetProperty("browser_download_url").GetString()!)));
            if (items.Count < 100) break;
        }
        return assets;
    }

    public async Task DeleteAsync(long assetId, CancellationToken ct)
    {
        using var req = GitHubRequest.Create(HttpMethod.Delete, $"https://api.github.com/repos/{repo}/releases/assets/{assetId}", token);
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode != HttpStatusCode.NotFound) res.EnsureSuccessStatusCode();
    }

    private async Task<long> EnsureReleaseAsync(CancellationToken ct)
    {
        if (_releaseId is { } known) return known;
        using var get = GitHubRequest.Create(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/tags/{Tag}", token);
        using var found = await http.SendAsync(get, ct);
        if (found.StatusCode != HttpStatusCode.NotFound)
        {
            found.EnsureSuccessStatusCode();
            return (_releaseId = JsonDocument.Parse(await found.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("id").GetInt64()).Value;
        }

        using var create = GitHubRequest.Create(HttpMethod.Post, $"https://api.github.com/repos/{repo}/releases", token);
        create.Content = new StringContent(JsonSerializer.Serialize(new { tag_name = Tag, name = Tag, prerelease = true, make_latest = "false" }), System.Text.Encoding.UTF8, "application/json");
        using var made = await http.SendAsync(create, ct);
        if (!made.IsSuccessStatusCode)
            throw new HttpRequestException($"creating release {Tag} returned {(int)made.StatusCode}");
        return (_releaseId = JsonDocument.Parse(await made.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("id").GetInt64()).Value;
    }
}
