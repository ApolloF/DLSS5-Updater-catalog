using System.Text.Json;
using System.Text.Json.Nodes;
using CatalogGen;
using Json.Schema;

namespace CatalogGen.Tests;

public class FrameLimitersGeneratorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private const string ReLimiterApi = "https://api.github.com/repos/RankFTW/ReLimiter/releases/latest";
    private const string DcApi = "https://api.github.com/repos/pmnoxx/display-commander/releases/tags/latest";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cg-" + Guid.NewGuid().ToString("N"));

    public FrameLimitersGeneratorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Release(string tag, string repo, params string[] assets) => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        assets = assets.Select(a => new { name = a, browser_download_url = $"https://github.com/{repo}/releases/download/{tag}/{a}" }).ToArray(),
    });

    private static Dictionary<string, string> Pages(string relimiter, string dc) => new() { [ReLimiterApi] = relimiter, [DcApi] = dc };

    private static string Re(string tag = "v3.3.5", bool x86 = true) =>
        Release(tag, "RankFTW/ReLimiter", x86 ? ["relimiter.addon32", "relimiter.addon64"] : ["relimiter.addon64"]);

    private static readonly string Dc = Release("latest", "pmnoxx/display-commander", "zzz_display_commander.addon32", "zzz_display_commander.addon64", "zzz_display_commander_x64.pdb");

    private FakeFiles Files(params string[] urls)
    {
        var files = new FakeFiles(_dir);
        foreach (var u in urls) files.Served[u] = TestZip.Pe(u);
        return files;
    }

    private static string[] AllUrls(string tag = "v3.3.5") =>
    [
        $"https://github.com/RankFTW/ReLimiter/releases/download/{tag}/relimiter.addon64",
        $"https://github.com/RankFTW/ReLimiter/releases/download/{tag}/relimiter.addon32",
        "https://github.com/pmnoxx/display-commander/releases/download/latest/zzz_display_commander.addon64",
        "https://github.com/pmnoxx/display-commander/releases/download/latest/zzz_display_commander.addon32",
    ];

    private FrameLimitersGenerator Generator(Dictionary<string, string> pages, IFetcher files, FakeMirrorSink? mirror = null) =>
        new(new FakeText(pages), new FileSync(files, new FakeLicenceSource(new() { ["RankFTW/ReLimiter"] = "MIT", ["pmnoxx/display-commander"] = null }), mirror));

    [Fact]
    public async Task Both_limiters_are_read_from_their_releases_with_a_file_per_bitness()
    {
        var output = await Generator(Pages(Re(), Dc), Files(AllUrls())).BuildAsync(null, Now, default);

        Assert.Empty(output.Errors);
        Assert.Equal(["relimiter", "display-commander"], output.Section.Limiters.Select(l => l.Id).ToArray());
        var re = output.Section.Limiters[0];
        Assert.Equal("v3.3.5", re.Release);
        Assert.Equal("RankFTW/ReLimiter", re.Repository);
        Assert.Equal(["relimiter.addon64", "relimiter.addon32"], re.Files.Select(f => f.Name).ToArray());
        Assert.Equal(["x64", "x86"], re.Files.Select(f => f.Arch).ToArray());
        Assert.All(output.Section.Limiters.SelectMany(l => l.Files), f => Assert.NotNull(f.Sha256));
        // the debug symbols of the Display Commander release are not add-on files
        Assert.Equal(["zzz_display_commander.addon64", "zzz_display_commander.addon32"], output.Section.Limiters[1].Files.Select(f => f.Name).ToArray());
        Assert.Equal(2, output.Section.Sources.Count);
    }

    [Fact]
    public async Task A_permissive_licence_is_mirrored_and_no_licence_stays_pinned()
    {
        var mirror = new FakeMirrorSink();

        var output = await Generator(Pages(Re(), Dc), Files(AllUrls()), mirror).BuildAsync(null, Now, default);

        var re = output.Section.Limiters[0];
        var dc = output.Section.Limiters[1];
        Assert.Equal("MIT", re.License);
        Assert.All(re.Files, f => Assert.NotNull(f.MirrorUrl));
        Assert.Null(dc.License);
        Assert.All(dc.Files, f => Assert.Null(f.MirrorUrl));
        Assert.Equal(["relimiter.addon32", "relimiter.addon64"], mirror.Uploads.Order().ToArray());
    }

    [Fact]
    public async Task A_release_without_a_32_bit_file_lists_only_the_64_bit_one_and_warns()
    {
        var urls = AllUrls("v3.3.0").Where(u => !u.EndsWith("relimiter.addon32")).ToArray();

        var output = await Generator(Pages(Re("v3.3.0", x86: false), Dc), Files(urls)).BuildAsync(null, Now, default);

        Assert.Equal(["x64"], output.Section.Limiters[0].Files.Select(f => f.Arch).ToArray());
        Assert.Contains(output.Warnings, w => w.Contains("no 32-bit build"));
        Assert.Empty(output.Errors);
    }

    [Fact]
    public async Task A_new_release_tag_keeps_the_history_of_the_file()
    {
        var first = (await Generator(Pages(Re("v3.3.4"), Dc), Files(AllUrls("v3.3.4"))).BuildAsync(null, Now, default)).Section;
        var oldHash = first.Limiters[0].Files[0].Sha256;

        var second = (await Generator(Pages(Re("v3.3.5"), Dc), Files(AllUrls("v3.3.5"))).BuildAsync(first, Now.AddDays(1), default)).Section;

        var file = second.Limiters[0].Files[0];
        Assert.Equal("v3.3.5", second.Limiters[0].Release);
        Assert.NotEqual(oldHash, file.Sha256);
        Assert.Contains(file.History, h => h.Sha256 == oldHash);
    }

    [Fact]
    public async Task A_limiter_whose_release_cannot_be_read_keeps_its_last_good_entry_and_is_an_error()
    {
        var first = (await Generator(Pages(Re(), Dc), Files(AllUrls())).BuildAsync(null, Now, default)).Section;
        var pages = new Dictionary<string, string> { [DcApi] = Dc };

        var output = await Generator(pages, Files(AllUrls())).BuildAsync(first, Now.AddDays(1), default);

        Assert.Equal(["relimiter", "display-commander"], output.Section.Limiters.Select(l => l.Id).ToArray());
        Assert.Equal(first.Limiters[0].Files[0].Sha256, output.Section.Limiters[0].Files[0].Sha256);
        Assert.Contains(output.Errors, e => e.StartsWith("ReLimiter"));
    }

    [Fact]
    public async Task Nothing_readable_and_nothing_before_is_a_source_error()
    {
        await Assert.ThrowsAsync<SourceException>(() => Generator([], Files()).BuildAsync(null, Now, default));
    }

    [Fact]
    public async Task A_release_without_the_64_bit_asset_is_refused()
    {
        var output = await Generator(Pages(Release("v9", "RankFTW/ReLimiter", "relimiter.addon32"), Dc), Files(AllUrls())).BuildAsync(null, Now, default);

        Assert.Equal(["display-commander"], output.Section.Limiters.Select(l => l.Id).ToArray());
        Assert.Contains(output.Errors, e => e.Contains("no relimiter.addon64"));
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        var generator = Generator(Pages(Re(), Dc), Files(AllUrls()));
        var first = (await generator.BuildAsync(null, Now, default)).Section;

        var second = (await generator.BuildAsync(first, Now.AddDays(1), default)).Section;

        Assert.Equal(JsonSerializer.Serialize(first, CatalogGen.Json.Options), JsonSerializer.Serialize(second, CatalogGen.Json.Options));
    }

    [Fact]
    public async Task The_section_validates_against_the_published_schema_and_is_among_the_mirrored_files()
    {
        var output = await Generator(Pages(Re(), Dc), Files(AllUrls()), new FakeMirrorSink()).BuildAsync(null, Now, default);
        var root = CatalogFile.Load(Path.Combine(_dir, "none.json"));
        CatalogFile.SetSection(root, "frameLimiters", output.Section, Now);

        var result = Evaluate(JsonNode.Parse(CatalogFile.Serialize(root))!);

        Assert.True(result.IsValid, string.Join("\n", result.Details.Where(d => !d.IsValid && d.Errors is not null).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))));
        Assert.Equal(4, Program.MirroredFiles(root).Count);
    }

    private static EvaluationResults Evaluate(JsonNode doc)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "schema", "catalog.schema.json");
            if (File.Exists(candidate))
                return JsonSchema.FromText(File.ReadAllText(candidate)).Evaluate(doc, new EvaluationOptions { OutputFormat = OutputFormat.List });
        }
        throw new FileNotFoundException("schema/catalog.schema.json");
    }
}
