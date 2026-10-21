using CatalogGen;

namespace CatalogGen.Tests;

public class WikiParserTests
{
    private static List<WikiRow> Rows() => WikiParser.Parse(File.ReadAllText(Path.Combine("Fixtures", "Mods.sample.md")));

    private static WikiRow Row(string name) => Rows().First(r => r.Name == name);

    [Fact]
    public void Tables_are_told_apart_by_their_heading()
    {
        var counts = Rows().GroupBy(r => r.Table).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(8, counts[WikiTable.List]);
        Assert.Equal(4, counts[WikiTable.UeExtended]);
        Assert.Equal(2, counts[WikiTable.Unreal]);
        Assert.Equal(1, counts[WikiTable.Unity]);
        Assert.Equal(1, counts[WikiTable.Related]);
        Assert.Equal(1, counts[WikiTable.Deprecated]);
    }

    [Fact]
    public void Tables_before_the_list_and_code_fences_are_ignored()
    {
        Assert.DoesNotContain(Rows(), r => r.Name == "ignored");
        Assert.DoesNotContain(Rows(), r => r.Name.StartsWith('#'));
    }

    [Fact]
    public void Snapshot_nexus_and_discord_links_are_classified()
    {
        var first = Row("007 First Light");
        Assert.Equal("Musa", first.Maintainer);
        Assert.Equal("working", first.Status);
        Assert.Contains(new WikiLink("nexus", "https://www.nexusmods.com/007firstlight/mods/37"), first.Links);
        Assert.Contains(new WikiLink(WikiLink.File, "https://github.com/mqhaji/renodx/releases/download/snapshot/renodx-007firstlight.addon64"), first.Links);

        Assert.Contains(Row("Arknights: Endfield").Links, l => l.Kind == "discord");
        Assert.Contains(Row("Assassin’s Creed II").Links, l => l.Kind == "gamebanana");
    }

    [Fact]
    public void Both_bitness_files_of_one_row_are_listed()
    {
        var files = Row("Double Variant").Links.Where(l => l.IsFile).Select(l => l.Url).ToList();

        Assert.Equal(["https://example.github.io/renodx/renodx-double.addon64", "https://example.github.io/renodx/renodx-double.addon32"], files);
    }

    [Fact]
    public void Status_marks_and_hover_notes_are_read()
    {
        Assert.Equal("in-progress", Row("Alien: Isolation").Status);
        Assert.Equal("in-progress", Row("Arknights: Endfield").Status);
        var alice = Row("Alice: Madness Returns");
        Assert.Equal("working", alice.Status);
        Assert.Equal("Check the mod's thread Notes.", alice.StatusNote);
    }

    [Fact]
    public void A_linked_name_keeps_its_text_and_url()
    {
        var row = Row("Arknights: Endfield");

        Assert.Equal("https://github.com/clshortfuse/renodx/discussions/490", row.NameUrl);
    }

    [Fact]
    public void Broken_encoding_marks_and_trademark_signs_are_cleaned_from_names()
    {
        Assert.Contains(Rows(), r => r.Name == "Assassin’s Creed II");
        Assert.Contains(Rows(), r => r.Name == "ABZÛ");
    }

    [Fact]
    public void Missing_trailing_cells_and_empty_links_do_not_break_a_row()
    {
        var row = Row("Garry's Mod");

        Assert.Empty(row.Links);
        Assert.Equal("working", row.Status);
        Assert.Equal("Adorable Adventures", Rows().First(r => r.Table == WikiTable.Unreal && r.Name.StartsWith("Adorable")).Name);
    }

    [Fact]
    public void Notes_lose_html_and_backticks_but_keep_their_line_breaks()
    {
        var notes = Row("ABZÛ").Notes;

        Assert.Equal("Upgrade Path: On\nB8G8R8A8_TYPELESS Output Size for 100% render resolution.", notes);
    }

    [Fact]
    public void Collapsible_details_inside_notes_are_dropped()
    {
        Assert.Equal("Upgrade Path: On", Row("What Remains of Edith Finch").Notes);
    }

    [Fact]
    public void The_same_game_in_two_engine_tables_stays_two_rows()
    {
        var abiotic = Rows().Where(r => r.Name == "Abiotic Factor").ToList();

        Assert.Equal([WikiTable.UeExtended, WikiTable.Unreal], abiotic.Select(r => r.Table).ToArray());
        Assert.Equal("Engine.ini", abiotic[0].Notes);
        Assert.Equal("in-progress", abiotic[1].Status);
    }

    [Fact]
    public void Related_and_deprecated_tables_are_read_with_their_links()
    {
        var luma = Rows().Single(r => r.Table == WikiTable.Related);
        Assert.Equal("Luma Framework", luma.Name);
        Assert.Contains(luma.Links, l => l.Kind == "other" && l.Url.Contains("Luma-Framework"));

        var old = Rows().Single(r => r.Table == WikiTable.Deprecated);
        Assert.Equal("Ace Combat 7", old.Name);
        Assert.True(old.Links.Single().IsFile);
    }
}
