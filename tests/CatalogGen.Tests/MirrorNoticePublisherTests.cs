using CatalogGen;

namespace CatalogGen.Tests;

public class MirrorNoticePublisherTests
{
    private sealed class Notices(Dictionary<string, LicenceNotice?> byRepo) : INoticeSource
    {
        public List<string> Calls { get; } = [];

        public Task<LicenceNotice?> NoticeAsync(string repo, CancellationToken ct)
        {
            Calls.Add(repo);
            return byRepo.TryGetValue(repo, out var n) ? Task.FromResult(n) : throw new HttpRequestException("500");
        }
    }

    private sealed class Mirror : IMirror
    {
        public List<(string Month, string Body)> Notes { get; } = [];

        public Task<string> PublishAsync(string fileName, string sha256, string month, string path, CancellationToken ct) => throw new NotSupportedException();

        public Task SetNotesAsync(string month, string body, CancellationToken ct)
        {
            Notes.Add((month, body));
            return Task.CompletedTask;
        }
    }

    private static LicenceNotice Mit(string owner) => new("MIT", $"https://github.com/{owner}/x/blob/main/LICENSE", "Copyright (c) 2024 " + owner);

    private static FileEntry File(string repo, string name, string? mirrorUrl, params string?[] history) => new()
    {
        Name = name,
        Url = $"https://github.com/{repo}/releases/download/snapshot/{name}",
        MirrorUrl = mirrorUrl,
        History = history.Select((url, i) => new HistoryEntry("v" + i, new string('a', 64), 1, "2026-01-01T00:00:00Z", url)).ToList(),
    };

    private static string Url(string month, string asset) => $"https://github.com/o/mirror/releases/download/mirror-{month}/{asset}";

    [Fact]
    public async Task Current_builds_and_history_are_grouped_by_the_month_of_their_mirror_url()
    {
        var notices = new Notices(new() { ["a/x"] = Mit("a"), ["b/x"] = Mit("b") });
        var mirror = new Mirror();
        var files = new[]
        {
            File("a/x", "a1.addon64", Url("2026-10", "1111-a1.addon64"), Url("2026-09", "2222-a1.addon64"), null),
            File("b/x", "b1.addon64", Url("2026-10", "3333-b1.addon64")),
            File("a/x", "pinned.addon64", null),
        };
        var errors = new List<string>();

        await new MirrorNoticePublisher(notices, mirror).PublishAsync(files, errors, default);

        Assert.Empty(errors);
        Assert.Equal(["2026-09", "2026-10"], mirror.Notes.Select(n => n.Month).ToArray());
        var september = mirror.Notes[0].Body;
        Assert.Contains("### a/x", september);
        Assert.Contains("  - 2222-a1.addon64", september);
        Assert.DoesNotContain("b/x", september);
        var october = mirror.Notes[1].Body;
        Assert.True(october.IndexOf("### a/x", StringComparison.Ordinal) < october.IndexOf("### b/x", StringComparison.Ordinal));
        Assert.Contains("  - 1111-a1.addon64", october);
        Assert.Contains("  - 3333-b1.addon64", october);
        Assert.DoesNotContain("pinned", october);
        Assert.Equal(["a/x", "b/x"], notices.Calls);
    }

    [Fact]
    public async Task Nothing_mirrored_means_no_lookup_and_no_call()
    {
        var notices = new Notices([]);
        var mirror = new Mirror();

        await new MirrorNoticePublisher(notices, mirror).PublishAsync([File("a/x", "p.addon64", null)], [], default);

        Assert.Empty(notices.Calls);
        Assert.Empty(mirror.Notes);
    }

    [Fact]
    public async Task A_repository_without_a_licence_file_is_an_error_and_its_months_are_left_alone()
    {
        var notices = new Notices(new() { ["a/x"] = null, ["b/x"] = Mit("b") });
        var mirror = new Mirror();
        var errors = new List<string>();
        var files = new[]
        {
            File("a/x", "a1.addon64", Url("2026-10", "1-a1.addon64")),
            File("b/x", "b1.addon64", Url("2026-11", "2-b1.addon64")),
        };

        await new MirrorNoticePublisher(notices, mirror).PublishAsync(files, errors, default);

        Assert.Contains(errors, e => e.Contains("a/x") && e.Contains("no licence file"));
        Assert.Equal(["2026-11"], mirror.Notes.Select(n => n.Month).ToArray());
    }
}
