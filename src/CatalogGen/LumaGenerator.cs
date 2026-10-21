using System.Text.Json;
using System.Text.RegularExpressions;

namespace CatalogGen;

public sealed record LumaOutput(LumaSection Section, List<string> Warnings, List<string> Errors);

/// <summary>
/// Builds the <c>luma</c> section from Filoppi/Luma-Framework: the wiki's tables (completed mods, the generic Unreal Engine games) merged with
/// the zips of the latest release. The repository has no usable licence (NOASSERTION), so every zip is pinned (upstream URL plus hash) and never
/// mirrored. Test and Dev builds are not listed. A source that can't be read keeps the last good section.
/// </summary>
public sealed partial class LumaGenerator(ITextSource wiki, ITextSource api, FileSync sync, int parallel = 4, int minMods = 10, int minUnrealGames = 50)
{
    public const string WikiUrl = "https://raw.githubusercontent.com/wiki/Filoppi/Luma-Framework/Home.md";
    public const string ReleaseApi = "https://api.github.com/repos/Filoppi/Luma-Framework/releases/latest";
    public const string UnrealId = "Unreal_Engine";

    /// <summary>Release zips that are not a game's mod: only listed when a wiki row points at them (the Unity one is).</summary>
    private static readonly HashSet<string> NotAGame = new(StringComparer.OrdinalIgnoreCase) { "Generic_Mod", "Graphics_Analyzer", "Unity_Engine", UnrealId };

    public async Task<LumaOutput> BuildAsync(LumaSection? previous, DateTimeOffset now, CancellationToken ct)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var parsed = await ReadWikiAsync(ct);
        var (tag, zips) = await ReadReleaseAsync(ct);
        if (!zips.ContainsKey(UnrealId)) throw new SourceException($"release {tag} has no Luma-{UnrealId}.zip");

        var section = new LumaSection { Release = tag };
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { UnrealId };
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in parsed.Mods)
        {
            var id = Names.Slug(row.Name);
            if (!ids.Add(id)) { warnings.Add($"Luma wiki lists {row.Name} twice; kept the first row"); continue; }
            var package = PackageFor(row, zips, warnings);
            if (package is not null) used.Add(package);
            section.Mods.Add(new LumaMod
            {
                Id = id, Name = row.Name, Authors = row.Authors, Status = row.Status, Notes = row.Notes,
                Links = row.Links.IsEmpty ? null : row.Links, Package = package, RequiresDgVoodoo = row.RequiresDgVoodoo,
                DgVoodooVersions = row.DgVoodooVersions, DgVoodooSetup = row.DgVoodooSetup, FeaturesUrl = row.FeaturesUrl,
            });
        }
        foreach (var stem in zips.Keys.Where(s => !NotAGame.Contains(s) && !used.Contains(s)))
        {
            var name = stem.Replace('_', ' ');
            if (!ids.Add(Names.Slug(name))) continue;
            used.Add(stem);
            section.Mods.Add(new LumaMod { Id = Names.Slug(name), Name = name, Package = stem });
        }

        foreach (var game in parsed.UnrealGames)
            if (section.UnrealGames.All(g => Names.Normalize(g.Name) != Names.Normalize(game.Name))) section.UnrealGames.Add(game);

        var built = await SyncPackagesAsync(zips, used, previous, now, warnings, errors, ct);
        section.Packages.AddRange(built);
        if (section.Packages.All(p => p.Id != UnrealId)) throw new SourceException($"Luma-{UnrealId}.zip could not be downloaded");
        var live = section.Packages.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in section.Mods.Where(m => m.Package is not null && !live.Contains(m.Package))) mod.Package = null;
        section.License = section.Packages.SelectMany(p => p.Files).Select(f => f.License).FirstOrDefault(l => l is not null);
        section.Sources.Add(new SourceInfo { Url = WikiUrl, Count = parsed.Mods.Count + parsed.UnrealGames.Count });
        section.Sources.Add(new SourceInfo { Url = ReleaseApi, Count = zips.Count });
        return new LumaOutput(section, warnings, errors);
    }

    private static string? PackageFor(LumaWikiMod row, Dictionary<string, ZipSet> zips, List<string> warnings)
    {
        if (row.Asset is not null)
        {
            if (ParseAsset(row.Asset) is { Stem: var stem, Test: false } && zips.ContainsKey(stem)) return zips.Keys.First(k => k.Equals(stem, StringComparison.OrdinalIgnoreCase));
            warnings.Add($"{row.Name}: the wiki links {row.Asset}, which the latest release does not have");
            return null;
        }
        var wanted = Names.Normalize(row.Name);
        return zips.Keys.FirstOrDefault(k => !NotAGame.Contains(k) && Names.Normalize(k) == wanted);
    }

    private async Task<List<LumaPackage>> SyncPackagesAsync(
        Dictionary<string, ZipSet> zips, HashSet<string> used, LumaSection? previous, DateTimeOffset now, List<string> warnings, List<string> errors, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(parallel);
        var jobs = zips.Where(z => used.Contains(z.Key)).Select(async z =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var package = new LumaPackage { Id = z.Key };
                var before = previous?.Packages.FirstOrDefault(p => p.Id == z.Key);
                var local = (Warnings: new List<string>(), Errors: new List<string>());
                foreach (var (name, url, arch) in z.Value.Files())
                {
                    var prev = before?.Files.FirstOrDefault(f => f.Name == name)?.Clone();
                    // The release tag is part of the address: a new tag is a new address for the same asset.
                    if (prev is not null && prev.Url != url) { prev.ETag = null; prev.LastModified = null; }
                    var result = await sync.SyncAsync(new FileEntry { Name = name, Arch = arch, Url = url, Mod = "luma" }, prev, now, ct);
                    if (result.Warning is not null) local.Warnings.Add(result.Warning);
                    if (result.Error is not null) local.Errors.Add(result.Error);
                    if (result.Entry is { } file) package.Files.Add(file);
                }
                return (Package: package, local.Warnings, local.Errors);
            }
            finally { gate.Release(); }
        }).ToList();

        var built = new List<LumaPackage>();
        foreach (var job in await Task.WhenAll(jobs))
        {
            warnings.AddRange(job.Warnings);
            errors.AddRange(job.Errors);
            if (job.Package.Files.Count > 0) built.Add(job.Package);
        }
        return built;
    }

    private async Task<LumaWiki> ReadWikiAsync(CancellationToken ct)
    {
        string text;
        try { text = await wiki.GetStringAsync(WikiUrl, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SourceException($"Luma wiki: {ex.Message}");
        }
        var parsed = LumaWikiParser.Parse(text);
        if (parsed.Mods.Count < minMods || parsed.UnrealGames.Count < minUnrealGames)
            throw new SourceException($"Luma wiki: only {parsed.Mods.Count} mods and {parsed.UnrealGames.Count} Unreal games parsed, the page looks truncated");
        return parsed;
    }

    private async Task<(string Tag, Dictionary<string, ZipSet> Zips)> ReadReleaseAsync(CancellationToken ct)
    {
        string json;
        try { json = await api.GetStringAsync(ReleaseApi, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SourceException($"Luma release: {ex.Message}");
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) throw new SourceException("the Luma release has no tag");
            var zips = new Dictionary<string, ZipSet>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var a in list.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("name", out var n) || n.GetString() is not { } name
                        || !a.TryGetProperty("browser_download_url", out var u) || u.GetString() is not { } url
                        || !url.StartsWith("https://", StringComparison.Ordinal)) continue;
                    if (ParseAsset(name) is not { Test: false } asset) continue;
                    if (!zips.TryGetValue(asset.Stem, out var set)) zips[asset.Stem] = set = new ZipSet();
                    if (asset.X32) set.X86 = (name, url); else set.X64 = (name, url);
                }
            return (tag!, zips);
        }
        catch (JsonException ex) { throw new SourceException($"Luma release: {ex.Message}"); }
    }

    public sealed record Asset(string Stem, bool X32, bool Test);

    /// <summary>"Luma-Prey-x32.zip" is the 32-bit zip of "Prey"; "-Test" and "-Dev" builds are flagged. Null for a name that is not a Luma zip.</summary>
    public static Asset? ParseAsset(string name)
    {
        var m = AssetName().Match(name);
        if (!m.Success) return null;
        return new Asset(m.Groups["stem"].Value, m.Groups["x32"].Success, m.Groups["kind"].Success);
    }

    [GeneratedRegex(@"^Luma-(?<stem>.+?)(?<kind>-(?:Test|Dev))?(?<x32>-x32)?\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex AssetName();

    public sealed class ZipSet
    {
        public (string Name, string Url)? X64 { get; set; }
        public (string Name, string Url)? X86 { get; set; }

        public IEnumerable<(string Name, string Url, string Arch)> Files()
        {
            if (X64 is { } a) yield return (a.Name, a.Url, "x64");
            if (X86 is { } b) yield return (b.Name, b.Url, "x86");
        }
    }
}
