using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace CatalogGen;

public sealed record AddonsOutput(AddonsSection Section, List<string> Warnings, List<string> Errors);

/// <summary>
/// Builds the <c>addons</c> section from crosire's Addons.ini: every package with a download is fetched, hashed and
/// mirrored or pinned by <see cref="FileSync"/> (permissive licence = mirror), and zips are read to record what the app
/// installs from them (<see cref="AddonPayload"/>). Packages the app already manages as components are marked, not listed.
/// </summary>
public sealed partial class AddonsGenerator(ITextSource text, FileSync sync, int parallelism = 4, bool acceptShrink = false, int minPackages = 10)
{
    public async Task<AddonsOutput> BuildAsync(AddonsSection? previous, DateTimeOffset now, CancellationToken ct)
    {
        List<AddonSource> sources;
        try { sources = AddonsIni.Parse(await text.GetStringAsync(AddonsIni.Url, ct)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SourceException($"Addons.ini: {ex.Message}");
        }
        if (sources.Count < minPackages) throw new SourceException($"Addons.ini lists only {sources.Count} packages, looks truncated");
        if (!acceptShrink && previous is { Addons.Count: > 0 } && sources.Count < previous.Addons.Count * 0.8)
            throw new SourceException($"{sources.Count} packages now against {previous.Addons.Count} before (more than 20% gone); set CATALOG_ACCEPT_SHRINK=1 if upstream really removed them");

        var warnings = new List<string>();
        var errors = new List<string>();
        var entries = new List<(AddonEntry Entry, List<FileEntry> Wanted)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sources) entries.Add(Describe(s, ids, warnings));

        var previousByUrl = (previous?.Addons ?? []).SelectMany(a => a.Files).GroupBy(f => f.Url)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var urls = entries.SelectMany(e => e.Wanted).GroupBy(f => f.Url).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var effectPaths = entries.SelectMany(e => e.Wanted.Select(f => (f.Url, e.Entry.EffectInstallPath)))
            .GroupBy(x => x.Url).ToDictionary(g => g.Key, g => g.First().EffectInstallPath, StringComparer.Ordinal);
        var results = new ConcurrentDictionary<string, SyncResult>(StringComparer.Ordinal);
        var gate = new SemaphoreSlim(Math.Max(1, parallelism));

        await Task.WhenAll(urls.Select(async kv =>
        {
            await gate.WaitAsync(ct);
            try
            {
                previousByUrl.TryGetValue(kv.Key, out var prev);
                // A build recorded before payloads existed is read again.
                if (prev is { Payload: null }) { prev = prev.Clone(); prev.Verified = null; }
                var want = kv.Value;
                results[kv.Key] = await sync.SyncAsync(want, prev, now, ct,
                    (path, entry) => Inspect(path, entry, want.Arch, effectPaths[want.Url]));
            }
            finally { gate.Release(); }
        }));

        var section = new AddonsSection();
        foreach (var (entry, wanted) in entries)
        {
            foreach (var w in wanted)
            {
                var r = results[w.Url];
                if (r.Warning is not null && !warnings.Contains(r.Warning)) warnings.Add(r.Warning);
                if (r.Error is not null && !errors.Contains(r.Error)) errors.Add(r.Error);
                if (r.Entry is not { } e) continue;
                var copy = e.Clone();
                copy.Name = w.Name;
                copy.Arch = w.Arch;
                entry.Files.Add(copy);
            }
            entry.License = entry.Files.Select(f => f.License).FirstOrDefault(l => l is not null);
            if (entry.ManagedBy is null) entry.Manual = ManualReason(entry, wanted.Count);
            section.Addons.Add(entry);
        }
        return new AddonsOutput(section, warnings, errors);
    }

    private static (AddonEntry Entry, List<FileEntry> Wanted) Describe(AddonSource s, HashSet<string> ids, List<string> warnings)
    {
        var (name, author) = SplitName(s.PackageName);
        var entry = new AddonEntry
        {
            Id = UniqueId(Slug(s.PackageName), ids),
            Name = name,
            Author = author,
            Description = s.Description.Length == 0 ? null : s.Description,
            RepositoryUrl = s.Repository,
            EffectInstallPath = s.EffectInstallPath,
            ManagedBy = ManagedComponents.For(s),
        };
        var wanted = new List<FileEntry>();
        if (entry.ManagedBy is not null) return (entry, wanted);

        void Add(string? url, string arch)
        {
            if (url is null) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                warnings.Add($"{s.PackageName}: download address \"{url}\" is not an https URL; skipped");
                return;
            }
            var existing = wanted.FirstOrDefault(f => f.Url == url);
            if (existing is not null) existing.Arch = "both";
            else wanted.Add(new FileEntry { Name = Uri.UnescapeDataString(uri.Segments[^1]), Arch = arch, Url = url });
        }
        Add(s.Url, "both");
        Add(s.Url32, "x86");
        Add(s.Url64, "x64");
        return (entry, wanted);
    }

    private static void Inspect(string path, FileEntry entry, string declaredArch, string? effectInstallPath)
    {
        var head = new byte[4];
        int read;
        using (var fs = File.OpenRead(path)) read = fs.Read(head, 0, 4);
        if (read >= 4 && head[0] == 'P' && head[1] == 'K')
        {
            AddonPayload.Result result;
            try { result = AddonPayload.AnalyseZip(path, effectInstallPath, declaredArch); }
            catch (InvalidDataException ex) { throw new InvalidDataException($"not a readable zip ({ex.Message})"); }
            entry.Payload = result.Items;
            entry.Unplaced = result.Unplaced.Count == 0 ? null : result.Unplaced;
        }
        else
        {
            entry.Payload = AddonPayload.ForBinary(entry.Name, declaredArch) is { } item ? [item] : [];
            entry.Unplaced = null;
        }
    }

    private static string? ManualReason(AddonEntry e, int wanted)
    {
        if (wanted == 0) return "No download is published for this package; get it from the repository.";
        if (e.Files.Count == 0) return "The download could not be read (see the catalog log); get it from the repository.";
        var unplaced = e.Files.SelectMany(f => f.Unplaced ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unplaced.Count > 0)
            return $"The package holds files this app does not place ({string.Join(", ", unplaced.Take(3))}{(unplaced.Count > 3 ? ", ..." : "")}); install it by hand from the repository.";
        if (e.Files.All(f => f.Payload is not { Count: > 0 }))
            return "The download holds no add-on or effect file the app recognises; install it by hand from the repository.";
        return null;
    }

    public static (string Name, string? Author) SplitName(string packageName)
    {
        var i = packageName.IndexOf(" by ", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? (packageName[..i].Trim(), packageName[(i + 4)..].Trim()) : (packageName.Trim(), null);
    }

    public static string Slug(string name) => NonAlnum().Replace(name.ToLowerInvariant(), "-").Trim('-');

    private static string UniqueId(string slug, HashSet<string> taken)
    {
        var id = slug.Length == 0 ? "addon" : slug;
        for (var n = 2; !taken.Add(id); n++) id = $"{slug}-{n}";
        return id;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnum();
}
