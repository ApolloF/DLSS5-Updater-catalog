using System.Text.Json;
using System.Text.Json.Nodes;
using CatalogGen;
using Json.Schema;

namespace CatalogGen.Tests;

public class LumaGeneratorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private const string Tag = "latest-755";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cg-" + Guid.NewGuid().ToString("N"));

    public LumaGeneratorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static string WikiText() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Luma.sample.md"));

    private static string Url(string asset, string tag = Tag) => $"https://github.com/Filoppi/Luma-Framework/releases/download/{tag}/{asset}";

    private static readonly string[] Assets =
    [
        "Luma-Unreal_Engine.zip", "Luma-Unreal_Engine-Test.zip", "Luma-Unity_Engine.zip", "Luma-Unity_Engine-x32.zip",
        "Luma-BioShock_Series-x32.zip", "Luma-BioShock_Series-Test-x32.zip", "Luma-Prey.zip", "Luma-Prey-Test.zip", "Luma-Prey-Dev.zip",
        "Luma-Call_of_Duty_Black_Ops_3.zip", "Luma-Borderlands_GOTY_Enhanced.zip", "Luma-Batman_Arkham_Knight.zip", "Luma-Heavy_Rain.zip",
        "Luma-Generic_Mod.zip", "Luma-Graphics_Analyzer.zip",
    ];

    private static string Release(string tag, IEnumerable<string> assets) => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        assets = assets.Select(a => new { name = a, browser_download_url = Url(a, tag) }).ToArray(),
    });

    private LumaGenerator Generator(string wiki, string release, IFetcher files, FakeMirrorSink? mirror = null) =>
        new(new FakeText(new() { [LumaGenerator.WikiUrl] = wiki }), new FakeText(new() { [LumaGenerator.ReleaseApi] = release }),
            new FileSync(files, new FakeLicenceSource(new() { ["Filoppi/Luma-Framework"] = null }), mirror), 1, 5, 3);

    private FakeFiles Served(string tag = Tag, IEnumerable<string>? assets = null)
    {
        var files = new FakeFiles(_dir);
        foreach (var a in assets ?? Assets) files.Served[Url(a, tag)] = TestZip.Pe(a + tag);
        return files;
    }

    private async Task<LumaSection> Build(FakeMirrorSink? mirror = null) =>
        (await Generator(WikiText(), Release(Tag, Assets), Served(), mirror).BuildAsync(null, Now, default)).Section;

    [Fact]
    public void Asset_names_give_the_package_the_bitness_and_whether_it_is_a_test_build()
    {
        Assert.Equal(new LumaGenerator.Asset("Prey", false, false), LumaGenerator.ParseAsset("Luma-Prey.zip"));
        Assert.Equal(new LumaGenerator.Asset("BioShock_Series", true, false), LumaGenerator.ParseAsset("Luma-BioShock_Series-x32.zip"));
        Assert.Equal(new LumaGenerator.Asset("BioShock_Series", true, true), LumaGenerator.ParseAsset("Luma-BioShock_Series-Test-x32.zip"));
        Assert.Equal(new LumaGenerator.Asset("Generic_Mod", false, true), LumaGenerator.ParseAsset("Luma-Generic_Mod-Dev.zip"));
        Assert.Null(LumaGenerator.ParseAsset("Luma-Prey.pdb"));
    }

    [Fact]
    public void The_wiki_tables_become_mods_and_generic_Unreal_games()
    {
        var wiki = LumaWikiParser.Parse(WikiText());

        Assert.Equal(9, wiki.Mods.Count);
        var prey = wiki.Mods.Single(m => m.Name == "Prey");
        Assert.Equal("Luma-Prey.zip", prey.Asset);
        Assert.Equal("working", prey.Status);
        Assert.Equal("https://www.nexusmods.com/prey2017/mods/149", prey.Links.Nexus);
        Assert.Equal("https://github.com/Filoppi/Luma-Framework/wiki#prey", prey.FeaturesUrl);
        Assert.Equal(["Pumbo"], prey.Authors);
        Assert.Equal("in-progress", wiki.Mods.Single(m => m.Name == "Batman Arkham Knight").Status);
        Assert.Null(wiki.Mods.Single(m => m.Name == "Borderlands 2").Asset);
        Assert.DoesNotContain(wiki.Mods, m => m.Name == "Not Listed Game");
        Assert.Equal(7, wiki.UnrealGames.Count);
    }

    [Fact]
    public void A_Nexus_only_mod_that_needs_dgVoodoo_has_its_versions_and_setup_lines()
    {
        var bl2 = LumaWikiParser.Parse(WikiText()).Mods.Single(m => m.Name == "Borderlands 2");

        Assert.True(bl2.RequiresDgVoodoo);
        Assert.Equal(["2.81.3", "2.87.3"], bl2.DgVoodooVersions);
        Assert.Contains("OutputAPI: d3d11_fl11_0", bl2.DgVoodooSetup);
        Assert.DoesNotContain("setup", bl2.Notes ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.False(LumaWikiParser.Parse(WikiText()).Mods.Single(m => m.Name == "Prey").RequiresDgVoodoo);
    }

    [Fact]
    public void Unreal_rows_carry_support_flags_Engine_ini_keys_and_launch_arguments()
    {
        var games = LumaWikiParser.Parse(WikiText()).UnrealGames;

        var ace = games.Single(g => g.Name.StartsWith("Ace Combat"));
        Assert.Equal("yes", ace.Dlss);
        Assert.Null(ace.Hdr);
        Assert.Equal("4.18", ace.UeVersion);
        Assert.Equal([new IniSetting("SystemSettings", "r.DefaultFeature.AntiAliasing", "2"), new IniSetting("SystemSettings", "r.PostProcessAAQuality", "4")], ace.EngineIni);
        Assert.Equal("In-game: AA \"FXAA High\"", ace.Notes);
        Assert.Equal("blocked", games.Single(g => g.Name == "Abzu").Dlss);
        Assert.Equal("yes", games.Single(g => g.Name == "Abzu").Hdr);
        Assert.Equal("blocked", games.Single(g => g.Name == "Biomutant").Hdr);
        Assert.Equal(["-DX11"], games.Single(g => g.Name == "Abiotic Factor").LaunchArgs);
        Assert.Equal(["-dx11"], games.Single(g => g.Name.StartsWith("Life is Strange")).LaunchArgs);
        Assert.Equal("in-progress", games.Single(g => g.Name.StartsWith("Life is Strange")).Dlss);
        Assert.Contains("Works perfectly", games.Single(g => g.Name.StartsWith("Daymare")).Notes);
        Assert.Empty(games.Single(g => g.Name == "Abzu").LaunchArgs);
    }

    [Fact]
    public async Task Mods_get_the_package_of_their_download_link_or_of_the_same_name_and_unlisted_zips_are_added()
    {
        var section = await Build();

        Assert.Equal(Tag, section.Release);
        Assert.Equal("Prey", section.Mods.Single(m => m.Id == "prey").Package);
        Assert.Equal("Unity_Engine", section.Mods.Single(m => m.Id == "alfred-hitchcock-vertigo").Package);
        Assert.Equal("BioShock_Series", section.Mods.Single(m => m.Id == "bioshock-2-remastered").Package);
        // no GitHub link on the wiki, but the release has a zip of that name
        Assert.Equal("Borderlands_GOTY_Enhanced", section.Mods.Single(m => m.Id == "borderlands-goty-enhanced").Package);
        Assert.Equal("Batman_Arkham_Knight", section.Mods.Single(m => m.Id == "batman-arkham-knight").Package);
        Assert.Null(section.Mods.Single(m => m.Id == "borderlands-2").Package);
        Assert.Null(section.Mods.Single(m => m.Id == "far-cry-5").Package);
        var heavyRain = section.Mods.Single(m => m.Id == "heavy-rain");
        Assert.Null(heavyRain.Status);
        Assert.Equal("Heavy_Rain", heavyRain.Package);
        Assert.DoesNotContain(section.Mods, m => m.Package is "Generic_Mod" or "Graphics_Analyzer");
    }

    [Fact]
    public async Task Only_the_needed_packages_are_downloaded_with_a_file_per_bitness_and_no_test_or_dev_builds()
    {
        var files = Served();
        var section = (await Generator(WikiText(), Release(Tag, Assets), files).BuildAsync(null, Now, default)).Section;

        Assert.Equal(["Batman_Arkham_Knight", "BioShock_Series", "Borderlands_GOTY_Enhanced", "Call_of_Duty_Black_Ops_3", "Heavy_Rain", "Prey", "Unity_Engine", "Unreal_Engine"],
            section.Packages.Select(p => p.Id).Order().ToArray());
        Assert.Equal(["x64", "x86"], section.Packages.Single(p => p.Id == "Unity_Engine").Files.Select(f => f.Arch).ToArray());
        Assert.Equal(["x86"], section.Packages.Single(p => p.Id == "BioShock_Series").Files.Select(f => f.Arch).ToArray());
        Assert.All(section.Packages.SelectMany(p => p.Files), f => { Assert.NotNull(f.Sha256); Assert.Equal(Url(f.Name), f.Url); });
        Assert.DoesNotContain(files.Requested, u => u.Contains("-Test") || u.Contains("-Dev") || u.Contains("Generic_Mod") || u.Contains("Graphics_Analyzer"));
    }

    [Fact]
    public async Task The_zips_are_pinned_and_never_mirrored_even_with_a_mirror_on()
    {
        var mirror = new FakeMirrorSink();

        var section = await Build(mirror);

        Assert.All(section.Packages.SelectMany(p => p.Files), f => Assert.Null(f.MirrorUrl));
        Assert.Null(section.License);
    }

    [Fact]
    public async Task A_new_release_tag_is_a_new_address_and_keeps_the_old_hash_in_the_history()
    {
        var first = await Build();
        var oldHash = first.Packages.Single(p => p.Id == "Prey").Files[0].Sha256;

        var output = await Generator(WikiText(), Release("latest-760", Assets), Served("latest-760"))
            .BuildAsync(first, Now.AddDays(1), default);

        var prey = output.Section.Packages.Single(p => p.Id == "Prey").Files[0];
        Assert.Equal("latest-760", output.Section.Release);
        Assert.Equal(Url("Luma-Prey.zip", "latest-760"), prey.Url);
        Assert.NotEqual(oldHash, prey.Sha256);
        Assert.Contains(prey.History, h => h.Sha256 == oldHash);
    }

    [Fact]
    public async Task A_wiki_link_to_a_zip_the_release_lacks_is_a_warning_and_the_mod_has_no_package()
    {
        var assets = Assets.Where(a => !a.StartsWith("Luma-Prey")).ToArray();

        var output = await Generator(WikiText(), Release(Tag, assets), Served(Tag, assets)).BuildAsync(null, Now, default);

        Assert.Null(output.Section.Mods.Single(m => m.Id == "prey").Package);
        Assert.Contains(output.Warnings, w => w.Contains("Prey") && w.Contains("Luma-Prey.zip"));
    }

    [Fact]
    public async Task A_zip_that_cannot_be_downloaded_is_a_warning_and_keeps_the_previous_entry()
    {
        var first = await Build();
        var files = Served();
        files.Served.Remove(Url("Luma-Prey.zip"));

        var output = await Generator(WikiText(), Release(Tag, Assets), files).BuildAsync(first, Now.AddDays(1), default);

        Assert.Contains(output.Warnings, w => w.Contains("Luma-Prey.zip"));
        Assert.Equal(first.Packages.Single(p => p.Id == "Prey").Files[0].Sha256, output.Section.Packages.Single(p => p.Id == "Prey").Files[0].Sha256);
    }

    [Fact]
    public async Task A_truncated_wiki_an_unreadable_release_or_a_missing_Unreal_zip_fails_the_section()
    {
        var short_ = string.Join('\n', WikiText().Split('\n').Take(6));
        await Assert.ThrowsAsync<SourceException>(() => Generator(short_, Release(Tag, Assets), Served()).BuildAsync(null, Now, default));
        await Assert.ThrowsAsync<SourceException>(() => Generator(WikiText(), "{ not json", Served()).BuildAsync(null, Now, default));
        var noUnreal = Assets.Where(a => !a.Contains("Unreal")).ToArray();
        await Assert.ThrowsAsync<SourceException>(() => Generator(WikiText(), Release(Tag, noUnreal), Served(Tag, noUnreal)).BuildAsync(null, Now, default));
        var gen = new LumaGenerator(new FakeText([]), new FakeText([]), new FileSync(Served(), new FakeLicenceSource([]), null));
        await Assert.ThrowsAsync<SourceException>(() => gen.BuildAsync(null, Now, default));
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        var generator = Generator(WikiText(), Release(Tag, Assets), Served());
        var first = (await generator.BuildAsync(null, Now, default)).Section;

        var second = (await generator.BuildAsync(first, Now.AddDays(1), default)).Section;

        Assert.Equal(JsonSerializer.Serialize(first, CatalogGen.Json.Options), JsonSerializer.Serialize(second, CatalogGen.Json.Options));
    }

    [Fact]
    public async Task The_section_validates_against_the_published_schema_and_its_files_are_listed_for_notices()
    {
        var section = await Build();
        var root = CatalogFile.Load(Path.Combine(_dir, "none.json"));
        CatalogFile.SetSection(root, "luma", section, Now);

        var result = Evaluate(JsonNode.Parse(CatalogFile.Serialize(root))!);

        Assert.True(result.IsValid, string.Join("\n", result.Details.Where(d => !d.IsValid && d.Errors is not null).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))));
        Assert.Equal(section.Packages.Sum(p => p.Files.Count), Program.MirroredFiles(root).Count);
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
