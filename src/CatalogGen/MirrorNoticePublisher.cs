using System.Text.RegularExpressions;

namespace CatalogGen;

/// <summary>
/// Puts the licence and copyright notices of every mirrored file into the notes of its monthly release.
/// Failures are reported, never thrown: the catalog is written either way and the next run tries again.
/// </summary>
public sealed partial class MirrorNoticePublisher(INoticeSource notices, IMirror mirror)
{
    private sealed record Mirrored(string Month, string Repo, string AssetName);

    public async Task PublishAsync(IEnumerable<FileEntry> files, List<string> errors, CancellationToken ct)
    {
        var mirrored = Collect(files);
        if (mirrored.Count == 0) return;

        var found = new Dictionary<string, LicenceNotice>(StringComparer.OrdinalIgnoreCase);
        var broken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in mirrored.Select(m => m.Repo).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (await notices.NoticeAsync(repo, ct) is { } notice) found[repo] = notice;
                else { broken.Add(repo); errors.Add($"licence notice for {repo}: the repository has no licence file"); }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
            {
                broken.Add(repo);
                errors.Add($"licence notice for {repo} could not be read: {ex.Message}");
            }
        }

        foreach (var month in mirrored.GroupBy(m => m.Month).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var repos = month.GroupBy(m => m.Repo, StringComparer.OrdinalIgnoreCase).ToList();
            if (repos.Any(g => broken.Contains(g.Key)))
            {
                errors.Add($"notices of mirror-{month.Key} not updated: a licence could not be read");
                continue;
            }
            var body = MirrorNotes.Build(repos.Select(g =>
            {
                var n = found[g.Key];
                return new RepoNotes(g.Key, n.Spdx, n.Url, MirrorNotes.CopyrightLines(n.Text), g.Select(m => m.AssetName).Distinct(StringComparer.Ordinal).ToList());
            }));
            try { await mirror.SetNotesAsync(month.Key, body, ct); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
            {
                errors.Add($"notices of mirror-{month.Key} could not be written: {ex.Message}");
            }
        }
    }

    /// <summary>Every mirror copy: the current build and the history entries that kept one.</summary>
    private static List<Mirrored> Collect(IEnumerable<FileEntry> files)
    {
        var all = new List<Mirrored>();
        foreach (var f in files)
        {
            if (Licence.RepoOf(f.Url) is not { } repo) continue;
            foreach (var url in f.History.Select(h => h.MirrorUrl).Prepend(f.MirrorUrl))
                if (url is not null && MonthOf(url) is { } month)
                    all.Add(new Mirrored(month, repo, AssetNameOf(url)));
        }
        return all;
    }

    private static string? MonthOf(string mirrorUrl) => MonthRule().Match(mirrorUrl) is { Success: true } m ? m.Groups[1].Value : null;

    private static string AssetNameOf(string url) => Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]);

    [GeneratedRegex(@"/mirror-(\d{4}-\d{2})/")]
    private static partial Regex MonthRule();
}
