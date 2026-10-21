using System.Text.Json.Nodes;
using CatalogGen;

namespace CatalogGen.Tests;

public class CatalogFileTests : IDisposable
{
    private static readonly DateTimeOffset T1 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cg-" + Guid.NewGuid().ToString("N"));

    public CatalogFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static RenodxSection Section(string modName = "Game") => new()
    {
        Mods = [new ModEntry { Id = "g", Name = modName }],
    };

    [Fact]
    public void A_missing_file_gives_an_empty_catalog_with_the_schema_version()
    {
        var root = CatalogFile.Load(Path.Combine(_dir, "catalog.json"));

        Assert.Equal(1, (int)root["schema"]!);
        Assert.Equal("ApolloF/DLSS5-Updater-catalog", (string)root["repository"]!);
        Assert.Empty(root["sections"]!.AsObject());
    }

    [Fact]
    public void A_corrupt_file_is_treated_as_empty()
    {
        var path = Path.Combine(_dir, "catalog.json");
        File.WriteAllText(path, "{ not json");

        Assert.Empty(CatalogFile.Load(path)["sections"]!.AsObject());
    }

    [Fact]
    public void Setting_a_new_section_stamps_it_and_the_catalog()
    {
        var root = CatalogFile.Load(Path.Combine(_dir, "catalog.json"));

        Assert.True(CatalogFile.SetSection(root, "renodx", Section(), T1));

        Assert.Equal("2026-10-21T12:00:00Z", (string)root["generated"]!);
        Assert.Equal("2026-10-21T12:00:00Z", (string)root["sections"]!["renodx"]!["updated"]!);
    }

    [Fact]
    public void The_same_content_later_changes_nothing_not_even_the_timestamps()
    {
        var path = Path.Combine(_dir, "catalog.json");
        var root = CatalogFile.Load(path);
        CatalogFile.SetSection(root, "renodx", Section(), T1);
        Assert.True(CatalogFile.Save(path, root));

        var again = CatalogFile.Load(path);
        var changed = CatalogFile.SetSection(again, "renodx", Section(), T1.AddHours(6));

        Assert.False(changed);
        Assert.False(CatalogFile.Save(path, again));
        Assert.Equal("2026-10-21T12:00:00Z", (string)again["generated"]!);
        Assert.Equal("2026-10-21T12:00:00Z", (string)again["sections"]!["renodx"]!["updated"]!);
    }

    [Fact]
    public void Changed_content_refreshes_the_timestamps()
    {
        var path = Path.Combine(_dir, "catalog.json");
        var root = CatalogFile.Load(path);
        CatalogFile.SetSection(root, "renodx", Section(), T1);
        CatalogFile.Save(path, root);

        var again = CatalogFile.Load(path);
        var changed = CatalogFile.SetSection(again, "renodx", Section("Renamed"), T1.AddHours(6));

        Assert.True(changed);
        Assert.Equal("2026-10-21T18:00:00Z", (string)again["sections"]!["renodx"]!["updated"]!);
        Assert.Equal("2026-10-21T18:00:00Z", (string)again["generated"]!);
    }

    [Fact]
    public void Sections_written_by_other_generators_are_kept_untouched()
    {
        var path = Path.Combine(_dir, "catalog.json");
        File.WriteAllText(path, """{"schema":1,"repository":"ApolloF/DLSS5-Updater-catalog","generated":"2026-10-01T00:00:00Z","sections":{"luma":{"updated":"2026-10-01T00:00:00Z","mods":[{"id":"l"}]}}}""");
        var root = CatalogFile.Load(path);

        CatalogFile.SetSection(root, "renodx", Section(), T1);

        var luma = root["sections"]!["luma"]!;
        Assert.Equal("2026-10-01T00:00:00Z", (string)luma["updated"]!);
        Assert.Equal("l", (string)luma["mods"]![0]!["id"]!);
    }

    [Fact]
    public void The_previous_section_reads_back_as_the_typed_model()
    {
        var path = Path.Combine(_dir, "catalog.json");
        var root = CatalogFile.Load(path);
        var section = Section();
        section.Mods[0].Files.Add(new FileEntry { Name = "a.addon64", Url = "https://x.test/a.addon64", Sha256 = new string('a', 64), ETag = "\"v1\"" });
        CatalogFile.SetSection(root, "renodx", section, T1);
        CatalogFile.Save(path, root);

        var back = CatalogFile.ReadSection<RenodxSection>(CatalogFile.Load(path), "renodx")!;

        Assert.Equal("\"v1\"", back.Mods[0].Files[0].ETag);
        Assert.Contains("\"etag\"", File.ReadAllText(path));
    }

    [Fact]
    public void Saved_text_uses_unix_line_endings_and_ends_with_a_newline()
    {
        var root = CatalogFile.Load(Path.Combine(_dir, "catalog.json"));

        var text = CatalogFile.Serialize(root);

        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("}\n", text);
    }

    [Fact]
    public void Overrides_file_is_optional_and_parsed_with_comments_allowed()
    {
        Assert.Empty(Program.LoadOverrides(Path.Combine(_dir, "missing.json")).NameFixes);

        var path = Path.Combine(_dir, "overrides.json");
        File.WriteAllText(path, """
            {
              // fixes
              "schema": 1,
              "renodx": { "nameFixes": [ { "id": "g", "name": "Game" } ], "engineIniPaths": [ { "steamAppId": 5, "path": "Game\\Saved\\Config\\Engine.ini" } ] }
            }
            """);

        var o = Program.LoadOverrides(path);

        Assert.Equal("Game", o.NameFixes.Single().Name);
        Assert.Equal(5, o.EngineIniPaths.Single().SteamAppId);
    }
}
