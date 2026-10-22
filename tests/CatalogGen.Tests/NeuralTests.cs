using System.Text.Json;
using System.Text.Json.Nodes;
using CatalogGen;
using Json.Schema;

namespace CatalogGen.Tests;

public class NeuralTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cg-" + Guid.NewGuid().ToString("N"));

    public NeuralTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static byte[] DlssNr(int build) =>
        TestPe.Make(internalName: "DLSSNR", originalName: "CL 38718415", fileVersion: [310, 8, build, 0], fileVersionText: $"310,8,{build},0");

    private static byte[] Lecram10(string label, int[]? fileVersion = null) =>
        TestPe.Make(originalName: "renodx-dlss5.addon64", fileVersion: fileVersion ?? [0, 2026, 926, 210], extra: "\0RenoDX.DLSS5\0v6.1.0\0" + label + "\0");

    private static byte[] Lecram11(string label) =>
        TestPe.Make(extra: "\0RenoDX.DLSS5\0NR-SETTINGS\0NR-COMPAT\0v6.1.0\0" + label + "\0");

    private static string Url(string tag, string asset) => $"https://github.com/RankFTW/rhi-repo/releases/download/{tag}/{asset}";

    private static string Release(string tag, string published, string asset) => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        draft = false,
        prerelease = false,
        published_at = published,
        assets = new[] { new { name = asset, browser_download_url = Url(tag, asset) } },
    });

    private static string List(params string[] releases) => "[" + string.Join(",", releases) + "]";

    private sealed class Releases(string json) : ITextSource
    {
        public Task<string> GetStringAsync(string url, CancellationToken ct) => Task.FromResult(json);
    }

    private sealed class FailingMirror : IMirror
    {
        public Task<string> PublishAsync(string fileName, string sha256, string month, string path, CancellationToken ct) => throw new HttpRequestException("500");

        public Task SetNotesAsync(string month, string body, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeIncoming(params IncomingAsset[] assets) : IIncoming
    {
        public Task<List<IncomingAsset>> ListAsync(CancellationToken ct) => Task.FromResult(assets.ToList());

        public Task DeleteAsync(long assetId, CancellationToken ct) => Task.CompletedTask;
    }

    private NeuralGenerator Generator(string json, FakeFiles files, IMirror mirror, int keep = 20) => new(new Releases(json), files, mirror, _dir, keep);

    private Drop MakeDrop(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        return new Drop(new IncomingAsset(1, name, "https://x/" + name), path, sha, bytes.Length, NeuralIdentify.Identify(path, name)!);
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ---------- reading and identifying ----------

    [Fact]
    public void The_version_resource_is_read_without_Windows()
    {
        var pe = PeInfo.ReadVersion(Write("a.dll", DlssNr(3)))!;

        Assert.Equal("DLSSNR", pe.Get("InternalName"));
        Assert.Equal("CL 38718415", pe.Get("OriginalFilename"));
        Assert.Equal("310.8.3", pe.FileVersion);
    }

    [Fact]
    public void A_file_without_a_version_resource_reads_as_null() =>
        Assert.Null(PeInfo.ReadVersion(Write("plain.bin", TestZip.Pe("plain"))));

    [Fact]
    public void The_dlssnr_dll_is_known_by_its_version_resource_not_its_name() =>
        Assert.Equal(new Identified(NeuralKind.DlssNr, "nvngx_dlssnr.dll", "310.8.3", null, "310.8.3"), NeuralIdentify.Identify(Write("a", DlssNr(3)), "whatever.dll"));

    [Fact]
    public void Lecram_v10_names_itself_in_the_version_resource_and_v11_by_its_strings()
    {
        var v10 = Write("v10", Lecram10("v8.5.0-rc10"));
        var v11 = Write("v11", Lecram11("v11.0.2"));

        Assert.Equal(new Identified(NeuralKind.Lecram, "renodx-dlss5.addon64", "v8.5.0-rc10", "v10", "0.2026.926.210"), NeuralIdentify.Identify(v10, "drop(1).bin"));
        Assert.Equal(new Identified(NeuralKind.Lecram, "dlss5-nr.addon64", "v11.0.2", "v11"), NeuralIdentify.Identify(v11, "drop(2).bin"));
    }

    [Fact]
    public void ShortFuses_add_on_is_known_by_name_and_other_files_are_not_known()
    {
        var sf = Write("sf", TestZip.Pe("sf"));
        var other = Write("other", TestPe.Make(internalName: "SOMETHING", originalName: "tool.exe"));
        var text = Write("text", "hello"u8.ToArray());

        Assert.Equal(NeuralKind.RenodxDlss, NeuralIdentify.Identify(sf, "renodx-dlss.addon64")!.Kind);
        Assert.Null(NeuralIdentify.Identify(sf, "renodx-dlss5.addon64"));
        Assert.Null(NeuralIdentify.Identify(other, "tool.exe"));
        Assert.Null(NeuralIdentify.Identify(text, "renodx-dlss.addon64"));
    }

    [Theory]
    [InlineData("310.8.3", "310.8.0", 1)]
    [InlineData("v8.5.0-rc10", "v8.5.0-rc5", 1)]
    [InlineData("v8.5.0", "v8.5.0-rc10", 1)]
    [InlineData("v8.5.0", "v8.4.9", 1)]
    [InlineData("v8.5.0", "v8.5.0", 0)]
    public void Versions_order_numerically_and_releases_beat_their_candidates(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(NeuralGenerator.CompareVersions(a, b)));

    // ---------- the section ----------

    [Fact]
    public async Task Release_builds_are_extracted_mirrored_and_listed_newest_first()
    {
        var files = new FakeFiles(_dir);
        files.Served[Url("dlssnr-310.8.0", "a.zip")] = TestPe.Zip("nvngx_dlssnr.dll", DlssNr(0));
        files.Served[Url("dlssnr-310.8.Lecram", "b.zip")] = TestPe.Zip("nvngx_dlssnr.dll", DlssNr(3));
        files.Served[Url("renodx-dlss5-8.5.0-rc5", "c.zip")] = TestPe.Zip("renodx-dlss5.addon64", Lecram10("v8.5.0-rc5"));
        files.Served[Url("renodx-dlss5-8.5.0-rc10", "d.zip")] = TestPe.Zip("renodx-dlss5.addon64", Lecram10("v8.5.0-rc10"));
        var json = List(
            Release("dlssnr-310.8.0", "2026-08-27T10:00:00Z", "a.zip"),
            Release("dlssnr-310.8.Lecram", "2026-09-24T15:00:00Z", "b.zip"),
            Release("renodx-dlss5-8.5.0-rc5", "2026-09-26T19:00:00Z", "c.zip"),
            Release("renodx-dlss5-8.5.0-rc10", "2026-09-26T23:00:00Z", "d.zip"),
            Release("dlss-310.1", "2026-01-01T00:00:00Z", "dlss.zip"));
        var mirror = new FakeMirrorSink();

        var output = await Generator(json, files, mirror).BuildAsync(null, [], Now, default);

        var s = output.Section;
        Assert.Empty(output.Errors);
        Assert.Equal("310.8.3", s.Dlssnr.Current!.Version);
        Assert.Equal(["310.8.0"], s.Dlssnr.History.Select(b => b.Version).ToArray());
        Assert.Equal("nvngx_dlssnr.dll", s.Dlssnr.Current.Name);
        Assert.StartsWith("https://mirror.test/mirror-2026-10/", s.Dlssnr.Current.MirrorUrl);
        Assert.Equal("v8.5.0-rc10", s.Lecram.Current!.Version);
        Assert.Equal(["v8.5.0-rc5"], s.Lecram.History.Select(b => b.Version).ToArray());
        Assert.Equal("v10", s.Lecram.Generation);
        Assert.Equal(4, mirror.Uploads.Count);
        Assert.DoesNotContain(files.Requested, u => u.Contains("dlss-310.1"));
    }

    [Fact]
    public async Task A_second_run_downloads_and_uploads_nothing()
    {
        var files = new FakeFiles(_dir);
        files.Served[Url("dlssnr-1", "a.zip")] = TestPe.Zip("nvngx_dlssnr.dll", DlssNr(1));
        var json = List(Release("dlssnr-1", "2026-08-27T10:00:00Z", "a.zip"));
        var first = await Generator(json, files, new FakeMirrorSink()).BuildAsync(null, [], Now, default);
        var again = new FakeFiles(_dir);
        var mirror = new FakeMirrorSink();

        var second = await Generator(json, again, mirror).BuildAsync(first.Section, [], Now.AddDays(1), default);

        Assert.Empty(again.Requested);
        Assert.Empty(mirror.Uploads);
        Assert.Equal(JsonSerializer.Serialize(first.Section, CatalogGen.Json.Options), JsonSerializer.Serialize(second.Section, CatalogGen.Json.Options));
    }

    [Fact]
    public async Task A_zip_that_does_not_hold_the_expected_binary_is_skipped_with_a_warning()
    {
        var files = new FakeFiles(_dir);
        files.Served[Url("dlssnr-1", "a.zip")] = TestPe.Zip("nvngx_dlssnr.dll", TestZip.Pe("not nvidia"));
        files.Served[Url("dlssnr-2", "b.zip")] = TestZip.Make("a.txt", "b.txt");
        var json = List(Release("dlssnr-1", "2026-08-27T10:00:00Z", "a.zip"), Release("dlssnr-2", "2026-08-28T10:00:00Z", "b.zip"));
        var mirror = new FakeMirrorSink();

        var output = await Generator(json, files, mirror).BuildAsync(null, [], Now, default);

        Assert.Null(output.Section.Dlssnr.Current);
        Assert.Empty(mirror.Uploads);
        Assert.Equal(2, output.Warnings.Count);
    }

    [Fact]
    public async Task Dropped_files_are_mirrored_and_the_same_file_twice_is_a_no_op()
    {
        var mirror = new FakeMirrorSink();
        var generator = Generator("[]", new FakeFiles(_dir), mirror);
        var first = MakeDrop("x.dll", DlssNr(5));
        var lecram = MakeDrop("y.bin", Lecram11("v11.0.2"));

        var one = await generator.BuildAsync(null, [first, lecram], Now, default);
        var twin = MakeDrop("copy.dll", DlssNr(5));
        var two = await generator.BuildAsync(one.Section, [twin], Now.AddDays(1), default);

        Assert.True(first.Handled);
        Assert.True(lecram.Handled);
        Assert.True(twin.Handled);
        Assert.Equal(2, mirror.Uploads.Count);
        Assert.Equal("310.8.5", two.Section.Dlssnr.Current!.Version);
        Assert.Empty(two.Section.Dlssnr.History);
        Assert.Null(two.Section.Dlssnr.Current.Url);
        Assert.Equal("v11", two.Section.Lecram.Generation);
        Assert.Equal("dlss5-nr.addon64", two.Section.Lecram.Current!.Name);
    }

    [Fact]
    public async Task A_v11_build_ranks_above_v10_and_the_list_is_capped()
    {
        var generator = Generator("[]", new FakeFiles(_dir), new FakeMirrorSink(), keep: 2);
        var drops = new[] { MakeDrop("a", Lecram10("v9.0.0")), MakeDrop("b", Lecram11("v11.0.1")), MakeDrop("c", Lecram10("v8.0.0")) };

        var output = await generator.BuildAsync(null, drops, Now, default);

        Assert.Equal("v11.0.1", output.Section.Lecram.Current!.Version);
        Assert.Equal(["v9.0.0"], output.Section.Lecram.History.Select(b => b.Version).ToArray());
    }

    [Fact]
    public async Task The_file_version_is_recorded_for_both_kinds()
    {
        var drops = new[] { MakeDrop("a", DlssNr(5)), MakeDrop("b", Lecram10("v310.8.0", [0, 2026, 828, 517])), MakeDrop("c", Lecram11("v11.0.2")) };

        var output = await Generator("[]", new FakeFiles(_dir), new FakeMirrorSink()).BuildAsync(null, drops, Now, default);

        Assert.Equal("310.8.5", output.Section.Dlssnr.Current!.FileVersion);
        Assert.Equal("v11.0.2", output.Section.Lecram.Current!.Version);
        Assert.Null(output.Section.Lecram.Current.FileVersion);
        Assert.Equal("0.2026.828.517", output.Section.Lecram.History.Single().FileVersion);
    }

    [Fact]
    public async Task Builds_with_one_version_label_order_by_file_version_and_the_highest_is_current()
    {
        var drops = new[]
        {
            MakeDrop("a", Lecram10("v310.8.0", [0, 2026, 828, 517])),
            MakeDrop("b", Lecram10("v310.8.0", [0, 2026, 1002, 9])),
            MakeDrop("c", Lecram10("v310.8.0", [0, 2026, 926, 210])),
            MakeDrop("d", Lecram10("v310.8.0", [0, 0, 0, 0])),
        };

        var output = await Generator("[]", new FakeFiles(_dir), new FakeMirrorSink()).BuildAsync(null, drops, Now, default);

        var all = new[] { output.Section.Lecram.Current! }.Concat(output.Section.Lecram.History).Select(b => b.FileVersion).ToArray();
        Assert.Equal(new string?[] { "0.2026.1002.9", "0.2026.926.210", "0.2026.828.517", null }, all);
    }

    [Fact]
    public async Task A_listed_build_without_file_version_gets_it_from_its_mirror_copy()
    {
        var bytes = Lecram10("v310.8.0", [0, 2026, 828, 517]);
        var old = Entry(bytes, "https://mirror.test/old");
        var files = new FakeFiles(_dir);
        files.Served[old.MirrorUrl] = bytes;
        var previous = new NeuralSection { Lecram = new NeuralLecram { Current = old, Generation = "v10" } };

        var output = await Generator("[]", files, new FakeMirrorSink()).BuildAsync(previous, [], Now, default);

        Assert.Equal("0.2026.828.517", output.Section.Lecram.Current!.FileVersion);
        Assert.Empty(output.Warnings);
    }

    [Fact]
    public async Task A_failed_backfill_keeps_the_entry_with_a_warning()
    {
        var bytes = Lecram10("v310.8.0", [0, 2026, 828, 517]);
        var gone = Entry(bytes, "https://mirror.test/gone");
        var changed = Entry(bytes, "https://mirror.test/changed");
        changed.Sha256 = new string('a', 64);
        var files = new FakeFiles(_dir);
        files.Served[changed.MirrorUrl] = bytes;
        var previous = new NeuralSection { Lecram = new NeuralLecram { Current = gone, History = [changed], Generation = "v10" } };

        var output = await Generator("[]", files, new FakeMirrorSink()).BuildAsync(previous, [], Now, default);

        var kept = new[] { output.Section.Lecram.Current! }.Concat(output.Section.Lecram.History).ToList();
        Assert.Equal(2, kept.Count);
        Assert.All(kept, b => Assert.Null(b.FileVersion));
        Assert.Equal(2, output.Warnings.Count);
    }

    [Fact]
    public async Task A_listed_dlssnr_takes_its_file_version_from_its_version_without_a_download()
    {
        var old = new NeuralBuild { Name = "nvngx_dlssnr.dll", Version = "310.8.3", Sha256 = new string('b', 64), Size = 5, FirstSeen = "2026-09-01T00:00:00Z", MirrorUrl = "https://mirror.test/dll" };
        var files = new FakeFiles(_dir);

        var output = await Generator("[]", files, new FakeMirrorSink()).BuildAsync(new NeuralSection { Dlssnr = new NeuralStream { Current = old } }, [], Now, default);

        Assert.Equal("310.8.3", output.Section.Dlssnr.Current!.FileVersion);
        Assert.Empty(files.Requested);
    }

    private static NeuralBuild Entry(byte[] bytes, string mirrorUrl) => new()
    {
        Name = "renodx-dlss5.addon64",
        Version = "v310.8.0",
        Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
        Size = bytes.Length,
        FirstSeen = "2026-09-01T00:00:00Z",
        MirrorUrl = mirrorUrl,
        Generation = "v10",
    };

    [Fact]
    public async Task A_failed_upload_leaves_the_drop_in_place()
    {
        var drop = MakeDrop("x.dll", DlssNr(5));

        var output = await Generator("[]", new FakeFiles(_dir), new FailingMirror()).BuildAsync(null, [drop], Now, default);

        Assert.False(drop.Handled);
        Assert.Single(output.Errors);
        Assert.Null(output.Section.Dlssnr.Current);
    }

    [Fact]
    public async Task The_section_validates_against_the_published_schema()
    {
        var drops = new[] { MakeDrop("x.dll", DlssNr(5)), MakeDrop("y", Lecram10("v8.0.0")) };
        var output = await Generator("[]", new FakeFiles(_dir), new FakeMirrorSink()).BuildAsync(null, drops, Now, default);
        var root = new JsonObject { ["schema"] = 1, ["repository"] = "a/b", ["generated"] = "2026-10-21T12:00:00Z", ["sections"] = new JsonObject() };
        CatalogFile.SetSection(root, "neural", output.Section, Now);

        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "schema", "catalog.schema.json"))) dir = Path.GetDirectoryName(dir)!;
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(dir, "schema", "catalog.schema.json")));
        var result = schema.Evaluate(JsonDocument.Parse(root.ToJsonString()).RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, JsonSerializer.Serialize(result));
    }

    // ---------- incoming ----------

    [Fact]
    public async Task Unknown_files_in_incoming_give_a_warning_and_stay()
    {
        var files = new FakeFiles(_dir);
        files.Served["https://x/known.dll"] = DlssNr(2);
        files.Served["https://x/harmless.exe"] = TestPe.Make(internalName: "HARMLESS", originalName: "harmless.exe");
        files.Served["https://x/notes.txt"] = "hello"u8.ToArray();
        var incoming = new FakeIncoming(new(1, "known.dll", "https://x/known.dll"), new(2, "harmless.exe", "https://x/harmless.exe"),
            new(3, "notes.txt", "https://x/notes.txt"), new(4, "gone.dll", "https://x/gone.dll"));
        var warnings = new List<string>();

        var drops = await IncomingDrops.ReadAsync(incoming, files, warnings, default);

        Assert.Equal(["known.dll"], drops.Select(d => d.Asset.Name).ToArray());
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("harmless.exe") && w.Contains("left in place"));
        foreach (var d in drops) d.Dispose();
    }

    // ---------- renodx-dlss ----------

    [Fact]
    public async Task A_dropped_renodx_dlss_is_mirrored_into_that_section_and_survives_the_next_run()
    {
        var mirror = new FakeMirrorSink();
        var files = new FakeFiles(_dir);
        files.Served[Url("renodx-dlss-SF-1", "renodx-dlss_SF_1.zip")] = TestZip.Make("renodx-dlss.addon64");
        var generator = new RenodxDlssGenerator(
            new Releases(List(Release("renodx-dlss-SF-1", "2026-01-01T00:00:00Z", "renodx-dlss_SF_1.zip"))),
            new FileSync(files, new FakeLicenceSource(new()), mirror, mirrorAnyLicence: true), mirror);
        var drop = MakeDrop("renodx-dlss.addon64", TestZip.Pe("dropped"));

        var first = await generator.BuildAsync(null, Now, default, [drop]);
        var second = await generator.BuildAsync(first.Section, Now.AddDays(1), default, []);

        Assert.True(drop.Handled);
        Assert.Equal(2, second.Section.Builds.Count);
        var rhi = second.Section.Builds.Single(b => !b.Tag.StartsWith("incoming-"));
        Assert.NotNull(rhi.File.MirrorUrl);
        var build = second.Section.Builds.Single(b => b.Tag.StartsWith("incoming-"));
        Assert.Equal("renodx-dlss.addon64", build.File.Name);
        Assert.Equal(build.File.MirrorUrl, build.File.Url);
        Assert.Equal(drop.Sha256, build.File.Sha256);
        Assert.Equal(2, mirror.Uploads.Count);
    }
}
