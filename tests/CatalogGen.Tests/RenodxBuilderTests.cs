using CatalogGen;

namespace CatalogGen.Tests;

public class RenodxBuilderTests
{
    private static readonly Uri IndexBase = new(RenodxSources.GamesIndexUrl);

    private static RenodxBuilder.Result Build(OverrideBlock? overrides = null)
    {
        var index = GamesIndex.Parse(File.ReadAllText(Path.Combine("Fixtures", "games-index.sample.json")), IndexBase);
        var wiki = WikiParser.Parse(File.ReadAllText(Path.Combine("Fixtures", "Mods.sample.md")));
        return RenodxBuilder.Build(index, wiki, overrides ?? new OverrideBlock());
    }

    private static ModEntry Mod(RenodxBuilder.Result r, string id) => r.Section.Mods.Single(m => m.Id == id);

    [Fact]
    public void Index_urls_resolve_against_the_snapshot_release_and_carry_the_steam_appid()
    {
        var m = Mod(Build(), "1000xresist");

        Assert.Equal([1675830], m.SteamAppIds);
        var file = Assert.Single(m.Files);
        Assert.Equal("https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-1000xresist.addon64", file.Url);
        Assert.Equal("x64", file.Arch);
        Assert.Equal("1000xresist", file.Mod);
    }

    [Fact]
    public void Wiki_row_is_merged_into_the_index_entry_by_file_name_and_keeps_the_index_file()
    {
        var m = Mod(Build(), "1000xresist");

        Assert.Equal("Ritsu", m.Authors.Single());
        Assert.Equal("working", m.Compat);
        Assert.Equal("stable", m.Status);
        // the wiki points at the author's own pages; the index url is the one we keep
        Assert.DoesNotContain(m.Files, f => f.Url.Contains("mohannedelfatih"));
    }

    [Fact]
    public void Wiki_row_is_merged_by_normalised_name_when_no_file_matches()
    {
        var m = Mod(Build(), "alien-isolation");

        Assert.Equal("in-progress", m.Compat);
        Assert.Equal("https://www.nexusmods.com/alienisolation/mods/78", m.Links!.Nexus);
        Assert.Contains("Playable, but some unresolved graphical bugs.", m.Notes);
        Assert.Equal("x86", Assert.Single(m.Files).Arch);
    }

    [Fact]
    public void Games_the_index_lacks_come_from_the_wiki_with_files_for_both_bitnesses()
    {
        var r = Build();

        var light = r.Section.Mods.Single(m => m.Name == "007 First Light");
        Assert.Equal("007-first-light", light.Id);
        Assert.Empty(light.SteamAppIds);
        Assert.Equal("renodx-007firstlight.addon64", Assert.Single(light.Files).Name);

        var both = r.Section.Mods.Single(m => m.Name == "Double Variant");
        Assert.Equal([("renodx-double.addon32", "x86"), ("renodx-double.addon64", "x64")], both.Files.Select(f => (f.Name, f.Arch)).ToArray());
    }

    [Fact]
    public void A_game_on_the_generic_engine_mod_has_no_file_of_its_own()
    {
        var m = Mod(Build(), "deaths-door");

        Assert.Equal("unity", m.Generic);
        Assert.Empty(m.Files);
        Assert.Equal([894020], m.SteamAppIds);
    }

    [Fact]
    public void Duplicate_artifacts_of_one_game_are_listed_once()
    {
        Assert.Equal(2, Mod(Build(), "two-variants").Files.Count);
    }

    [Fact]
    public void A_wiki_row_that_matches_nothing_gets_a_unique_slug_id()
    {
        var r = Build();

        Assert.Equal(r.Section.Mods.Count, r.Section.Mods.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(r.Section.Mods, m => m.Id == "alice-madness-returns" && m.Files.Count == 1 && m.Files[0].Arch == "x86");
    }

    [Fact]
    public void Deprecated_wiki_rows_are_listed_but_not_downloaded_and_related_ones_are_links_only()
    {
        var r = Build();

        var old = r.Section.Mods.Single(m => m.Name == "Ace Combat 7");
        Assert.Equal("deprecated", old.Listing);
        Assert.Empty(old.Files);

        var luma = r.Section.Mods.Single(m => m.Name == "Luma Framework");
        Assert.Equal("related", luma.Listing);
        Assert.Empty(luma.Files);
    }

    [Fact]
    public void Engine_game_tables_become_a_flat_list_per_engine()
    {
        var games = Build().Section.EngineGames;

        var abiotic = games.Where(g => g.Name == "Abiotic Factor").ToList();
        Assert.Equal(["ue-extended", "unreal"], abiotic.Select(g => g.Engine).ToArray());
        Assert.Equal("Engine.ini", abiotic[0].Notes);
        Assert.Contains(games, g => g.Engine == "unity" && g.Name == "60 Parsecs!");
    }

    [Fact]
    public void Generic_add_ons_cover_unreal_unity_and_ue_extended()
    {
        var generic = Build().Section.Generic.ToDictionary(g => g.Id);

        Assert.Equal("https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-unrealengine.addon64", generic["unreal"].Files.Single().Url);
        Assert.Equal(["x64", "x86"], generic["unity"].Files.Select(f => f.Arch).ToArray());
        Assert.Equal("https://marat569.github.io/renodx/renodx-ue-extended.addon64", generic["ue-extended"].Files.Single().Url);
    }

    [Fact]
    public void Overrides_fix_names_app_ids_and_block_entries()
    {
        var overrides = new OverrideBlock
        {
            NameFixes = [new NameFix { Id = "alien-isolation", Name = "Alien Isolation (Steam)", Aliases = ["Alien: Isolation Collection"] }],
            AppIdFixes = [new AppIdFix { Id = "1000xresist", SteamAppIds = [1, 2, 2] }],
            Blocklist = [new BlockItem { Name = "two variants", Reason = "crashes" }, new BlockItem { Id = "nope" }],
            UeExtendedOptOut = [new UeExtendedOptOut { SteamAppId = 7, Reason = "no HDR" }],
        };

        var r = Build(overrides);

        var alien = Mod(r, "alien-isolation");
        Assert.Equal("Alien Isolation (Steam)", alien.Name);
        Assert.Contains("Alien: Isolation", alien.Aliases);
        Assert.Contains("Alien: Isolation Collection", alien.Aliases);
        Assert.Equal([1, 2], Mod(r, "1000xresist").SteamAppIds);

        var blocked = Mod(r, "two-variants");
        Assert.True(blocked.Blocked);
        Assert.Equal("crashes", blocked.BlockReason);
        Assert.Empty(blocked.Files);

        Assert.Single(r.Warnings);
        Assert.Contains("nope", r.Warnings[0]);
        Assert.Equal(7, r.Section.Overrides.UeExtendedOptOut.Single().SteamAppId);
    }

    [Fact]
    public void Output_is_sorted_so_an_unchanged_upstream_gives_identical_json()
    {
        var a = System.Text.Json.JsonSerializer.Serialize(Build().Section, Json.Options);
        var b = System.Text.Json.JsonSerializer.Serialize(Build().Section, Json.Options);

        Assert.Equal(a, b);
        var names = Build().Section.Mods.Select(m => m.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), names);
    }
}
