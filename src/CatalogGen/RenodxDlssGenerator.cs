using System.Text.Json;
using System.Text.RegularExpressions;

namespace CatalogGen;

public sealed record DlssOutput(RenodxDlssSection Section, List<string> Warnings, List<string> Errors);

/// <summary>
/// Builds the <c>renodxDlss</c> section: ShortFuse's renodx-dlss add-on has no official channel, so the builds come from
/// the GitHub releases of RankFTW/rhi-repo (tag <c>renodx-dlss-SF-&lt;version&gt;</c>, one zip each). That repository has no
/// licence, so <see cref="FileSync"/> keeps every build pinned (url + hash) and never mirrors it.
/// </summary>
public sealed partial class RenodxDlssGenerator(ITextSource text, FileSync sync, int keep = 10, int parallelism = 4)
{
    public const string ReleasesUrl = "https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100";
    public const string Mod = "renodx-dlss";

    private sealed record Release(string Tag, string Version, DateTimeOffset Published, string AssetName, string AssetUrl);

    public async Task<DlssOutput> BuildAsync(RenodxDlssSection? previous, DateTimeOffset now, CancellationToken ct)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var releases = await ReadReleasesAsync(warnings, ct);
        if (releases.Count == 0) throw new SourceException("rhi-repo lists no renodx-dlss-SF releases with a zip, looks broken");

        var previousByUrl = (previous?.Builds ?? []).Select(b => b.File).GroupBy(f => f.Url).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var gate = new SemaphoreSlim(Math.Max(1, parallelism));
        var results = await Task.WhenAll(releases.Select(async r =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var wanted = new FileEntry { Name = r.AssetName, Arch = "x64", Url = r.AssetUrl, Mod = Mod };
                previousByUrl.TryGetValue(r.AssetUrl, out var prev);
                return await sync.SyncAsync(wanted, prev, now, ct);
            }
            finally { gate.Release(); }
        }));

        var builds = new List<DlssBuild>();
        for (var i = 0; i < releases.Count; i++)
        {
            if (results[i].Warning is { } w) warnings.Add(w);
            if (results[i].Error is { } e) errors.Add(e);
            if (results[i].Entry is not { } file) continue;
            var r = releases[i];
            builds.Add(new DlssBuild { Tag = r.Tag, Version = r.Version, Published = FileSync.Iso(r.Published), File = file });
        }
        return new DlssOutput(new RenodxDlssSection { Source = ReleasesUrl, Builds = builds }, warnings, errors);
    }

    private async Task<List<Release>> ReadReleasesAsync(List<string> warnings, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(await text.GetStringAsync(ReleasesUrl, ct)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            throw new SourceException($"rhi-repo releases: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new SourceException("rhi-repo releases: not a list");
            var found = new List<Release>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Flag(item, "draft") || Flag(item, "prerelease")) continue;
                var tag = Str(item, "tag_name");
                var m = tag is null ? null : TagRule().Match(tag);
                if (m is not { Success: true }) continue;

                var zips = item.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
                    ? assets.EnumerateArray().Where(a => Str(a, "name")?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true).ToList()
                    : [];
                if (zips.Count != 1) { warnings.Add($"{tag}: expected one zip asset, found {zips.Count}; skipped"); continue; }
                if (!DateTimeOffset.TryParse(Str(item, "published_at"), out var published)) { warnings.Add($"{tag}: no publish date; skipped"); continue; }

                var name = Str(zips[0], "name");
                var url = Str(zips[0], "browser_download_url");
                if (name is null || url is null) { warnings.Add($"{tag}: the zip asset has no download url; skipped"); continue; }
                found.Add(new Release(tag!, m.Groups[1].Value, published, name, url));
            }
            return found.OrderByDescending(r => r.Published).ThenBy(r => r.Tag, StringComparer.Ordinal).Take(keep).ToList();
        }
    }

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"^renodx-dlss-SF-(.+)$")]
    private static partial Regex TagRule();
}
