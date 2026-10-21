using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CatalogGen;
using Json.Schema;

namespace CatalogGen.Tests;

public class RenodxGeneratorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cg-" + Guid.NewGuid().ToString("N"));

    public RenodxGeneratorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Texts(Dictionary<string, string> pages) : ITextSource
    {
        public Task<string> GetStringAsync(string url, CancellationToken ct) =>
            pages.TryGetValue(url, out var text) ? Task.FromResult(text) : throw new HttpRequestException($"404 {url}");
    }

    private sealed class Files(string dir) : IFetcher
    {
        public List<string> Urls { get; } = [];
        public HashSet<string> Missing { get; } = [];

        public Task<FetchResult> FetchAsync(string url, FileEntry? previous, CancellationToken ct)
        {
            lock (Urls) Urls.Add(url);
            if (Missing.Contains(url)) return Task.FromResult<FetchResult>(new FetchResult.Failed("HTTP 404"));
            if (previous is not null) return Task.FromResult<FetchResult>(new FetchResult.Unchanged(previous.ETag, previous.LastModified));
            var bytes = Encoding.ASCII.GetBytes("MZ" + url);
            var path = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(path, bytes);
            return Task.FromResult<FetchResult>(new FetchResult.Fetched(path, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length, "\"e\"", null));
        }
    }

    private sealed class Mit : ILicenceSource
    {
        public Task<string?> SpdxAsync(string repo, CancellationToken ct) => Task.FromResult<string?>(repo.StartsWith("clshortfuse/") ? "MIT" : null);
    }

    private sealed class RecordingMirror : IMirror
    {
        public List<string> Names { get; } = [];
        public List<(string Month, string Body)> Notes { get; } = [];
        public bool FailNotes;

        public Task<string> PublishAsync(string fileName, string sha256, string month, string path, CancellationToken ct)
        {
            lock (Names) Names.Add(fileName);
            return Task.FromResult($"https://mirror.test/mirror-{month}/{sha256[..16]}-{fileName}");
        }

        public Task SetNotesAsync(string month, string body, CancellationToken ct)
        {
            if (FailNotes) throw new HttpRequestException("403");
            Notes.Add((month, body));
            return Task.CompletedTask;
        }
    }

    private sealed class MitNotices : INoticeSource
    {
        public List<string> Repos { get; } = [];
        public bool Fail;

        public Task<LicenceNotice?> NoticeAsync(string repo, CancellationToken ct)
        {
            Repos.Add(repo);
            if (Fail) throw new HttpRequestException("rate limited");
            return Task.FromResult<LicenceNotice?>(new LicenceNotice("MIT", $"https://github.com/{repo}/blob/main/LICENSE", "MIT License\n\nCopyright (c) 2025 Someone\n"));
        }
    }

    private sealed class NoLicence : ILicenceSource
    {
        public Task<string?> SpdxAsync(string repo, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private static Dictionary<string, string> Pages() => new()
    {
        [RenodxSources.GamesIndexUrl] = File.ReadAllText(Path.Combine("Fixtures", "games-index.sample.json")),
        [RenodxSources.WikiUrl] = File.ReadAllText(Path.Combine("Fixtures", "Mods.sample.md")),
    };

    private RenodxGenerator Generator(Dictionary<string, string> pages, Files files, IMirror? mirror = null, bool acceptShrink = false, INoticeSource? notices = null) =>
        new(new Texts(pages), new FileSync(files, new Mit(), mirror), 4, acceptShrink, minIndexGames: 2, minWikiRows: 5,
            notices: mirror is not null && notices is not null ? new MirrorNoticePublisher(notices, mirror) : null);

    [Fact]
    public async Task Every_file_gets_a_hash_and_a_url_shared_by_two_entries_is_fetched_once()
    {
        var files = new Files(_dir);

        var output = await Generator(Pages(), files).BuildAsync(null, new OverrideBlock(), Now, default);

        var all = output.Section.Mods.SelectMany(m => m.Files).Concat(output.Section.Generic.SelectMany(g => g.Files)).ToList();
        Assert.All(all, f => Assert.NotNull(f.Sha256));
        Assert.Equal(files.Urls.Count, files.Urls.Distinct().Count());
        Assert.Equal(all.Select(f => f.Url).Distinct().Count(), files.Urls.Count);
        Assert.Empty(output.Errors);
    }

    [Fact]
    public async Task Only_files_of_permissive_sources_are_mirrored()
    {
        var mirror = new RecordingMirror();

        var output = await Generator(Pages(), new Files(_dir), mirror).BuildAsync(null, new OverrideBlock(), Now, default);

        var all = output.Section.Mods.SelectMany(m => m.Files).Concat(output.Section.Generic.SelectMany(g => g.Files)).ToList();
        Assert.All(all.Where(f => f.Url.Contains("github.com/clshortfuse/")), f => Assert.NotNull(f.MirrorUrl));
        Assert.All(all.Where(f => !f.Url.Contains("github.com/clshortfuse/")), f => Assert.Null(f.MirrorUrl));
        Assert.Contains("renodx-1000xresist.addon64", mirror.Names);
        Assert.DoesNotContain("renodx-ue-extended.addon64", mirror.Names);
    }

    [Fact]
    public async Task The_notices_of_each_month_are_set_once_with_the_mirrored_files_of_their_repositories()
    {
        var mirror = new RecordingMirror();
        var notices = new MitNotices();

        var output = await Generator(Pages(), new Files(_dir), mirror, notices: notices).BuildAsync(null, new OverrideBlock(), Now, default);

        var (month, body) = Assert.Single(mirror.Notes);
        Assert.Equal("2026-10", month);
        Assert.Empty(output.Errors);
        Assert.Contains("### clshortfuse/renodx\n", body);
        Assert.Contains("- Licence: MIT (https://github.com/clshortfuse/renodx/blob/main/LICENSE)", body);
        Assert.Contains("  - Copyright (c) 2025 Someone\n", body);
        var mirrored = output.Section.Mods.SelectMany(m => m.Files).Concat(output.Section.Generic.SelectMany(g => g.Files))
            .Where(f => f.MirrorUrl is not null).ToList();
        Assert.NotEmpty(mirrored);
        Assert.All(mirrored, f => Assert.Contains("  - " + f.MirrorUrl![(f.MirrorUrl!.LastIndexOf('/') + 1)..] + "\n", body + "\n"));
        Assert.DoesNotContain("marat569", body);
        Assert.Equal(notices.Repos.Count, notices.Repos.Distinct().Count());
    }

    [Fact]
    public async Task No_notices_are_set_when_nothing_is_mirrored()
    {
        var mirror = new RecordingMirror();

        var generator = new RenodxGenerator(new Texts(Pages()), new FileSync(new Files(_dir), new NoLicence(), mirror), 4, false, 2, 5,
            new MirrorNoticePublisher(new MitNotices(), mirror));
        var output = await generator.BuildAsync(null, new OverrideBlock(), Now, default);

        Assert.Empty(mirror.Notes);
        Assert.Empty(mirror.Names);
        Assert.Empty(output.Errors);
    }

    [Fact]
    public async Task A_failed_notice_lookup_is_an_error_for_the_run_but_the_section_is_still_built()
    {
        var mirror = new RecordingMirror();

        var output = await Generator(Pages(), new Files(_dir), mirror, notices: new MitNotices { Fail = true }).BuildAsync(null, new OverrideBlock(), Now, default);

        Assert.Empty(mirror.Notes);
        Assert.Contains(output.Errors, e => e.Contains("clshortfuse/renodx") && e.Contains("rate limited"));
        Assert.NotEmpty(output.Section.Mods);
    }

    [Fact]
    public async Task A_failed_notice_write_is_an_error_for_the_run_but_the_section_is_still_built()
    {
        var mirror = new RecordingMirror { FailNotes = true };

        var output = await Generator(Pages(), new Files(_dir), mirror, notices: new MitNotices()).BuildAsync(null, new OverrideBlock(), Now, default);

        Assert.Contains(output.Errors, e => e.Contains("mirror-2026-10") && e.Contains("403"));
        Assert.NotEmpty(output.Section.Mods);
    }

    [Fact]
    public async Task A_file_that_fails_to_download_is_left_out_with_a_warning_and_does_not_fail_the_run()
    {
        var files = new Files(_dir);
        files.Missing.Add("https://github.com/mqhaji/renodx/releases/download/snapshot/renodx-007firstlight.addon64");

        var output = await Generator(Pages(), files).BuildAsync(null, new OverrideBlock(), Now, default);

        Assert.Empty(output.Section.Mods.Single(m => m.Name == "007 First Light").Files);
        Assert.Contains(output.Warnings, w => w.Contains("renodx-007firstlight") && w.Contains("404"));
        Assert.Empty(output.Errors);
    }

    [Fact]
    public async Task Second_run_with_the_previous_section_keeps_versions_and_reports_no_change()
    {
        var files = new Files(_dir);
        var generator = Generator(Pages(), files);
        var first = (await generator.BuildAsync(null, new OverrideBlock(), Now, default)).Section;

        var second = (await generator.BuildAsync(first, new OverrideBlock(), Now.AddDays(1), default)).Section;

        Assert.Equal(JsonSerializer.Serialize(first, Json.Options), JsonSerializer.Serialize(second, Json.Options));
    }

    [Fact]
    public async Task A_broken_games_index_fails_the_run_with_a_source_error()
    {
        var pages = Pages();
        pages.Remove(RenodxSources.GamesIndexUrl);

        var ex = await Assert.ThrowsAsync<SourceException>(() => Generator(pages, new Files(_dir)).BuildAsync(null, new OverrideBlock(), Now, default));

        Assert.Contains("games-index", ex.Message);
    }

    [Fact]
    public async Task Invalid_json_in_the_games_index_is_a_source_error()
    {
        var pages = Pages();
        pages[RenodxSources.GamesIndexUrl] = "<html>rate limited</html>";

        await Assert.ThrowsAsync<SourceException>(() => Generator(pages, new Files(_dir)).BuildAsync(null, new OverrideBlock(), Now, default));
    }

    [Fact]
    public async Task A_missing_wiki_page_is_a_source_error()
    {
        var pages = Pages();
        pages.Remove(RenodxSources.WikiUrl);

        var ex = await Assert.ThrowsAsync<SourceException>(() => Generator(pages, new Files(_dir)).BuildAsync(null, new OverrideBlock(), Now, default));

        Assert.Contains("wiki", ex.Message);
    }

    [Fact]
    public async Task A_truncated_list_is_a_source_error()
    {
        var generator = new RenodxGenerator(new Texts(Pages()), new FileSync(new Files(_dir), new Mit(), null), minIndexGames: 50, minWikiRows: 100);

        var ex = await Assert.ThrowsAsync<SourceException>(() => generator.BuildAsync(null, new OverrideBlock(), Now, default));

        Assert.Contains("truncated", ex.Message);
    }

    [Fact]
    public async Task Losing_a_fifth_of_the_mods_is_refused_unless_accepted()
    {
        var files = new Files(_dir);
        var previous = (await Generator(Pages(), files).BuildAsync(null, new OverrideBlock(), Now, default)).Section;
        while (previous.Mods.Count < 40) previous.Mods.Add(new ModEntry { Id = "extra" + previous.Mods.Count, Name = "Extra " + previous.Mods.Count });

        await Assert.ThrowsAsync<SourceException>(() => Generator(Pages(), files).BuildAsync(previous, new OverrideBlock(), Now, default));
        var accepted = await Generator(Pages(), files, acceptShrink: true).BuildAsync(previous, new OverrideBlock(), Now, default);

        Assert.True(accepted.Section.Mods.Count < previous.Mods.Count);
    }

    [Fact]
    public async Task The_catalog_validates_against_the_published_schema()
    {
        var output = await Generator(Pages(), new Files(_dir), new RecordingMirror()).BuildAsync(null, new OverrideBlock(), Now, default);
        var root = CatalogFile.Load(Path.Combine(_dir, "none.json"));
        CatalogFile.SetSection(root, "renodx", output.Section, Now);

        var schema = JsonSchema.FromText(File.ReadAllText(SchemaPath()));
        var result = schema.Evaluate(JsonNode.Parse(CatalogFile.Serialize(root)), new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, string.Join("\n", result.Details.Where(d => !d.IsValid && d.Errors is not null).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))));
    }

    [Fact]
    public void The_schema_rejects_a_file_without_a_hash()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(SchemaPath()));
        var doc = JsonNode.Parse("""
            {"schema":1,"repository":"a/b","sections":{"renodx":{"updated":"2026-10-21T12:00:00Z","sources":[],"generic":[],"engineGames":[],"overrides":{},
              "mods":[{"id":"x","name":"X","aliases":[],"steamAppIds":[],"authors":[],"listing":"main","blocked":false,
                "files":[{"name":"a.addon64","arch":"x64","url":"https://x.test/a.addon64","size":1,"version":"v","firstSeen":"2026-10-21T12:00:00Z","history":[]}]}]}}}
            """);

        Assert.False(schema.Evaluate(doc, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid);
    }

    [Fact]
    public void The_schema_checks_the_structured_unreal_extended_fields()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(SchemaPath()));
        string Doc(string game) => """
            {"schema":1,"repository":"a/b","sections":{"renodx":{"updated":"2026-10-21T12:00:00Z","sources":[],"generic":[],"mods":[],"overrides":{},"engineGames":[GAME]}}}
            """.Replace("GAME", game);
        bool Valid(string game) => schema.Evaluate(JsonNode.Parse(Doc(game)), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;

        Assert.True(Valid("""{"engine":"ue-extended","name":"X","method":"upgrade","upgrades":[{"format":"B8G8R8A8_TYPELESS","mode":"output-size"}],"copyDestinations":false,"engineIni":[{"section":"SystemSettings","key":"r.HDR.EnableHDROutput","value":"1"}]}"""));
        Assert.False(Valid("""{"engine":"ue-extended","name":"X","method":"magic"}"""));
        Assert.False(Valid("""{"engine":"ue-extended","name":"X","upgrades":[{"format":"R8G8R8A8_TYPELESS","mode":"any-size"}]}"""));
        Assert.False(Valid("""{"engine":"ue-extended","name":"X","upgrades":[{"format":"R11G11B10_FLOAT","mode":"whatever"}]}"""));
        Assert.False(Valid("""{"engine":"ue-extended","name":"X","engineIni":[{"section":"SystemSettings","key":"foo","value":"1"}]}"""));
    }

    [Fact]
    public void The_committed_catalog_json_validates_against_the_schema()
    {
        var path = Path.Combine(Path.GetDirectoryName(SchemaPath())!, "..", "catalog.json");
        if (!File.Exists(path)) return; // before the first Action run there is none

        var schema = JsonSchema.FromText(File.ReadAllText(SchemaPath()));
        var result = schema.Evaluate(JsonNode.Parse(File.ReadAllText(path)), new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Details.Where(d => !d.IsValid && d.Errors is not null).Take(10).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))));
    }

    private static string SchemaPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "schema", "catalog.schema.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("schema/catalog.schema.json");
    }
}
