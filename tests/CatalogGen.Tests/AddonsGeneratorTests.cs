using System.Text.Json;
using System.Text.Json.Nodes;
using CatalogGen;
using Json.Schema;

namespace CatalogGen.Tests;

public class AddonsGeneratorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cg-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFiles _files;
    private readonly string _ini = File.ReadAllText(Path.Combine("Fixtures", "Addons.sample.ini"));

    public AddonsGeneratorTests()
    {
        Directory.CreateDirectory(_dir);
        _files = new FakeFiles(_dir);
        const string docs = "https://github.com/crosire/reshade-docs/releases/latest/download/";
        _files.Served[docs + "swapchain_override.addon32"] = TestZip.Pe("32");
        _files.Served[docs + "swapchain_override.addon64"] = TestZip.Pe("64");
        _files.Served["https://github.com/murchalloo/reshade-addons/releases/download/1.0.2/frame_capture.addon"] = TestZip.Pe("fc");
        _files.Served["https://github.com/4lex4nder/ReshadeEffectShaderToggler/releases/latest/download/release.zip"] =
            TestZip.Make("LICENSE", "README.md", "ReshadeEffectShaderToggler.addon32", "ReshadeEffectShaderToggler.addon64", "REST_FLIP.fx", "REST_NOOP.fx");
        const string seri = "https://github.com/cot6/reshade-addons/releases/download/setup-release-reference/";
        _files.Served[seri + "ReShade32-EditorHistory-By-seri14.zip"] = TestZip.Make("LICENSE.md", "ReShade32-EditorHistory-By-seri14.addon32");
        _files.Served[seri + "ReShade64-EditorHistory-By-seri14.zip"] = TestZip.Make("LICENSE.md", "ReShade64-EditorHistory-By-seri14.addon64");
        _files.Served["https://github.com/JoeyAnthony/3DGameBridgeProjects/releases/latest/download/srReshade.zip"] =
            TestZip.Make("LICENSE.txt", "DO_NOT_ENABLE.addonfx", "srReshade_v2.1.0.addon32", "srReshade_v2.1.0.addon64");
        _files.Served["https://github.com/FransBouma/IgcsConnector/releases/download/v260/IGCSConnector_v260.zip"] =
            TestZip.Make("IgcsConnector.addon64", "Readme.txt", "Shaders/", "Shaders/IgcsDof.fx");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private AddonsGenerator Generator(Dictionary<string, string?>? licences = null, IMirror? mirror = null, string? ini = null, int min = 1) =>
        new(new FakeText(new() { [AddonsIni.Url] = ini ?? _ini }),
            new FileSync(_files, new FakeLicenceSource(licences ?? new()), mirror), minPackages: min);

    private async Task<AddonsSection> Build(Dictionary<string, string?>? licences = null, IMirror? mirror = null, AddonsSection? previous = null, DateTimeOffset? at = null) =>
        (await Generator(licences, mirror).BuildAsync(previous, at ?? Now, default)).Section;

    [Fact]
    public async Task Every_active_package_becomes_an_entry_in_list_order()
    {
        var s = await Build();

        Assert.Equal(AddonsIni.Url, s.Source);
        Assert.Equal(["swap-chain-override-by-crosire", "frame-capture-by-murchalloo", "reshadeeffectshadertoggler-rest-by-4lex4nder", "pyhook-by-dwojtasik",
            "editor-history-by-seri14", "renodx-by-shortfuse", "3dgamebridge-by-janthony-dinnerbram", "display-commander-by-pmnoxx",
            "igcs-connector-by-otis-inf", "broken-download-by-nobody"], s.Addons.Select(a => a.Id).ToArray());
    }

    [Fact]
    public async Task Names_are_split_into_name_and_author()
    {
        var s = await Build();

        var a = s.Addons.Single(x => x.Id == "swap-chain-override-by-crosire");
        Assert.Equal("Swap chain override", a.Name);
        Assert.Equal("crosire", a.Author);
        Assert.Equal("https://github.com/crosire/reshade/tree/main/examples/16-swapchain_override", a.RepositoryUrl);
        Assert.Equal("Display Commander is advanced fps limiter, window control, OSD, and more.", s.Addons.Single(x => x.Id.StartsWith("display")).Description);
    }

    [Fact]
    public async Task Separate_downloads_per_bitness_keep_their_arch_and_get_a_hash()
    {
        var a = (await Build()).Addons.Single(x => x.Id == "swap-chain-override-by-crosire");

        Assert.Equal([("swapchain_override.addon32", "x86"), ("swapchain_override.addon64", "x64")], a.Files.Select(f => (f.Name, f.Arch)).ToArray());
        Assert.All(a.Files, f => Assert.Equal(64, f.Sha256!.Length));
        Assert.Equal("addon", a.Files[0].Payload!.Single().Kind);
        Assert.Equal("x86", a.Files[0].Payload!.Single().Arch);
        Assert.Null(a.Manual);
    }

    [Fact]
    public async Task One_zip_listed_for_both_bitnesses_is_one_file_with_arch_both()
    {
        var a = (await Build()).Addons.Single(x => x.Id.StartsWith("3dgamebridge"));

        var f = Assert.Single(a.Files);
        Assert.Equal("both", f.Arch);
        Assert.Equal(["srReshade_v2.1.0.addon32", "srReshade_v2.1.0.addon64"], f.Payload!.Select(p => p.Entry).ToArray());
    }

    [Fact]
    public async Task A_zip_records_the_addons_and_effects_the_app_installs()
    {
        var s = await Build();

        var rest = s.Addons.Single(x => x.Id.StartsWith("reshadeeffect")).Files.Single();
        Assert.Equal(["ReshadeEffectShaderToggler.addon32", "ReshadeEffectShaderToggler.addon64", "REST_FLIP.fx", "REST_NOOP.fx"], rest.Payload!.Select(p => p.Entry).ToArray());
        Assert.Equal(["REST/REST_FLIP.fx", "REST/REST_NOOP.fx"], rest.Payload!.Where(p => p.Kind == "effect").Select(p => p.Dest!).ToArray());
        Assert.Equal(@".\reshade-shaders\Shaders\REST", s.Addons.Single(x => x.Id.StartsWith("reshadeeffect")).EffectInstallPath);
        var igcs = s.Addons.Single(x => x.Id.StartsWith("igcs")).Files.Single();
        Assert.Equal("IgcsDof.fx", igcs.Payload!.Single(p => p.Kind == "effect").Dest);
    }

    [Fact]
    public async Task A_bare_addon_file_takes_the_bitness_of_its_list_key()
    {
        var f = (await Build()).Addons.Single(x => x.Id.StartsWith("frame-capture")).Files.Single();

        Assert.Equal("x64", f.Arch);
        Assert.Equal("x64", f.Payload!.Single().Arch);
    }

    [Fact]
    public async Task Packages_the_app_manages_as_components_are_marked_and_list_no_files()
    {
        var s = await Build();

        var reno = s.Addons.Single(x => x.Id.StartsWith("renodx"));
        Assert.Equal("renodx", reno.ManagedBy);
        Assert.Empty(reno.Files);
        Assert.Null(reno.Manual);
        var dc = s.Addons.Single(x => x.Id.StartsWith("display-commander"));
        Assert.Equal("display-commander", dc.ManagedBy);
        Assert.Empty(dc.Files);
        Assert.DoesNotContain(_files.Requested, u => u.Contains("display-commander") || u.Contains("clshortfuse"));
    }

    [Theory]
    [InlineData("Some Re-Limiter by x", "https://github.com/x/relimiter", "relimiter")]
    [InlineData("Deep Fried Chicken by y", null, "dfc")]
    [InlineData("Lecram DLSS5 helper", null, "lecram-dlss5")]
    [InlineData("GITC Uplift", null, "gitc-uplift")]
    [InlineData("DLSS5-Feeder by z", null, "dlss5-feeder")]
    [InlineData("ShaderToggler by Otis_Inf", "https://github.com/FransBouma/ShaderToggler", null)]
    public void Managed_components_are_recognised_by_name_or_repository(string name, string? repo, string? expected)
    {
        Assert.Equal(expected, ManagedComponents.For(new AddonSource("00", name, "", null, null, null, repo, null)));
    }

    [Fact]
    public async Task A_package_without_a_download_is_listed_as_manual()
    {
        var a = (await Build()).Addons.Single(x => x.Id.StartsWith("pyhook"));

        Assert.Empty(a.Files);
        Assert.Contains("No download", a.Manual);
        Assert.Equal("https://github.com/dwojtasik/PyHook", a.RepositoryUrl);
    }

    [Fact]
    public async Task A_zip_with_files_the_app_does_not_place_is_manual()
    {
        var a = (await Build()).Addons.Single(x => x.Id.StartsWith("3dgamebridge"));

        Assert.Contains("DO_NOT_ENABLE.addonfx", a.Manual);
        Assert.Equal(["DO_NOT_ENABLE.addonfx"], a.Files.Single().Unplaced);
    }

    [Fact]
    public async Task A_dead_download_is_a_warning_and_leaves_the_package_manual_or_with_its_other_file()
    {
        _files.Served.Remove("https://github.com/cot6/reshade-addons/releases/download/setup-release-reference/ReShade32-EditorHistory-By-seri14.zip");

        var output = await Generator().BuildAsync(null, Now, default);

        var seri = output.Section.Addons.Single(x => x.Id.StartsWith("editor-history"));
        Assert.Equal(["x64"], seri.Files.Select(f => f.Arch).ToArray());
        Assert.Null(seri.Manual);
        Assert.Contains(output.Warnings, w => w.Contains("ReShade32-EditorHistory"));
        var broken = output.Section.Addons.Single(x => x.Id.StartsWith("broken"));
        Assert.Empty(broken.Files);
        Assert.Contains("could not be read", broken.Manual);
        Assert.Empty(output.Errors);
    }

    [Fact]
    public void A_download_that_is_not_a_binary_or_zip_never_becomes_a_build()
    {
        // The real fetcher refuses what DownloadCheck refuses; FakeFiles would hand out any bytes.
        var path = Path.Combine(_dir, "page.html");
        File.WriteAllBytes(path, "<html>404</html>"u8.ToArray());

        Assert.NotNull(DownloadCheck.Problem(path, "<htm"u8, requiredEntry: null));
    }

    [Fact]
    public void Any_readable_zip_with_files_passes_the_check_when_no_entry_is_required()
    {
        var zip = Path.Combine(_dir, "ok.zip");
        File.WriteAllBytes(zip, TestZip.Make("a/b.txt"));
        var empty = Path.Combine(_dir, "empty.zip");
        File.WriteAllBytes(empty, TestZip.Make("dir/"));

        Assert.Null(DownloadCheck.Problem(zip, "PK\u0003\u0004"u8, requiredEntry: null));
        Assert.NotNull(DownloadCheck.Problem(zip, "PK\u0003\u0004"u8));
        Assert.NotNull(DownloadCheck.Problem(empty, "PK\u0003\u0004"u8, requiredEntry: null));
    }

    [Fact]
    public async Task A_permissive_licence_is_mirrored_and_anything_else_is_pinned()
    {
        var mirror = new FakeMirrorSink();
        var s = await Build(new()
        {
            ["cot6/reshade-addons"] = "BSD-3-Clause",
            ["4lex4nder/ReshadeEffectShaderToggler"] = "GPL-3.0",
            ["crosire/reshade-docs"] = null,
        }, mirror);

        var seri = s.Addons.Single(x => x.Id.StartsWith("editor-history"));
        Assert.All(seri.Files, f => Assert.StartsWith("https://mirror.test/mirror-2026-10/", f.MirrorUrl));
        Assert.Equal("BSD-3-Clause", seri.License);
        var rest = s.Addons.Single(x => x.Id.StartsWith("reshadeeffect"));
        Assert.Null(rest.Files.Single().MirrorUrl);
        Assert.Equal("GPL-3.0", rest.License);
        var crosire = s.Addons.Single(x => x.Id.StartsWith("swap-chain"));
        Assert.All(crosire.Files, f => Assert.Null(f.MirrorUrl));
        Assert.Null(crosire.License);
        Assert.DoesNotContain(mirror.Uploads, n => n.Contains("swapchain") || n.Contains("release.zip"));
    }

    [Fact]
    public async Task A_changed_download_keeps_the_old_build_in_the_history_and_a_second_run_changes_nothing()
    {
        var first = await Build();
        var again = await Build(previous: first, at: Now.AddHours(6));
        Assert.Equal(JsonSerializer.Serialize(first, CatalogGen.Json.Options), JsonSerializer.Serialize(again, CatalogGen.Json.Options));

        _files.Served["https://github.com/murchalloo/reshade-addons/releases/download/1.0.2/frame_capture.addon"] = TestZip.Pe("fc2");
        var changed = await Build(previous: first, at: Now.AddDays(8));

        var before = first.Addons.Single(x => x.Id.StartsWith("frame-capture")).Files.Single();
        var after = changed.Addons.Single(x => x.Id.StartsWith("frame-capture")).Files.Single();
        Assert.NotEqual(before.Sha256, after.Sha256);
        Assert.Equal(before.Sha256, after.History.Single().Sha256);
    }

    [Fact]
    public async Task A_build_recorded_without_a_payload_is_read_again()
    {
        var first = await Build();
        foreach (var f in first.Addons.SelectMany(a => a.Files)) f.Payload = null;

        var again = await Build(previous: first, at: Now.AddHours(1));

        Assert.All(again.Addons.SelectMany(a => a.Files), f => Assert.NotNull(f.Payload));
    }

    [Fact]
    public async Task A_list_that_looks_truncated_or_is_unreachable_is_a_source_error()
    {
        await Assert.ThrowsAsync<SourceException>(() => Generator(min: 50).BuildAsync(null, Now, default));
        var gen = new AddonsGenerator(new FakeText(new()), new FileSync(_files, new FakeLicenceSource(new()), null));
        await Assert.ThrowsAsync<SourceException>(() => gen.BuildAsync(null, Now, default));
    }

    [Fact]
    public async Task A_list_that_lost_more_than_a_fifth_of_the_packages_is_refused_unless_accepted()
    {
        var previous = await Build();
        var small = "[00]\nPackageName=Only one by me\nRepositoryUrl=https://example.com\n";

        await Assert.ThrowsAsync<SourceException>(() => Generator(ini: small).BuildAsync(previous, Now, default));

        var accepting = new AddonsGenerator(new FakeText(new() { [AddonsIni.Url] = small }), new FileSync(_files, new FakeLicenceSource(new()), null), acceptShrink: true, minPackages: 1);
        Assert.Single((await accepting.BuildAsync(previous, Now, default)).Section.Addons);
    }

    [Fact]
    public async Task Ids_stay_unique_when_two_packages_share_a_name()
    {
        var ini = "[00]\nPackageName=Same by a\n[01]\nPackageName=Same by a\n[02]\nPackageName=Same by a\n";

        var s = (await Generator(ini: ini).BuildAsync(null, Now, default)).Section;

        Assert.Equal(["same-by-a", "same-by-a-2", "same-by-a-3"], s.Addons.Select(a => a.Id).ToArray());
    }

    [Fact]
    public async Task A_download_address_that_is_not_https_is_skipped_with_a_warning()
    {
        var ini = "[00]\nPackageName=Plain by a\nDownloadUrl64=http://example.com/a.addon64\n";

        var output = await Generator(ini: ini).BuildAsync(null, Now, default);

        Assert.Empty(output.Section.Addons.Single().Files);
        Assert.Contains(output.Warnings, w => w.Contains("not an https URL"));
    }

    [Fact]
    public async Task The_section_validates_against_the_published_schema()
    {
        var s = await Build(new() { ["cot6/reshade-addons"] = "BSD-3-Clause" }, new FakeMirrorSink());
        var root = CatalogFile.Load(Path.Combine(_dir, "none.json"));
        CatalogFile.SetSection(root, "addons", s, Now);

        var result = SchemaCheck.Evaluate(JsonNode.Parse(CatalogFile.Serialize(root))!);

        Assert.True(result.IsValid, SchemaCheck.Errors(result));
    }
}

public static class SchemaCheck
{
    public static EvaluationResults Evaluate(JsonNode doc)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "schema", "catalog.schema.json");
            if (File.Exists(candidate))
                return JsonSchema.FromText(File.ReadAllText(candidate)).Evaluate(doc, new EvaluationOptions { OutputFormat = OutputFormat.List });
        }
        throw new FileNotFoundException("schema/catalog.schema.json not found above " + AppContext.BaseDirectory);
    }

    public static string Errors(EvaluationResults result) =>
        string.Join("\n", result.Details.Where(d => !d.IsValid && d.Errors is not null).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")));
}
