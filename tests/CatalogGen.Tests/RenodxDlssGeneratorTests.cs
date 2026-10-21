using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CatalogGen;
using Json.Schema;

namespace CatalogGen.Tests;

public class RenodxDlssGeneratorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cg-" + Guid.NewGuid().ToString("N"));

    public RenodxDlssGeneratorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Releases(string? json) : ITextSource
    {
        public Task<string> GetStringAsync(string url, CancellationToken ct) =>
            url == RenodxDlssGenerator.ReleasesUrl && json is not null ? Task.FromResult(json) : throw new HttpRequestException("404 " + url);
    }

    private sealed class Zips(string dir) : IFetcher
    {
        public HashSet<string> Missing { get; } = [];

        public Task<FetchResult> FetchAsync(string url, FileEntry? previous, CancellationToken ct)
        {
            if (Missing.Contains(url)) return Task.FromResult<FetchResult>(new FetchResult.Failed("HTTP 404"));
            if (previous is not null) return Task.FromResult<FetchResult>(new FetchResult.Unchanged(previous.ETag, previous.LastModified));
            var bytes = Encoding.ASCII.GetBytes("PK\u0003\u0004" + url);
            var path = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(path, bytes);
            return Task.FromResult<FetchResult>(new FetchResult.Fetched(path, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length, "\"e\"", null));
        }
    }

    private sealed class Licences(string? spdx) : ILicenceSource
    {
        public List<string> Repos { get; } = [];

        public Task<string?> SpdxAsync(string repo, CancellationToken ct)
        {
            Repos.Add(repo);
            return Task.FromResult(spdx);
        }
    }

    private sealed class CountingMirror : IMirror
    {
        public int Calls;

        public Task<string> PublishAsync(string fileName, string sha256, string month, string path, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult("https://mirror.test/" + fileName);
        }

        public Task SetNotesAsync(string month, string body, CancellationToken ct) => Task.CompletedTask;
    }

    private static object Release(string version, string published, int zips = 1, bool draft = false, bool prerelease = false, string? tag = null) => new
    {
        tag_name = tag ?? $"renodx-dlss-SF-{version}",
        draft,
        prerelease,
        published_at = published,
        assets = Enumerable.Range(0, zips).Select(i => new
        {
            name = $"renodx-dlss_SF_{version}{(i > 0 ? "-" + i : "")}.zip",
            browser_download_url = $"https://github.com/RankFTW/rhi-repo/releases/download/renodx-dlss-SF-{version}/renodx-dlss_SF_{version}{(i > 0 ? "-" + i : "")}.zip",
        }).ToArray(),
    };

    private static string ReleaseList(params object[] releases) => JsonSerializer.Serialize(releases);

    private RenodxDlssGenerator Generator(string? json, Zips? zips = null, ILicenceSource? licences = null, IMirror? mirror = null) =>
        new(new Releases(json), new FileSync(zips ?? new Zips(_dir), licences ?? new Licences(null), mirror));

    [Fact]
    public async Task Builds_come_from_the_releases_newest_first_with_a_pinned_zip()
    {
        var json = ReleaseList(
            Release("26.1003.2350", "2026-10-03T23:55:00Z"),
            Release("0.55", "2025-06-01T10:00:00Z"));

        var output = await Generator(json).BuildAsync(null, Now, default);

        Assert.Equal(RenodxDlssGenerator.ReleasesUrl, output.Section.Source);
        Assert.Equal(["26.1003.2350", "0.55"], output.Section.Builds.Select(b => b.Version).ToArray());
        var first = output.Section.Builds[0];
        Assert.Equal("renodx-dlss-SF-26.1003.2350", first.Tag);
        Assert.Equal("2026-10-03T23:55:00Z", first.Published);
        Assert.Equal("renodx-dlss_SF_26.1003.2350.zip", first.File.Name);
        Assert.Equal("x64", first.File.Arch);
        Assert.Equal("renodx-dlss", first.File.Mod);
        Assert.Equal("https://github.com/RankFTW/rhi-repo/releases/download/renodx-dlss-SF-26.1003.2350/renodx-dlss_SF_26.1003.2350.zip", first.File.Url);
        Assert.NotNull(first.File.Sha256);
        Assert.NotNull(first.File.Version);
        Assert.Empty(output.Warnings);
        Assert.Empty(output.Errors);
    }

    [Fact]
    public async Task A_build_is_newest_by_publish_date_not_by_list_order()
    {
        var json = ReleaseList(Release("1", "2026-01-01T00:00:00Z"), Release("3", "2026-03-01T00:00:00Z"), Release("2", "2026-02-01T00:00:00Z"));

        var output = await Generator(json).BuildAsync(null, Now, default);

        Assert.Equal(["3", "2", "1"], output.Section.Builds.Select(b => b.Version).ToArray());
    }

    [Fact]
    public async Task Other_tags_drafts_prereleases_and_releases_without_exactly_one_zip_are_left_out()
    {
        var json = ReleaseList(
            Release("ok", "2026-05-01T00:00:00Z"),
            Release("x", "2026-05-02T00:00:00Z", tag: "renodx-dlss-RANK-1.0"),
            Release("x", "2026-05-02T00:00:00Z", tag: "rhi-1.2.3"),
            Release("draft", "2026-05-03T00:00:00Z", draft: true),
            Release("pre", "2026-05-04T00:00:00Z", prerelease: true),
            Release("none", "2026-05-05T00:00:00Z", zips: 0),
            Release("two", "2026-05-06T00:00:00Z", zips: 2));

        var output = await Generator(json).BuildAsync(null, Now, default);

        Assert.Equal(["ok"], output.Section.Builds.Select(b => b.Version).ToArray());
        Assert.Contains(output.Warnings, w => w.Contains("renodx-dlss-SF-none"));
        Assert.Contains(output.Warnings, w => w.Contains("renodx-dlss-SF-two"));
    }

    [Fact]
    public async Task Only_the_newest_twenty_are_kept()
    {
        var json = ReleaseList(Enumerable.Range(1, 24).Select(i => Release($"v{i}", $"2026-01-{i:00}T00:00:00Z")).ToArray());

        var output = await Generator(json).BuildAsync(null, Now, default);

        Assert.Equal(20, output.Section.Builds.Count);
        Assert.Equal("v24", output.Section.Builds[0].Version);
        Assert.Equal("v5", output.Section.Builds[^1].Version);
    }

    [Fact]
    public async Task A_build_whose_download_failed_is_a_warning_and_keeps_its_last_good_entry()
    {
        var json = ReleaseList(Release("1", "2026-01-01T00:00:00Z"), Release("2", "2026-02-01T00:00:00Z"));
        var zips = new Zips(_dir);
        var first = (await Generator(json, zips).BuildAsync(null, Now, default)).Section;
        var url1 = first.Builds.Single(b => b.Version == "1").File.Url;

        zips.Missing.Add(url1);
        var fresh = await Generator(json, zips).BuildAsync(null, Now, default);
        Assert.Equal(["2"], fresh.Section.Builds.Select(b => b.Version).ToArray());
        Assert.Contains(fresh.Warnings, w => w.Contains(url1));
        Assert.Empty(fresh.Errors);

        // a week later the hash is due for a full download again, which fails
        var kept = await Generator(json, zips).BuildAsync(first, Now.AddDays(8), default);
        Assert.Equal(["2", "1"], kept.Section.Builds.Select(b => b.Version).ToArray());
        Assert.Equal(first.Builds.Single(b => b.Version == "1").File.Sha256, kept.Section.Builds.Single(b => b.Version == "1").File.Sha256);
        Assert.NotEmpty(kept.Warnings);
    }

    [Fact]
    public async Task No_matching_release_keeps_the_previous_section()
    {
        var json = ReleaseList(Release("x", "2026-05-02T00:00:00Z", tag: "rhi-1.0"));

        var ex = await Assert.ThrowsAsync<SourceException>(() => Generator(json).BuildAsync(null, Now, default));

        Assert.Contains("renodx-dlss-SF", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("<html>rate limited</html>")]
    [InlineData("{\"message\":\"Not Found\"}")]
    public async Task An_unreachable_or_broken_list_is_a_source_error(string? json)
    {
        await Assert.ThrowsAsync<SourceException>(() => Generator(json).BuildAsync(null, Now, default));
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        var generator = Generator(ReleaseList(Release("1", "2026-01-01T00:00:00Z")));
        var first = (await generator.BuildAsync(null, Now, default)).Section;

        var second = (await generator.BuildAsync(first, Now.AddDays(1), default)).Section;

        Assert.Equal(JsonSerializer.Serialize(first, CatalogGen.Json.Options), JsonSerializer.Serialize(second, CatalogGen.Json.Options));
    }

    [Fact]
    public async Task Without_the_any_licence_switch_a_build_of_an_unlicensed_repository_is_never_mirrored()
    {
        var mirror = new CountingMirror();
        var licences = new Licences(null);

        var output = await Generator(ReleaseList(Release("1", "2026-01-01T00:00:00Z")), licences: licences, mirror: mirror).BuildAsync(null, Now, default);

        Assert.Equal(["RankFTW/rhi-repo"], licences.Repos.Distinct());
        Assert.Equal(0, mirror.Calls);
        var file = output.Section.Builds.Single().File;
        Assert.Null(file.MirrorUrl);
        Assert.Null(file.License);
        Assert.NotNull(file.Sha256);
    }

    [Fact]
    public async Task The_section_validates_against_the_published_schema()
    {
        var output = await Generator(ReleaseList(Release("26.1003.2350", "2026-10-03T23:55:00Z"), Release("0.55", "2025-06-01T10:00:00Z"))).BuildAsync(null, Now, default);
        var root = CatalogFile.Load(Path.Combine(_dir, "none.json"));
        CatalogFile.SetSection(root, "renodxDlss", output.Section, Now);

        var result = Evaluate(JsonNode.Parse(CatalogFile.Serialize(root))!);

        Assert.True(result.IsValid, string.Join("\n", result.Details.Where(d => !d.IsValid && d.Errors is not null).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))));
        Assert.NotNull(root["sections"]!["renodxDlss"]!["builds"]![0]!["file"]!["sha256"]);
    }

    [Fact]
    public void The_schema_rejects_a_build_without_a_hash_or_tag()
    {
        var hash = new string('a', 64);
        string File(bool withHash) =>
            "{\"name\":\"a.zip\",\"arch\":\"x64\",\"url\":\"https://x.test/a.zip\"," + (withHash ? $"\"sha256\":\"{hash}\"," : "") +
            "\"size\":1,\"version\":\"v\",\"firstSeen\":\"2026-10-21T12:00:00Z\",\"history\":[]}";
        JsonNode Doc(string build) => JsonNode.Parse(
            "{\"schema\":1,\"repository\":\"a/b\",\"sections\":{\"renodxDlss\":{\"updated\":\"2026-10-21T12:00:00Z\",\"source\":\"https://api.github.com/x\",\"builds\":[" + build + "]}}}")!;

        Assert.True(Evaluate(Doc("{\"tag\":\"t\",\"version\":\"1\",\"published\":\"2026-10-21T12:00:00Z\",\"file\":" + File(true) + "}")).IsValid);
        Assert.False(Evaluate(Doc("{\"tag\":\"t\",\"version\":\"1\",\"published\":\"2026-10-21T12:00:00Z\",\"file\":" + File(false) + "}")).IsValid);
        Assert.False(Evaluate(Doc("{\"version\":\"1\",\"published\":\"2026-10-21T12:00:00Z\",\"file\":" + File(true) + "}")).IsValid);
    }

    private static EvaluationResults Evaluate(JsonNode doc)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "schema", "catalog.schema.json");
            if (System.IO.File.Exists(candidate))
                return JsonSchema.FromText(System.IO.File.ReadAllText(candidate)).Evaluate(doc, new EvaluationOptions { OutputFormat = OutputFormat.List });
        }
        throw new FileNotFoundException("schema/catalog.schema.json");
    }
}
