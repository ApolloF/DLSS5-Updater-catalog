using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CatalogGen;

public sealed record ShaderPacksOutput(ShaderPacksSection Section, List<string> Warnings, List<string> Errors);

/// <summary>
/// Builds the <c>shaderPacks</c> section from shader-packs.json: each pack is pinned to a release tag or a commit, its zip is
/// downloaded, hashed and mirrored (permissive licence) or pinned by <see cref="FileSync"/>, and the zip's Shaders and
/// Textures folders are recorded. A commit zip is fetched by commit id (<c>/archive/&lt;sha&gt;.zip</c>), so what was hashed
/// is exactly what the URL names.
/// </summary>
public sealed class ShaderPacksGenerator(ITextSource github, FileSync sync, int parallelism = 4)
{
    private sealed record Resolved(PackSource Source, string Ref, string Version, string? Commit, FileEntry Wanted);

    public async Task<ShaderPacksOutput> BuildAsync(ShaderPackList list, ShaderPacksSection? previous, DateTimeOffset now, CancellationToken ct)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var previousById = (previous?.Packs ?? []).ToDictionary(p => p.Id, StringComparer.Ordinal);

        var resolved = new ConcurrentDictionary<string, Resolved>(StringComparer.Ordinal);
        var gate = new SemaphoreSlim(Math.Max(1, parallelism));
        var results = new ConcurrentDictionary<string, SyncResult>(StringComparer.Ordinal);

        await Task.WhenAll(list.Packs.Select(async p =>
        {
            await gate.WaitAsync(ct);
            try
            {
                Resolved r;
                try { r = await ResolveAsync(p, ct); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException && !ct.IsCancellationRequested)
                {
                    lock (warnings) warnings.Add($"{p.Id}: {ex.Message}");
                    return;
                }
                resolved[p.Id] = r;
                previousById.TryGetValue(p.Id, out var before);
                var prev = before?.File?.Clone();
                if (prev is not null && prev.Url != r.Wanted.Url) { prev.ETag = null; prev.LastModified = null; }
                // A build recorded before layouts existed is read again.
                if (prev is { Layout: null }) prev.Verified = null;
                results[p.Id] = await sync.SyncAsync(r.Wanted, prev, now, ct, Inspect);
            }
            finally { gate.Release(); }
        }));

        var section = new ShaderPacksSection();
        foreach (var p in list.Packs)
        {
            previousById.TryGetValue(p.Id, out var before);
            if (!results.TryGetValue(p.Id, out var result) || !resolved.TryGetValue(p.Id, out var r))
            {
                // Could not even resolve: the last good pack stays, a pack never seen is left out.
                if (before is not null) section.Packs.Add(Describe(p, before));
                continue;
            }
            if (result.Warning is not null) warnings.Add(result.Warning);
            if (result.Error is not null) errors.Add(result.Error);
            if (result.Entry is not { } file)
            {
                if (before is not null) section.Packs.Add(Describe(p, before));
                continue;
            }
            // The zip we kept belongs to the ref we kept: when the new download failed, the old ref and layout stay with it.
            var kept = before is not null && before.File is { } old && old.Sha256 == file.Sha256 && old.Url != r.Wanted.Url;
            var pack = kept ? Describe(p, before!) : new ShaderPack
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                Repo = p.Repo,
                Homepage = $"https://github.com/{p.Repo}",
                Category = p.Category,
                Default = p.Default,
                Source = p.Source.Kind,
                Ref = r.Ref,
                Commit = r.Commit,
                Version = r.Version,
                License = file.License ?? p.License,
                File = file,
            };
            if (file.License is not null && p.License is not null && !file.License.Equals(p.License, StringComparison.OrdinalIgnoreCase))
                warnings.Add($"{p.Id}: shader-packs.json says {p.License}, GitHub says {file.License}; the catalog follows GitHub");
            section.Packs.Add(pack);
        }
        if (section.Packs.Count == 0) throw new SourceException("no shader pack could be built");
        return new ShaderPacksOutput(section, warnings, errors);
    }

    private static ShaderPack Describe(PackSource p, ShaderPack before)
    {
        before.Name = p.Name;
        before.Description = p.Description;
        before.Category = p.Category;
        before.Default = p.Default;
        before.License ??= p.License;
        return before;
    }

    private async Task<Resolved> ResolveAsync(PackSource p, CancellationToken ct)
    {
        var repoName = p.Repo[(p.Repo.IndexOf('/') + 1)..];
        if (p.Source.Kind == "branch")
        {
            using var doc = JsonDocument.Parse(await github.GetStringAsync($"https://api.github.com/repos/{p.Repo}/commits/{Uri.EscapeDataString(p.Source.Branch!)}", ct));
            var sha = Str(doc.RootElement, "sha");
            if (sha is null || !Sha().IsMatch(sha)) throw new InvalidDataException($"{p.Repo}: branch {p.Source.Branch} has no commit");
            var wanted = new FileEntry { Name = $"{repoName}-{sha[..7]}.zip", Arch = "both", Url = $"https://github.com/{p.Repo}/archive/{sha}.zip", Mod = p.Id };
            return new Resolved(p, p.Source.Branch!, sha[..7], sha, wanted);
        }

        using var rel = JsonDocument.Parse(await github.GetStringAsync($"https://api.github.com/repos/{p.Repo}/releases/latest", ct));
        var tag = Str(rel.RootElement, "tag_name") ?? throw new InvalidDataException($"{p.Repo}: the latest release has no tag");
        var pattern = GlobToRegex(string.IsNullOrWhiteSpace(p.Source.Asset) ? "*.zip" : p.Source.Asset);
        var assets = rel.RootElement.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => Str(x, "name") is { } n && pattern.IsMatch(n)).ToList()
            : [];
        if (assets.Count != 1) throw new InvalidDataException($"{p.Repo}: release {tag} has {assets.Count} assets matching {p.Source.Asset ?? "*.zip"}, expected one");
        var name = Str(assets[0], "name")!;
        var url = Str(assets[0], "browser_download_url") ?? throw new InvalidDataException($"{p.Repo}: asset {name} has no download url");
        return new Resolved(p, tag, tag, null, new FileEntry { Name = name, Arch = "both", Url = url, Mod = p.Id });
    }

    public static Regex GlobToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static readonly Regex ShaRule = new("^[0-9a-f]{40}$", RegexOptions.Compiled);
    private static Regex Sha() => ShaRule;

    private static void Inspect(string path, FileEntry entry)
    {
        try { entry.Layout = PackLayoutScan.AnalyseZip(path); }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidDataException(ex.Message); }
    }
}
