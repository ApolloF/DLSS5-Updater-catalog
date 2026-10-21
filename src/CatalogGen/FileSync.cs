namespace CatalogGen;

public abstract record FetchResult
{
    /// <summary>The server said the file hasn't changed (304, or the same ETag and size).</summary>
    public sealed record Unchanged(string? ETag, string? LastModified) : FetchResult;

    /// <summary>Downloaded to <paramref name="Path"/>, which the caller deletes.</summary>
    public sealed record Fetched(string Path, string Sha256, long Size, string? ETag, string? LastModified) : FetchResult;

    public sealed record Failed(string Reason) : FetchResult;
}

public interface IFetcher
{
    /// <summary>With <paramref name="previous"/> the request is conditional; without it the file is always downloaded.</summary>
    Task<FetchResult> FetchAsync(string url, FileEntry? previous, CancellationToken ct);
}

public interface ILicenceSource
{
    /// <summary>SPDX id of <paramref name="repo"/> ("owner/repo"), null when it has none or the repo doesn't exist; throws when the lookup itself failed.</summary>
    Task<string?> SpdxAsync(string repo, CancellationToken ct);
}

public interface IMirror
{
    /// <summary>Stores the file as an immutable release asset named by its hash and returns its download url. Existing assets are never overwritten.</summary>
    Task<string> PublishAsync(string fileName, string sha256, string month, string path, CancellationToken ct);

    /// <summary>Sets the licence notices of the monthly release. The release's intro paragraph stays; nothing is written when the text is already there.</summary>
    Task SetNotesAsync(string month, string body, CancellationToken ct);
}

/// <summary>A source repository's licence file as GitHub reports it.</summary>
public sealed record LicenceNotice(string? Spdx, string? Url, string Text);

public interface INoticeSource
{
    /// <summary>The licence file of <paramref name="repo"/> ("owner/repo"), null when it has none; throws when the lookup itself failed.</summary>
    Task<LicenceNotice?> NoticeAsync(string repo, CancellationToken ct);
}

public sealed record SyncResult(FileEntry? Entry, string? Warning, string? Error);

/// <summary>Reads a freshly downloaded file (a zip) and records what it holds on the entry. Throws <see cref="InvalidDataException"/> when the file is unusable.</summary>
public delegate void FileInspector(string path, FileEntry entry);

/// <summary>
/// Brings one file entry up to date: conditional re-download, SHA-256, first-seen version and history,
/// licence decision and mirroring. A failure keeps the last good entry.
/// </summary>
public sealed class FileSync(IFetcher fetcher, ILicenceSource licences, IMirror? mirror, int verifyEveryDays = 7, int historyLimit = 20)
{
    public async Task<SyncResult> SyncAsync(FileEntry wanted, FileEntry? previous, DateTimeOffset now, CancellationToken ct, FileInspector? inspect = null)
    {
        string? error = null;
        var license = previous?.License;
        if (Licence.RepoOf(wanted.Url) is { } repo)
        {
            try { license = await licences.SpdxAsync(repo, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                error = $"licence lookup for {repo} failed ({ex.Message}); keeping '{previous?.License ?? "none"}'";
            }
        }

        var mirrorAllowed = mirror is not null && Licence.AllowsMirror(license);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var due = !DateOnly.TryParse(previous?.Verified, out var last) || today.DayNumber - last.DayNumber >= verifyEveryDays;
        var force = previous?.Sha256 is null || due || (mirrorAllowed && previous.MirrorUrl is null);

        var result = await fetcher.FetchAsync(wanted.Url, force ? null : previous, ct);
        switch (result)
        {
            case FetchResult.Failed failed:
                var reason = $"{wanted.Url}: {failed.Reason}";
                return previous is null
                    ? new SyncResult(null, $"skipped, {reason}", error)
                    : new SyncResult(Describe(previous, wanted, license), $"kept the last good build, {reason}", error);

            case FetchResult.Unchanged same:
                var kept = Describe(previous!, wanted, license);
                kept.ETag = same.ETag ?? kept.ETag;
                kept.LastModified = same.LastModified ?? kept.LastModified;
                return new SyncResult(kept, null, error);

            case FetchResult.Fetched fetched:
                try
                {
                    var entry = Merge(wanted, previous, fetched, license, now);
                    if (inspect is not null)
                    {
                        try { inspect(fetched.Path, entry); }
                        catch (InvalidDataException ex)
                        {
                            var why = $"{wanted.Url}: {ex.Message}";
                            return previous is null
                                ? new SyncResult(null, $"skipped, {why}", error)
                                : new SyncResult(Describe(previous, wanted, license), $"kept the last good build, {why}", error);
                        }
                    }
                    if (mirrorAllowed && entry.MirrorUrl is null)
                    {
                        try
                        {
                            entry.MirrorUrl = await mirror!.PublishAsync(entry.Name, entry.Sha256!, MonthOf(entry.FirstSeen!), fetched.Path, ct);
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
                        {
                            error = $"mirroring {entry.Name} failed: {ex.Message}";
                        }
                    }
                    else if (!mirrorAllowed) entry.MirrorUrl = null;
                    return new SyncResult(entry, null, error);
                }
                finally { TryDelete(fetched.Path); }

            default:
                throw new InvalidOperationException("unknown fetch result");
        }
    }

    private static FileEntry Describe(FileEntry previous, FileEntry wanted, string? license)
    {
        var e = previous.Clone();
        e.Name = wanted.Name;
        e.Arch = wanted.Arch;
        e.Url = wanted.Url;
        e.Mod = wanted.Mod;
        e.License = license;
        if (!Licence.AllowsMirror(license)) e.MirrorUrl = null;
        return e;
    }

    private FileEntry Merge(FileEntry wanted, FileEntry? previous, FetchResult.Fetched f, string? license, DateTimeOffset now)
    {
        var stamp = Iso(now);
        var entry = previous?.Clone() ?? new FileEntry();
        entry.Name = wanted.Name;
        entry.Arch = wanted.Arch;
        entry.Url = wanted.Url;
        entry.Mod = wanted.Mod;
        entry.License = license;
        entry.ETag = f.ETag;
        entry.LastModified = f.LastModified;
        entry.Size = f.Size;
        entry.Verified = now.UtcDateTime.ToString("yyyy-MM-dd");

        if (previous?.Sha256 == f.Sha256) return entry;

        if (previous?.Sha256 is not null && previous.Version is not null && previous.FirstSeen is not null)
            entry.History.Insert(0, new HistoryEntry(previous.Version, previous.Sha256, previous.Size, previous.FirstSeen, previous.MirrorUrl));

        // A build we have seen before (upstream went back to it) keeps its original date and its mirror copy.
        var seen = entry.History.FirstOrDefault(h => h.Sha256 == f.Sha256);
        if (seen is not null)
        {
            entry.History.Remove(seen);
            entry.Sha256 = seen.Sha256;
            entry.Version = seen.Version;
            entry.FirstSeen = seen.FirstSeen;
            entry.MirrorUrl = seen.MirrorUrl;
        }
        else
        {
            entry.Sha256 = f.Sha256;
            entry.FirstSeen = stamp;
            entry.Version = VersionOf(now, f.Sha256);
            entry.MirrorUrl = null;
        }
        if (entry.History.Count > historyLimit) entry.History.RemoveRange(historyLimit, entry.History.Count - historyLimit);
        return entry;
    }

    /// <summary>Upstream snapshots carry no version, so a build is named by when we first saw it plus its hash.</summary>
    public static string VersionOf(DateTimeOffset firstSeen, string sha256) =>
        $"{firstSeen.UtcDateTime:yyyy-MM-dd}-{sha256[..7]}";

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static string MonthOf(string firstSeen) => firstSeen[..7];

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
