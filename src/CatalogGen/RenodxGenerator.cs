namespace CatalogGen;

/// <summary>An upstream list is unreachable or looks broken; the previous section is kept and the run fails.</summary>
public sealed class SourceException(string message) : Exception(message);

public interface ITextSource
{
    Task<string> GetStringAsync(string url, CancellationToken ct);
}

public sealed record GeneratorOutput(RenodxSection Section, List<string> Warnings, List<string> Errors);

/// <summary>Builds the <c>renodx</c> section: lists, merge, overrides, then every binary through <see cref="FileSync"/>.</summary>
public sealed class RenodxGenerator(ITextSource text, FileSync sync, int parallelism = 4, bool acceptShrink = false, int minIndexGames = 50, int minWikiRows = 100)
{
    public async Task<GeneratorOutput> BuildAsync(RenodxSection? previous, OverrideBlock overrides, DateTimeOffset now, CancellationToken ct)
    {
        List<IndexGame> index;
        List<WikiRow> wiki;
        try
        {
            index = GamesIndex.Parse(await text.GetStringAsync(RenodxSources.GamesIndexUrl, ct), new Uri(RenodxSources.GamesIndexUrl));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            throw new SourceException($"games-index.json: {ex.Message}");
        }
        try
        {
            wiki = WikiParser.Parse(await text.GetStringAsync(RenodxSources.WikiUrl, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SourceException($"wiki Mods.md: {ex.Message}");
        }

        var wikiMods = wiki.Count(r => r.Table is WikiTable.List or WikiTable.Related or WikiTable.Deprecated);
        if (index.Count < minIndexGames) throw new SourceException($"games-index.json lists only {index.Count} games, looks truncated");
        if (wikiMods < minWikiRows) throw new SourceException($"the wiki Mods table has only {wikiMods} rows, looks truncated");

        var built = RenodxBuilder.Build(index, wiki, overrides);
        var section = built.Section;
        if (!acceptShrink && previous is { Mods.Count: > 0 } && section.Mods.Count < previous.Mods.Count * 0.8)
            throw new SourceException($"{section.Mods.Count} mods now against {previous.Mods.Count} before (more than 20% gone); " +
                                      "set CATALOG_ACCEPT_SHRINK=1 if upstream really removed them");

        section.Sources =
        [
            new SourceInfo { Url = RenodxSources.GamesIndexUrl, Count = index.Count },
            new SourceInfo { Url = RenodxSources.WikiUrl, Count = wiki.Count },
        ];

        var warnings = new List<string>(built.Warnings);
        var errors = new List<string>();
        await SyncFilesAsync(section, previous, now, warnings, errors, ct);
        return new GeneratorOutput(section, warnings, errors);
    }

    private async Task SyncFilesAsync(RenodxSection section, RenodxSection? previous, DateTimeOffset now,
        List<string> warnings, List<string> errors, CancellationToken ct)
    {
        var owners = section.Mods.Select(m => m.Files).Concat(section.Generic.Select(g => g.Files)).ToList();
        var previousByUrl = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        if (previous is not null)
            foreach (var f in previous.Mods.SelectMany(m => m.Files).Concat(previous.Generic.SelectMany(g => g.Files)))
                previousByUrl.TryAdd(f.Url, f);

        var wanted = owners.SelectMany(o => o).GroupBy(f => f.Url).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var results = new Dictionary<string, SyncResult>(StringComparer.Ordinal);
        var gate = new SemaphoreSlim(Math.Max(1, parallelism));
        var lockObj = new object();

        await Task.WhenAll(wanted.Select(async kv =>
        {
            await gate.WaitAsync(ct);
            try
            {
                previousByUrl.TryGetValue(kv.Key, out var prev);
                var result = await sync.SyncAsync(kv.Value, prev, now, ct);
                lock (lockObj)
                {
                    results[kv.Key] = result;
                    if (result.Warning is not null) warnings.Add(result.Warning);
                    if (result.Error is not null) errors.Add(result.Error);
                    if (result.Entry is { } e && (prev is null || prev.Sha256 != e.Sha256))
                        Console.WriteLine($"{(prev is null ? "new" : "changed")}: {e.Name} {e.Version}{(e.MirrorUrl is null ? "" : " (mirrored)")}");
                }
            }
            finally { gate.Release(); }
        }));

        foreach (var files in owners)
        {
            var updated = new List<FileEntry>();
            foreach (var f in files)
            {
                if (results[f.Url].Entry is not { } e) continue;
                var copy = e.Clone();
                copy.Name = f.Name;
                copy.Arch = f.Arch;
                copy.Mod = f.Mod;
                updated.Add(copy);
            }
            files.Clear();
            files.AddRange(updated);
        }
    }
}
