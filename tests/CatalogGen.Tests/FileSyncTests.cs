using CatalogGen;

namespace CatalogGen.Tests;

public class FileSyncTests : IDisposable
{
    private const string Url = "https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-x.addon64";
    private static readonly DateTimeOffset Day1 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cg-" + Guid.NewGuid().ToString("N"));

    public FileSyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class FakeFetcher(string dir) : IFetcher
    {
        public Func<FileEntry?, FetchResult>? Next;
        public List<FileEntry?> Requests { get; } = [];

        public Task<FetchResult> FetchAsync(string url, FileEntry? previous, CancellationToken ct)
        {
            Requests.Add(previous);
            return Task.FromResult(Next!(previous));
        }

        public FetchResult.Fetched File(string sha, long size = 10, string? etag = "\"e1\"")
        {
            var path = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            System.IO.File.WriteAllText(path, "MZ");
            return new FetchResult.Fetched(path, sha, size, etag, "Wed, 21 Oct 2026 10:00:00 GMT");
        }
    }

    private sealed class FakeLicences(string? spdx) : ILicenceSource
    {
        public bool Throw;
        public int Calls;

        public Task<string?> SpdxAsync(string repo, CancellationToken ct)
        {
            Calls++;
            if (Throw) throw new HttpRequestException("rate limited");
            return Task.FromResult(spdx);
        }
    }

    private sealed class FakeMirror : IMirror
    {
        public bool Fail;
        public List<(string Name, string Sha, string Month)> Uploads { get; } = [];

        public Task<string> PublishAsync(string fileName, string sha256, string month, string path, CancellationToken ct)
        {
            if (Fail) throw new HttpRequestException("upload failed");
            Uploads.Add((fileName, sha256, month));
            return Task.FromResult($"https://mirror.test/mirror-{month}/{sha256[..16]}-{fileName}");
        }
    }

    private static string Sha(char c) => new(c, 64);

    private static FileEntry Wanted() => new() { Name = "renodx-x.addon64", Arch = "x64", Url = Url, Mod = "x" };

    private (FileSync Sync, FakeFetcher Fetcher, FakeMirror Mirror, FakeLicences Licences) Make(string? spdx = "MIT")
    {
        var fetcher = new FakeFetcher(_dir);
        var mirror = new FakeMirror();
        var licences = new FakeLicences(spdx);
        return (new FileSync(fetcher, licences, mirror), fetcher, mirror, licences);
    }

    [Fact]
    public async Task A_new_file_gets_its_first_seen_date_and_a_date_plus_hash_version()
    {
        var (sync, fetcher, _, _) = Make();
        fetcher.Next = _ => fetcher.File(Sha('a'));

        var r = await sync.SyncAsync(Wanted(), null, Day1, default);

        var e = r.Entry!;
        Assert.Equal(Sha('a'), e.Sha256);
        Assert.Equal("2026-10-21-aaaaaaa", e.Version);
        Assert.Equal("2026-10-21T12:00:00Z", e.FirstSeen);
        Assert.Equal("2026-10-21", e.Verified);
        Assert.Equal(10, e.Size);
        Assert.Equal("\"e1\"", e.ETag);
        Assert.Empty(e.History);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task A_permissive_licence_mirrors_the_file_into_the_month_of_its_first_sighting()
    {
        var (sync, fetcher, mirror, _) = Make("MIT");
        fetcher.Next = _ => fetcher.File(Sha('a'));

        var e = (await sync.SyncAsync(Wanted(), null, Day1, default)).Entry!;

        Assert.Equal("MIT", e.License);
        Assert.Equal("https://mirror.test/mirror-2026-10/aaaaaaaaaaaaaaaa-renodx-x.addon64", e.MirrorUrl);
        Assert.Equal([("renodx-x.addon64", Sha('a'), "2026-10")], mirror.Uploads);
    }

    [Theory]
    [InlineData("GPL-3.0")]
    [InlineData("NOASSERTION")]
    [InlineData(null)]
    public async Task Other_licences_are_pinned_to_the_upstream_url_and_never_uploaded(string? spdx)
    {
        var (sync, fetcher, mirror, _) = Make(spdx);
        fetcher.Next = _ => fetcher.File(Sha('a'));

        var e = (await sync.SyncAsync(Wanted(), null, Day1, default)).Entry!;

        Assert.Null(e.MirrorUrl);
        Assert.Equal(spdx, e.License);
        Assert.Equal(Url, e.Url);
        Assert.Empty(mirror.Uploads);
        Assert.Equal(Sha('a'), e.Sha256);
    }

    [Fact]
    public async Task A_url_we_cannot_attribute_to_a_repository_is_pinned()
    {
        var (sync, fetcher, mirror, licences) = Make("MIT");
        fetcher.Next = _ => fetcher.File(Sha('a'));
        var wanted = Wanted();
        wanted.Url = "https://example.com/renodx-x.addon64";

        var e = (await sync.SyncAsync(wanted, null, Day1, default)).Entry!;

        Assert.Null(e.License);
        Assert.Null(e.MirrorUrl);
        Assert.Empty(mirror.Uploads);
        Assert.Equal(0, licences.Calls);
    }

    [Fact]
    public async Task An_unchanged_file_is_a_conditional_request_and_keeps_everything()
    {
        var (sync, fetcher, _, _) = Make();
        fetcher.Next = _ => fetcher.File(Sha('a'));
        var first = (await sync.SyncAsync(Wanted(), null, Day1, default)).Entry!;
        fetcher.Next = _ => new FetchResult.Unchanged("\"e1\"", null);

        var r = await sync.SyncAsync(Wanted(), first, Day1.AddDays(2), default);

        Assert.Null(fetcher.Requests[0]);
        Assert.Same(first, fetcher.Requests[1]);
        Assert.Equal(first.Sha256, r.Entry!.Sha256);
        Assert.Equal(first.Version, r.Entry.Version);
        Assert.Equal(first.FirstSeen, r.Entry.FirstSeen);
        Assert.Equal(first.Verified, r.Entry.Verified);
        Assert.Equal(first.MirrorUrl, r.Entry.MirrorUrl);
        Assert.Equal(2, fetcher.Requests.Count);
    }

    [Fact]
    public async Task A_file_not_fully_verified_for_a_week_is_downloaded_again_without_conditions()
    {
        var (sync, fetcher, _, _) = Make();
        fetcher.Next = _ => fetcher.File(Sha('a'));
        var first = (await sync.SyncAsync(Wanted(), null, Day1, default)).Entry!;

        await sync.SyncAsync(Wanted(), first, Day1.AddDays(6), default);
        await sync.SyncAsync(Wanted(), first, Day1.AddDays(7), default);

        Assert.NotNull(fetcher.Requests[1]);
        Assert.Null(fetcher.Requests[2]);
    }

    [Fact]
    public async Task Same_hash_on_a_weekly_check_only_moves_the_verified_date()
    {
        var (sync, fetcher, mirror, _) = Make();
        fetcher.Next = _ => fetcher.File(Sha('a'));
        var first = (await sync.SyncAsync(Wanted(), null, Day1, default)).Entry!;

        var again = (await sync.SyncAsync(Wanted(), first, Day1.AddDays(8), default)).Entry!;

        Assert.Equal("2026-10-29", again.Verified);
        Assert.Equal(first.Version, again.Version);
        Assert.Equal(first.FirstSeen, again.FirstSeen);
        Assert.Empty(again.History);
        Assert.Single(mirror.Uploads);
    }

    [Fact]
    public async Task A_new_hash_becomes_the_current_build_and_the_old_one_goes_to_history()
    {
        var (sync, fetcher, mirror, _) = Make();
        fetcher.Next = _ => fetcher.File(Sha('a'));
        var first = (await sync.SyncAsync(Wanted(), null, Day1, default)).Entry!;
        fetcher.Next = _ => fetcher.File(Sha('b'), 20);

        var e = (await sync.SyncAsync(Wanted(), first, Day1.AddDays(40), default)).Entry!;

        Assert.Equal(Sha('b'), e.Sha256);
        Assert.Equal("2026-11-30-bbbbbbb", e.Version);
        Assert.Equal(20, e.Size);
        var old = Assert.Single(e.History);
        Assert.Equal(Sha('a'), old.Sha256);
        Assert.Equal("2026-10-21-aaaaaaa", old.Version);
        Assert.Equal(first.MirrorUrl, old.MirrorUrl);
        Assert.Equal("https://mirror.test/mirror-2026-11/bbbbbbbbbbbbbbbb-renodx-x.addon64", e.MirrorUrl);
        Assert.Equal(2, mirror.Uploads.Count);
    }

    [Fact]
    public async Task History_is_newest_first_and_capped()
    {
        var fetcher = new FakeFetcher(_dir);
        var sync = new FileSync(fetcher, new FakeLicences(null), null, historyLimit: 2);
        FileEntry? entry = null;
        var day = 0;
        foreach (var c in "abcd")
        {
            fetcher.Next = _ => fetcher.File(Sha(c));
            entry = (await sync.SyncAsync(Wanted(), entry, Day1.AddDays(day++), default)).Entry;
        }

        Assert.Equal(Sha('d'), entry!.Sha256);
        Assert.Equal([Sha('c'), Sha('b')], entry.History.Select(h => h.Sha256).ToArray());
    }

    [Fact]
    public async Task Upstream_going_back_to_an_older_build_restores_its_date_and_mirror_copy()
    {
        var (sync, fetcher, mirror, _) = Make();
        fetcher.Next = _ => fetcher.File(Sha('a'));
        var a = (await sync.SyncAsync(Wanted(), null, Day1, default)).Entry!;
        fetcher.Next = _ => fetcher.File(Sha('b'));
        var b = (await sync.SyncAsync(Wanted(), a, Day1.AddDays(1), default)).Entry!;
        fetcher.Next = _ => fetcher.File(Sha('a'));

        var back = (await sync.SyncAsync(Wanted(), b, Day1.AddDays(2), default)).Entry!;

        Assert.Equal(a.Version, back.Version);
        Assert.Equal(a.FirstSeen, back.FirstSeen);
        Assert.Equal(a.MirrorUrl, back.MirrorUrl);
        Assert.Equal([Sha('b')], back.History.Select(h => h.Sha256).ToArray());
        Assert.Equal(2, mirror.Uploads.Count);
    }

    [Fact]
    public async Task A_failed_download_keeps_the_last_good_build_with_a_warning()
    {
        var (sync, fetcher, _, _) = Make();
        fetcher.Next = _ => fetcher.File(Sha('a'));
        var first = (await sync.SyncAsync(Wanted(), null, Day1, default)).Entry!;
        fetcher.Next = _ => new FetchResult.Failed("HTTP 404");

        var r = await sync.SyncAsync(Wanted(), first, Day1.AddDays(10), default);

        Assert.Equal(first.Sha256, r.Entry!.Sha256);
        Assert.Equal(first.MirrorUrl, r.Entry.MirrorUrl);
        Assert.Contains("HTTP 404", r.Warning);
    }

    [Fact]
    public async Task A_failed_download_of_a_new_file_leaves_it_out()
    {
        var (sync, fetcher, _, _) = Make();
        fetcher.Next = _ => new FetchResult.Failed("not a Windows executable");

        var r = await sync.SyncAsync(Wanted(), null, Day1, default);

        Assert.Null(r.Entry);
        Assert.Contains("not a Windows executable", r.Warning);
    }

    [Fact]
    public async Task A_failed_upload_keeps_the_hash_without_a_mirror_url_and_reports_an_error_then_retries_next_run()
    {
        var (sync, fetcher, mirror, _) = Make();
        fetcher.Next = _ => fetcher.File(Sha('a'));
        mirror.Fail = true;

        var failed = await sync.SyncAsync(Wanted(), null, Day1, default);

        Assert.Equal(Sha('a'), failed.Entry!.Sha256);
        Assert.Null(failed.Entry.MirrorUrl);
        Assert.Contains("mirroring", failed.Error);

        mirror.Fail = false;
        var retried = await sync.SyncAsync(Wanted(), failed.Entry, Day1.AddHours(6), default);

        Assert.Null(fetcher.Requests[1]);
        Assert.NotNull(retried.Entry!.MirrorUrl);
        Assert.Null(retried.Error);
    }

    [Fact]
    public async Task A_failed_licence_lookup_keeps_the_previous_licence_and_reports_an_error()
    {
        var (sync, fetcher, mirror, licences) = Make("MIT");
        fetcher.Next = _ => fetcher.File(Sha('a'));
        var first = (await sync.SyncAsync(Wanted(), null, Day1, default)).Entry!;
        licences.Throw = true;
        fetcher.Next = _ => new FetchResult.Unchanged(null, null);

        var r = await sync.SyncAsync(Wanted(), first, Day1.AddDays(1), default);

        Assert.Equal("MIT", r.Entry!.License);
        Assert.Equal(first.MirrorUrl, r.Entry.MirrorUrl);
        Assert.Contains("licence lookup", r.Error);
        Assert.Single(mirror.Uploads);
    }

    [Fact]
    public async Task When_the_licence_stops_allowing_it_the_entry_is_pinned_again()
    {
        var fetcher = new FakeFetcher(_dir);
        var sync1 = new FileSync(fetcher, new FakeLicences("MIT"), new FakeMirror());
        fetcher.Next = _ => fetcher.File(Sha('a'));
        var first = (await sync1.SyncAsync(Wanted(), null, Day1, default)).Entry!;
        var sync2 = new FileSync(fetcher, new FakeLicences("GPL-3.0"), new FakeMirror());
        fetcher.Next = _ => new FetchResult.Unchanged(null, null);

        var e = (await sync2.SyncAsync(Wanted(), first, Day1.AddDays(1), default)).Entry!;

        Assert.Null(e.MirrorUrl);
        Assert.Equal("GPL-3.0", e.License);
        Assert.Equal(first.Sha256, e.Sha256);
    }

    [Fact]
    public async Task Without_a_mirror_every_file_is_pinned()
    {
        var fetcher = new FakeFetcher(_dir);
        var sync = new FileSync(fetcher, new FakeLicences("MIT"), null);
        fetcher.Next = _ => fetcher.File(Sha('a'));

        var e = (await sync.SyncAsync(Wanted(), null, Day1, default)).Entry!;

        Assert.Null(e.MirrorUrl);
        Assert.Equal("MIT", e.License);
    }

    [Fact]
    public async Task The_downloaded_temp_file_is_deleted_afterwards()
    {
        var (sync, fetcher, _, _) = Make();
        FetchResult.Fetched? seen = null;
        fetcher.Next = _ => seen = fetcher.File(Sha('a'));

        await sync.SyncAsync(Wanted(), null, Day1, default);

        Assert.False(File.Exists(seen!.Path));
    }

    [Fact]
    public void Version_is_the_utc_date_and_seven_hash_characters()
    {
        Assert.Equal("2026-10-22-0123abc", FileSync.VersionOf(new DateTimeOffset(2026, 10, 21, 23, 30, 0, TimeSpan.FromHours(-2)), "0123abc" + new string('f', 57)));
    }
}
