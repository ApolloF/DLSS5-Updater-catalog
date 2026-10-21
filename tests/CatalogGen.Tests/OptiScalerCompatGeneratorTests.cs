using System.Text;
using System.Text.Json.Nodes;
using CatalogGen;
using Json.Schema;

namespace CatalogGen.Tests;

public class OptiScalerCompatGeneratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed class Pages(string? compat, string? hudfix) : ITextSource
    {
        public Task<string> GetStringAsync(string url, CancellationToken ct) =>
            url == OptiScalerCompatGenerator.CompatUrl && compat is not null ? Task.FromResult(compat)
            : url == OptiScalerCompatGenerator.HudfixUrl && hudfix is not null ? Task.FromResult(hudfix)
            : throw new HttpRequestException("404 " + url);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    private static Task<CompatOutput> Build(string? compat = null, string? hudfix = null, int minMain = 5, int minHudfix = 5) =>
        new OptiScalerCompatGenerator(new Pages(compat ?? Fixture("Compatibility-List.sample.md"), hudfix ?? Fixture("Hudfix-incompatible.sample.md")), minMain, minHudfix)
            .BuildAsync(CancellationToken.None);

    private static List<CompatGame> Parse(string markdown, List<string>? warnings = null) =>
        OptiScalerCompatGenerator.ParseGames(markdown, warnings ?? []);

    private const string Header = "| Game | Compatibility | Upscaler <br>Inputs | OptiPatcher <br>Support | Notes | Images |\n| ---- | :--: | :--: | :--: | ----- | :----: |\n";

    [Fact]
    public async Task The_three_tables_become_three_lists_in_upstream_order()
    {
        var section = (await Build()).Section;

        var lists = section.Games.Select(g => g.List).Distinct().ToList();
        Assert.Equal(["main", "upscalerMods", "lumaUe"], lists);
        Assert.Equal(11, section.Games.Count(g => g.List == "main"));
        Assert.Equal("007 First Light", section.Games[0].Name);
        Assert.Equal(OptiScalerCompatGenerator.CompatUrl, section.Source);
        Assert.Equal(OptiScalerCompatGenerator.HudfixUrl, section.HudfixSource);
    }

    [Fact]
    public async Task The_main_table_has_the_optipatcher_column_the_small_tables_do_not()
    {
        var games = (await Build()).Section.Games;

        Assert.True(games.Single(g => g.Name == "1666: Amsterdam").OptiPatcher);
        Assert.False(games.Single(g => g.Name == "007 First Light").OptiPatcher);
        var devil = games.Single(g => g.Name == "Devil May Cry 5");
        Assert.Equal("upscalerMods", devil.List);
        Assert.False(devil.OptiPatcher);
        Assert.StartsWith("Requires REFramework", devil.Notes);
        Assert.Equal("Disable `Frame Smoothing` in game settings to unlock FPS", games.Single(g => g.Name == "Bloodstained: Ritual of the Night").Notes);
    }

    [Fact]
    public async Task Links_with_nested_parentheses_keep_the_whole_slug_and_plain_names_have_no_page()
    {
        var games = (await Build()).Section.Games;

        Assert.Equal("Elden-Ring-(ERSS‐FG)", games.Single(g => g.Name == "Elden Ring").Page);
        var dead = games.Single(g => g.Name == "Dead Space (2023)");
        Assert.Equal("Dead-Space-(2023)", dead.Page);
        Assert.Equal("171", games.Single(g => g.Name == "171").Page);
        Assert.Null(games.Single(g => g.Name == "63 Days").Page);
    }

    [Fact]
    public async Task Status_marks_and_inputs_are_read_as_written()
    {
        var games = (await Build()).Section.Games;

        Assert.Equal("working", games.Single(g => g.Name == "Dead Space (2023)").Compat);
        Assert.Equal("not-working", games.Single(g => g.Name == "Gears of War: Reloaded").Compat);
        Assert.Equal("single-os", games.Single(g => g.Name == "Minecraft RTX").Compat);
        Assert.Equal(["DLSS", "FSR3.1/4", "XeSS"], games.Single(g => g.Name == "1666: Amsterdam").Inputs);
        Assert.Equal(["DLSS", "FSR2"], games.Single(g => g.Name == "Dead Space (2023)").Inputs);
        Assert.Empty(games.Single(g => g.Name == "Atlas Fallen: Reign Of Sand").Inputs);
    }

    [Fact]
    public async Task Rows_without_an_images_cell_still_keep_their_note()
    {
        var atlas = (await Build()).Section.Games.Single(g => g.Name == "Atlas Fallen: Reign Of Sand");

        Assert.Equal("OptiScaler cannot hook this game's FSR2 inputs in DX12 or Vulkan.", atlas.Notes);
    }

    [Fact]
    public async Task The_template_row_inside_a_comment_is_not_a_game()
    {
        var games = (await Build()).Section.Games;

        Assert.DoesNotContain(games, g => g.Name == "GAME NAME");
        Assert.DoesNotContain(games, g => g.Name == "Game");
        Assert.DoesNotContain(games, g => g.Name.StartsWith("---"));
    }

    [Fact]
    public void Notes_lose_line_breaks_and_links_but_keep_their_text()
    {
        var games = Parse(Header + "| A | ✅ | DLSS | | See [the entry](https://x.test/a_(b)) first<br>then  go. | [1](https://i.test/1) |\n");

        Assert.Equal("See the entry first then go.", games.Single().Notes);
    }

    [Fact]
    public void A_pipe_inside_a_note_stays_in_the_note()
    {
        var games = Parse(Header + "| A | ✅ | DLSS | | use a | b | [1](https://i.test/1) |\n");

        Assert.Equal("use a | b", games.Single().Notes);
    }

    [Fact]
    public void An_empty_note_is_left_out()
    {
        Assert.Null(Parse(Header + "| A | ✅ | DLSS | | | |\n").Single().Notes);
    }

    [Fact]
    public void Unknown_marks_and_short_rows_are_skipped_with_a_warning()
    {
        var warnings = new List<string>();

        var games = Parse(Header + "| Odd | ? | DLSS | | n | |\n| Short | ✅ |\n| Fine | ✅ | DLSS | | | |\n", warnings);

        Assert.Equal(["Fine"], games.Select(g => g.Name));
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void A_repeated_name_in_one_list_keeps_the_first_row()
    {
        var warnings = new List<string>();

        var games = Parse(Header + "| A | ✅ | DLSS | | first | |\n| A | ❌ | | | second | |\n", warnings);

        Assert.Equal("first", games.Single().Notes);
        Assert.Single(warnings);
    }

    [Fact]
    public void The_same_name_in_two_lists_is_kept_in_both()
    {
        var games = Parse(Header + "| A | ✅ | DLSS | | | |\n\n## Luma Unreal Engine\n\n| Game | Compatibility | Upscaler <br>Inputs | Notes | Images |\n| - | - | - | - | - |\n| A | ✅ | DLSS | | |\n");

        Assert.Equal(["main", "lumaUe"], games.Select(g => g.List));
    }

    [Fact]
    public async Task Hudfix_names_come_from_the_bullets_before_the_divider_only()
    {
        var names = (await Build()).Section.HudfixIncompatible;

        Assert.Equal(24, names.Count);
        Assert.Equal("Alan Wake 2", names[0]);
        Assert.Contains("Assassin's Creed: Mirage", names);
        Assert.Equal("Uncharted 4", names[^1]);
        Assert.DoesNotContain(names, n => n.Contains("Frame Generation Options") || n.Contains("dlssg"));
    }

    [Fact]
    public async Task A_compatibility_list_with_too_few_main_games_is_refused()
    {
        var ex = await Assert.ThrowsAsync<SourceException>(() => Build(minMain: 12));

        Assert.Contains("main games", ex.Message);
    }

    [Fact]
    public async Task A_hudfix_page_with_too_few_names_is_refused()
    {
        await Assert.ThrowsAsync<SourceException>(() => Build(hudfix: "* One\n* Two\n---\n* note\n"));
    }

    [Fact]
    public async Task The_default_guards_refuse_the_small_fixture()
    {
        var generator = new OptiScalerCompatGenerator(new Pages(Fixture("Compatibility-List.sample.md"), Fixture("Hudfix-incompatible.sample.md")));

        await Assert.ThrowsAsync<SourceException>(() => generator.BuildAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_unreachable_page_is_a_source_failure()
    {
        await Assert.ThrowsAsync<SourceException>(() =>
            new OptiScalerCompatGenerator(new Pages(Fixture("Compatibility-List.sample.md"), null), 5, 5).BuildAsync(CancellationToken.None));
        await Assert.ThrowsAsync<SourceException>(() =>
            new OptiScalerCompatGenerator(new Pages(null, null), 5, 5).BuildAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_unchanged_second_run_reports_no_change()
    {
        var root = CatalogFile.Load(Path.Combine(Path.GetTempPath(), "cg-missing-" + Guid.NewGuid().ToString("N") + ".json"));
        Assert.True(CatalogFile.SetSection(root, "optiscalerCompat", (await Build()).Section, Now));
        var before = CatalogFile.Serialize(root);

        var changed = CatalogFile.SetSection(root, "optiscalerCompat", (await Build()).Section, Now.AddHours(6));

        Assert.False(changed);
        Assert.Equal(before, CatalogFile.Serialize(root));
    }

    [Fact]
    public async Task The_generated_section_validates_against_the_schema()
    {
        var root = CatalogFile.Load(Path.Combine(Path.GetTempPath(), "cg-missing-" + Guid.NewGuid().ToString("N") + ".json"));
        CatalogFile.SetSection(root, "optiscalerCompat", (await Build()).Section, Now);

        var result = Evaluate(JsonNode.Parse(CatalogFile.Serialize(root))!);

        Assert.True(result.IsValid, string.Join("; ", result.Details.Where(d => d.Errors is not null).SelectMany(d => d.Errors!.Select(e => d.InstanceLocation + " " + e.Value))));
    }

    [Fact]
    public async Task A_game_with_an_unknown_status_fails_the_schema()
    {
        var root = CatalogFile.Load(Path.Combine(Path.GetTempPath(), "cg-missing-" + Guid.NewGuid().ToString("N") + ".json"));
        CatalogFile.SetSection(root, "optiscalerCompat", (await Build()).Section, Now);
        root["sections"]!["optiscalerCompat"]!["games"]![0]!["compat"] = "maybe";

        Assert.False(Evaluate(JsonNode.Parse(CatalogFile.Serialize(root))!).IsValid);
    }

    private static EvaluationResults Evaluate(JsonNode doc)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "schema", "catalog.schema.json");
            if (File.Exists(candidate))
                return JsonSchema.FromText(File.ReadAllText(candidate, Encoding.UTF8)).Evaluate(doc, new EvaluationOptions { OutputFormat = OutputFormat.List });
        }
        throw new FileNotFoundException("schema/catalog.schema.json");
    }
}
