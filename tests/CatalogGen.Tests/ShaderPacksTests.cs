using System.Text.Json;
using System.Text.Json.Nodes;
using CatalogGen;

namespace CatalogGen.Tests;

public class PackLayoutScanTests
{
    [Fact]
    public void A_github_archive_has_its_shaders_and_textures_below_one_wrapper_folder()
    {
        var l = PackLayoutScan.Analyse(["repo-abc/", "repo-abc/README.md", "repo-abc/Shaders/A.fx", "repo-abc/Shaders/inc/A.fxh",
            "repo-abc/Textures/lut.png", "repo-abc/Textures/n/noise.png", "repo-abc/docs/shot.png"]);

        Assert.Equal("repo-abc/Shaders/", l.ShadersDir);
        Assert.Equal("repo-abc/Textures/", l.TexturesDir);
        Assert.Equal(2, l.ShaderFiles);
        Assert.Equal(2, l.TextureFiles);
        Assert.Equal(["A.fx"], l.Effects);
    }

    [Fact]
    public void A_zip_without_a_wrapper_works_and_a_pack_without_textures_has_none()
    {
        var l = PackLayoutScan.Analyse(["Shaders/A.fx"]);

        Assert.Equal("Shaders/", l.ShadersDir);
        Assert.Null(l.TexturesDir);
        Assert.Equal(0, l.TextureFiles);
    }

    [Fact]
    public void Folder_names_match_without_case_and_without_stray_spaces()
    {
        var l = PackLayoutScan.Analyse(["repo/shaders/A.fx", "repo/TEXTURES/t.png"]);
        var spaced = PackLayoutScan.Analyse(["repo/Shaders /A.fx", "repo/Textures/t.png"]);

        Assert.Equal("repo/shaders/", l.ShadersDir);
        Assert.Equal("repo/TEXTURES/", l.TexturesDir);
        Assert.Equal("repo/Shaders /", spaced.ShadersDir);
        Assert.Equal("repo/Textures/", spaced.TexturesDir);
    }

    [Fact]
    public void The_shaders_folder_with_the_most_effects_wins_and_textures_must_be_its_sibling()
    {
        var l = PackLayoutScan.Analyse(["repo/Other/Shaders/x.fx", "repo/Other/Textures/other.png", "repo/Shaders/a.fx", "repo/Shaders/b.fx", "repo/Textures/mine.png"]);

        Assert.Equal("repo/Shaders/", l.ShadersDir);
        Assert.Equal("repo/Textures/", l.TexturesDir);
    }

    [Fact]
    public void A_shaders_folder_without_effect_files_is_not_a_shader_pack()
    {
        var ex = Assert.Throws<InvalidDataException>(() => PackLayoutScan.Analyse(["repo/Shaders/readme.md", "repo/code/a.cpp"]));

        Assert.Contains("no Shaders folder", ex.Message);
    }

    [Fact]
    public void Entries_that_climb_out_of_the_zip_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => PackLayoutScan.Analyse(["Shaders/A.fx", "../evil.dll"]));
    }

    [Fact]
    public void The_layout_is_read_from_a_real_zip_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "cg-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            File.WriteAllBytes(path, TestZip.Make("r/Shaders/A.fx", "r/Textures/t.png"));
            Assert.Equal("r/Shaders/", PackLayoutScan.AnalyseZip(path).ShadersDir);
        }
        finally { File.Delete(path); }
    }
}

public class ShaderPackListTests
{
    private static string Pack(string id = "a-pack", string repo = "o/r", string kind = "branch", string? branch = "main", string category = "hdr") =>
        $$"""{ "id": "{{id}}", "name": "A", "repo": "{{repo}}", "category": "{{category}}", "source": { "kind": "{{kind}}"{{(branch is null ? "" : $", \"branch\": \"{branch}\"")}} } }""";

    private static string List(params string[] packs) => $$"""{ "schema": 1, "packs": [ {{string.Join(",", packs)}} ] }""";

    [Fact]
    public void The_committed_list_is_valid_and_seeded_from_public_repositories()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "shader-packs.json");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "shader-packs.json"))) { path = Path.Combine(dir.FullName, "shader-packs.json"); break; }

        var list = ShaderPackLists.Load(path);

        var repos = list.Packs.Select(p => p.Repo).ToList();
        Assert.Contains("EndlesslyFlowering/ReShade_HDR_shaders", repos);
        Assert.Contains("Filoppi/PumboAutoHDR", repos);
        Assert.Contains("clshortfuse/renofx", repos);
        Assert.Contains("crosire/reshade-shaders", repos);
        Assert.Equal(["lilium-hdr", "pumbo-autohdr", "renofx"], list.Packs.Where(p => p.Default).Select(p => p.Id).ToArray());
        Assert.All(list.Packs.Where(p => p.Default), p => Assert.Equal("hdr", p.Category));
        Assert.All(list.Packs.Where(p => !p.Default), p => Assert.False(p.Default));
    }

    [Fact]
    public void A_valid_list_loads()
    {
        var list = ShaderPackLists.Parse(List(Pack(), Pack("b-pack", kind: "release-asset", branch: null)));

        Assert.Equal(2, list.Packs.Count);
        Assert.Equal("branch", list.Packs[0].Source.Kind);
        Assert.Equal("main", list.Packs[0].Source.Branch);
        Assert.Equal("release-asset", list.Packs[1].Source.Kind);
    }

    [Theory]
    [InlineData("Upper")]
    [InlineData("with space")]
    [InlineData("")]
    [InlineData("-lead")]
    public void An_id_must_be_a_lowercase_slug(string id)
    {
        Assert.Throws<SourceException>(() => ShaderPackLists.Parse(List(Pack(id: id))));
    }

    [Theory]
    [InlineData("norepo")]
    [InlineData("a/b/c")]
    [InlineData("https://github.com/a/b")]
    public void A_repo_must_be_owner_slash_name(string repo)
    {
        Assert.Throws<SourceException>(() => ShaderPackLists.Parse(List(Pack(repo: repo))));
    }

    [Fact]
    public void Duplicate_ids_unknown_categories_unknown_kinds_and_branchless_sources_are_refused()
    {
        Assert.Throws<SourceException>(() => ShaderPackLists.Parse(List(Pack(), Pack())));
        Assert.Throws<SourceException>(() => ShaderPackLists.Parse(List(Pack(category: "misc"))));
        Assert.Throws<SourceException>(() => ShaderPackLists.Parse(List(Pack(kind: "tarball"))));
        Assert.Throws<SourceException>(() => ShaderPackLists.Parse(List(Pack(branch: null))));
    }

    [Fact]
    public void An_empty_or_broken_list_is_a_source_error()
    {
        Assert.Throws<SourceException>(() => ShaderPackLists.Parse(List()));
        Assert.Throws<SourceException>(() => ShaderPackLists.Parse("{ nope"));
        Assert.Throws<SourceException>(() => ShaderPackLists.Load(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".json")));
    }
}

public class ShaderPacksGeneratorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private const string Sha1 = "0123456789abcdef0123456789abcdef01234567";
    private const string Sha2 = "fedcba9876543210fedcba9876543210fedcba98";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cg-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFiles _files;
    private readonly Dictionary<string, string> _api = new();

    public ShaderPacksGeneratorTests()
    {
        Directory.CreateDirectory(_dir);
        _files = new FakeFiles(_dir);
        Branch("o/lilium", "master", Sha1, "lilium-" + Sha1[..7]);
        Branch("o/pumbo", "main", Sha1, "pumbo-" + Sha1[..7]);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private void Branch(string repo, string branch, string sha, string wrapper, params string[] extra)
    {
        _api[$"https://api.github.com/repos/{repo}/commits/{branch}"] = JsonSerializer.Serialize(new { sha });
        _files.Served[$"https://github.com/{repo}/archive/{sha}.zip"] =
            TestZip.Make([wrapper + "/", wrapper + "/Shaders/A.fx", wrapper + "/Shaders/inc/A.fxh", wrapper + "/Textures/t.png", wrapper + "/README.md", .. extra]);
    }

    private static PackSource Source(string id, string repo, string branch = "master", string category = "general", bool isDefault = false, string? license = null) => new()
    {
        Id = id, Name = id.ToUpperInvariant(), Repo = repo, Category = category, Default = isDefault, License = license,
        Source = new PackSourceKind { Kind = "branch", Branch = branch },
    };

    private static ShaderPackList List(params PackSource[] packs) => new() { Packs = [.. packs] };

    private ShaderPacksGenerator Generator(Dictionary<string, string?>? licences = null, IMirror? mirror = null, Dictionary<string, string>? verified = null) =>
        new(new FakeText(_api), new FileSync(_files, new VerifiedLicences(new FakeLicenceSource(licences ?? new()), verified ?? new()), mirror));

    [Fact]
    public async Task A_branch_pack_is_pinned_to_the_commit_and_its_zip_is_hashed()
    {
        var output = await Generator().BuildAsync(List(Source("lilium", "o/lilium", isDefault: true, category: "hdr")), null, Now, default);

        var p = Assert.Single(output.Section.Packs);
        Assert.Equal(("lilium", "branch", "master", Sha1, "0123456"), (p.Id, p.Source, p.Ref, p.Commit, p.Version));
        Assert.True(p.Default);
        Assert.Equal("hdr", p.Category);
        Assert.Equal("https://github.com/o/lilium", p.Homepage);
        Assert.Equal($"https://github.com/o/lilium/archive/{Sha1}.zip", p.File!.Url);
        Assert.Equal("lilium-0123456.zip", p.File.Name);
        Assert.Equal(64, p.File.Sha256!.Length);
        Assert.Equal("lilium-0123456/Shaders/", p.File.Layout!.ShadersDir);
        Assert.Equal("lilium-0123456/Textures/", p.File.Layout.TexturesDir);
        Assert.Empty(output.Errors);
    }

    [Fact]
    public async Task A_release_asset_pack_uses_the_latest_release_tag_and_the_one_matching_zip()
    {
        _api["https://api.github.com/repos/o/rel/releases/latest"] = JsonSerializer.Serialize(new
        {
            tag_name = "v4.2",
            assets = new object[]
            {
                new { name = "notes.txt", browser_download_url = "https://github.com/o/rel/releases/download/v4.2/notes.txt" },
                new { name = "Pack-v4.2.zip", browser_download_url = "https://github.com/o/rel/releases/download/v4.2/Pack-v4.2.zip" },
                new { name = "Pack-v4.2.7z", browser_download_url = "https://github.com/o/rel/releases/download/v4.2/Pack-v4.2.7z" },
            },
        });
        _files.Served["https://github.com/o/rel/releases/download/v4.2/Pack-v4.2.zip"] = TestZip.Make("Shaders/R.fx");
        var src = new PackSource { Id = "rel", Name = "Rel", Repo = "o/rel", Category = "general", Source = new PackSourceKind { Kind = "release-asset", Asset = "Pack-*.zip" } };

        var p = (await Generator().BuildAsync(List(src), null, Now, default)).Section.Packs.Single();

        Assert.Equal(("release-asset", "v4.2", "v4.2", null), (p.Source, p.Ref, p.Version, p.Commit));
        Assert.Equal("Pack-v4.2.zip", p.File!.Name);
        Assert.Equal("Shaders/", p.File.Layout!.ShadersDir);
    }

    [Fact]
    public async Task A_release_with_no_single_matching_asset_is_a_warning_and_the_pack_is_left_out()
    {
        _api["https://api.github.com/repos/o/rel/releases/latest"] = JsonSerializer.Serialize(new
        {
            tag_name = "v1",
            assets = new object[] { new { name = "a.zip", browser_download_url = "https://x/a.zip" }, new { name = "b.zip", browser_download_url = "https://x/b.zip" } },
        });
        var rel = new PackSource { Id = "rel", Name = "Rel", Repo = "o/rel", Category = "general", Source = new PackSourceKind { Kind = "release-asset" } };

        var output = await Generator().BuildAsync(List(Source("lilium", "o/lilium"), rel), null, Now, default);

        Assert.Equal(["lilium"], output.Section.Packs.Select(p => p.Id).ToArray());
        Assert.Contains(output.Warnings, w => w.StartsWith("rel:") && w.Contains("2 assets"));
    }

    [Fact]
    public async Task A_permissive_licence_is_mirrored_and_a_copyleft_or_missing_one_is_pinned()
    {
        var mirror = new FakeMirrorSink();
        var output = await Generator(new() { ["o/lilium"] = "GPL-3.0", ["o/pumbo"] = "MIT" }, mirror)
            .BuildAsync(List(Source("lilium", "o/lilium"), Source("pumbo", "o/pumbo", "main")), null, Now, default);

        var lilium = output.Section.Packs.Single(p => p.Id == "lilium");
        var pumbo = output.Section.Packs.Single(p => p.Id == "pumbo");
        Assert.Null(lilium.File!.MirrorUrl);
        Assert.Equal("GPL-3.0", lilium.License);
        Assert.StartsWith("https://mirror.test/mirror-2026-10/", pumbo.File!.MirrorUrl);
        Assert.Equal("MIT", pumbo.License);
        Assert.Equal(["pumbo-0123456.zip"], mirror.Uploads.ToArray());
    }

    [Fact]
    public async Task A_licence_checked_by_hand_is_used_only_when_github_reports_none()
    {
        var mirror = new FakeMirrorSink();
        var output = await Generator(new() { ["o/lilium"] = null, ["o/pumbo"] = "GPL-3.0" }, mirror, new() { ["o/lilium"] = "MIT", ["o/pumbo"] = "MIT" })
            .BuildAsync(List(Source("lilium", "o/lilium", license: "MIT"), Source("pumbo", "o/pumbo", "main", license: "MIT")), null, Now, default);

        var lilium = output.Section.Packs.Single(p => p.Id == "lilium");
        var pumbo = output.Section.Packs.Single(p => p.Id == "pumbo");
        Assert.Equal("MIT", lilium.License);
        Assert.NotNull(lilium.File!.MirrorUrl);
        Assert.Equal("GPL-3.0", pumbo.License);
        Assert.Null(pumbo.File!.MirrorUrl);
        Assert.Contains(output.Warnings, w => w.StartsWith("pumbo:") && w.Contains("GPL-3.0"));
    }

    [Fact]
    public async Task A_new_commit_gives_a_new_build_and_keeps_the_old_one_in_the_history()
    {
        var gen = Generator();
        var first = (await gen.BuildAsync(List(Source("lilium", "o/lilium")), null, Now, default)).Section;
        Branch("o/lilium", "master", Sha2, "lilium-" + Sha2[..7], "lilium-" + Sha2[..7] + "/Shaders/B.fx");

        var next = (await gen.BuildAsync(List(Source("lilium", "o/lilium")), first, Now.AddHours(6), default)).Section.Packs.Single();

        Assert.Equal(("fedcba9", Sha2), (next.Version, next.Commit));
        Assert.Equal(3, next.File!.Layout!.ShaderFiles);
        Assert.Equal(first.Packs[0].File!.Sha256, next.File.History.Single().Sha256);
        Assert.Equal(first.Packs[0].File!.Version, next.File.History.Single().Version);
    }

    [Fact]
    public async Task The_same_commit_again_changes_nothing()
    {
        var gen = Generator();
        var first = (await gen.BuildAsync(List(Source("lilium", "o/lilium")), null, Now, default)).Section;

        var again = (await gen.BuildAsync(List(Source("lilium", "o/lilium")), first, Now.AddHours(6), default)).Section;

        Assert.Equal(JsonSerializer.Serialize(first, CatalogGen.Json.Options), JsonSerializer.Serialize(again, CatalogGen.Json.Options));
    }

    [Fact]
    public async Task A_repository_that_cannot_be_resolved_keeps_its_last_good_pack()
    {
        var gen = Generator();
        var first = (await gen.BuildAsync(List(Source("lilium", "o/lilium"), Source("pumbo", "o/pumbo", "main")), null, Now, default)).Section;
        _api.Remove("https://api.github.com/repos/o/pumbo/commits/main");

        var output = await gen.BuildAsync(List(Source("lilium", "o/lilium"), Source("pumbo", "o/pumbo", "main")), first, Now.AddHours(6), default);

        Assert.Equal(["lilium", "pumbo"], output.Section.Packs.Select(p => p.Id).ToArray());
        Assert.Equal(first.Packs[1].File!.Sha256, output.Section.Packs[1].File!.Sha256);
        Assert.Contains(output.Warnings, w => w.StartsWith("pumbo:"));
    }

    [Fact]
    public async Task A_zip_without_effect_files_is_refused_and_the_last_good_build_stays()
    {
        var gen = Generator();
        var first = (await gen.BuildAsync(List(Source("lilium", "o/lilium")), null, Now, default)).Section;
        _api["https://api.github.com/repos/o/lilium/commits/master"] = JsonSerializer.Serialize(new { sha = Sha2 });
        _files.Served[$"https://github.com/o/lilium/archive/{Sha2}.zip"] = TestZip.Make("r/docs/readme.md");

        var output = await gen.BuildAsync(List(Source("lilium", "o/lilium")), first, Now.AddHours(6), default);

        var p = output.Section.Packs.Single();
        Assert.Equal("0123456", p.Version);
        Assert.Equal(Sha1, p.Commit);
        Assert.Equal(first.Packs[0].File!.Sha256, p.File!.Sha256);
        Assert.Contains(output.Warnings, w => w.Contains("no Shaders folder"));
    }

    [Fact]
    public async Task A_run_where_nothing_could_be_built_is_a_source_error()
    {
        _api.Clear();

        await Assert.ThrowsAsync<SourceException>(() => Generator().BuildAsync(List(Source("lilium", "o/lilium")), null, Now, default));
    }

    [Fact]
    public async Task The_section_validates_against_the_published_schema()
    {
        var output = await Generator(new() { ["o/lilium"] = "GPL-3.0", ["o/pumbo"] = "MIT" }, new FakeMirrorSink())
            .BuildAsync(List(Source("lilium", "o/lilium", category: "hdr", isDefault: true), Source("pumbo", "o/pumbo", "main")), null, Now, default);
        var root = CatalogFile.Load(Path.Combine(_dir, "none.json"));
        CatalogFile.SetSection(root, "shaderPacks", output.Section, Now);

        var result = SchemaCheck.Evaluate(JsonNode.Parse(CatalogFile.Serialize(root))!);

        Assert.True(result.IsValid, SchemaCheck.Errors(result));
        Assert.True((bool)root["sections"]!["shaderPacks"]!["packs"]![0]!["default"]!);
    }
}
