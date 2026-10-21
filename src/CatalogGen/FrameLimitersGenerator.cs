using System.Text.Json;

namespace CatalogGen;

public sealed record FrameLimitersOutput(FrameLimitersSection Section, List<string> Warnings, List<string> Errors);

/// <summary>Where one limiter's add-on files are published: a GitHub release and the asset name of each bitness.</summary>
public sealed record LimiterSource(string Id, string Name, string Repository, string ReleaseApi, string Asset64, string Asset32);

/// <summary>
/// Builds the <c>frameLimiters</c> section: ReLimiter and Display Commander, read from their own GitHub releases. The repository's
/// licence decides in <see cref="FileSync"/>: a permissive one (ReLimiter, MIT) is mirrored, no licence (Display Commander) stays pinned
/// to the author's URL plus the hash. A limiter whose release can't be read keeps its last good entry and is reported as an error.
/// </summary>
public sealed class FrameLimitersGenerator(ITextSource text, FileSync sync, IReadOnlyList<LimiterSource>? sources = null)
{
    public static readonly IReadOnlyList<LimiterSource> Default =
    [
        new("relimiter", "ReLimiter", "RankFTW/ReLimiter", "https://api.github.com/repos/RankFTW/ReLimiter/releases/latest", "relimiter.addon64", "relimiter.addon32"),
        new("display-commander", "Display Commander", "pmnoxx/display-commander", "https://api.github.com/repos/pmnoxx/display-commander/releases/tags/latest",
            "zzz_display_commander.addon64", "zzz_display_commander.addon32"),
    ];

    private readonly IReadOnlyList<LimiterSource> _sources = sources ?? Default;

    public async Task<FrameLimitersOutput> BuildAsync(FrameLimitersSection? previous, DateTimeOffset now, CancellationToken ct)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var section = new FrameLimitersSection();
        foreach (var source in _sources)
        {
            var before = previous?.Limiters.FirstOrDefault(l => l.Id == source.Id);
            try
            {
                section.Limiters.Add(await BuildOneAsync(source, before, now, warnings, errors, ct));
            }
            catch (SourceException ex)
            {
                errors.Add($"{source.Name}: {ex.Message}; kept the last good entry");
                if (before is not null) section.Limiters.Add(before);
            }
            section.Sources.Add(new SourceInfo { Url = source.ReleaseApi, Count = section.Limiters.FirstOrDefault(l => l.Id == source.Id)?.Files.Count ?? 0 });
        }
        if (section.Limiters.Count == 0) throw new SourceException("no frame limiter could be read");
        return new FrameLimitersOutput(section, warnings, errors);
    }

    private async Task<LimiterEntry> BuildOneAsync(LimiterSource source, LimiterEntry? before, DateTimeOffset now, List<string> warnings, List<string> errors, CancellationToken ct)
    {
        var (tag, assets) = await ReadReleaseAsync(source, ct);
        var wanted = new List<FileEntry>();
        foreach (var (name, arch) in new[] { (source.Asset64, "x64"), (source.Asset32, "x86") })
        {
            if (assets.TryGetValue(name, out var url)) wanted.Add(new FileEntry { Name = name, Arch = arch, Url = url, Mod = source.Id });
            else if (arch == "x64") throw new SourceException($"release {tag} has no {name}");
            else warnings.Add($"{source.Name} {tag}: no {name}, so there is no 32-bit build");
        }

        var entry = new LimiterEntry { Id = source.Id, Name = source.Name, Repository = source.Repository, Release = tag };
        foreach (var w in wanted)
        {
            // The release tag is in ReLimiter's download address: a new tag is a new address for the same file.
            var prev = before?.Files.FirstOrDefault(f => f.Name == w.Name)?.Clone();
            if (prev is not null && prev.Url != w.Url) { prev.ETag = null; prev.LastModified = null; }
            var result = await sync.SyncAsync(w, prev, now, ct);
            if (result.Warning is not null) warnings.Add(result.Warning);
            if (result.Error is not null) errors.Add(result.Error);
            if (result.Entry is { } file) entry.Files.Add(file);
        }
        if (!entry.Files.Any(f => f.Arch == "x64")) throw new SourceException($"{source.Asset64} could not be downloaded");
        entry.License = entry.Files.Select(f => f.License).FirstOrDefault(l => l is not null);
        return entry;
    }

    private async Task<(string Tag, Dictionary<string, string> Assets)> ReadReleaseAsync(LimiterSource source, CancellationToken ct)
    {
        string json;
        try { json = await text.GetStringAsync(source.ReleaseApi, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SourceException($"release list: {ex.Message}");
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) throw new SourceException("the release has no tag");
            var assets = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var a in list.EnumerateArray())
                    if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out var n) && n.GetString() is { } name
                        && a.TryGetProperty("browser_download_url", out var u) && u.GetString() is { } url && url.StartsWith("https://", StringComparison.Ordinal))
                        assets[name] = url;
            return (tag!, assets);
        }
        catch (JsonException ex) { throw new SourceException($"release: {ex.Message}"); }
    }
}
