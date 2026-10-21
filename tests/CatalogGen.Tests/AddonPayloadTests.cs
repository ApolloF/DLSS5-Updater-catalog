using CatalogGen;

namespace CatalogGen.Tests;

public class AddonPayloadTests
{
    [Fact]
    public void Addon_binaries_take_their_bitness_from_the_extension()
    {
        var r = AddonPayload.Analyse(["Pack/Foo.addon32", "Pack/Foo.addon64", "README.md", "LICENSE"], null, "both");

        Assert.Equal([("Pack/Foo.addon32", "addon", "x86"), ("Pack/Foo.addon64", "addon", "x64")], r.Items.Select(i => (i.Entry, i.Kind, i.Arch)).ToArray());
        Assert.Empty(r.Unplaced);
    }

    [Theory]
    [InlineData("x64", "x64")]
    [InlineData("x86", "x86")]
    [InlineData("both", "both")]
    public void A_bare_addon_file_takes_the_bitness_the_list_declares(string declared, string expected)
    {
        Assert.Equal(expected, AddonPayload.Analyse(["tool.addon"], null, declared).Items.Single().Arch);
        Assert.Equal(expected, AddonPayload.ForBinary("tool.addon", declared)!.Arch);
    }

    [Fact]
    public void A_plain_download_that_is_not_an_addon_has_no_payload()
    {
        Assert.Null(AddonPayload.ForBinary("tool.dll", "x64"));
    }

    [Fact]
    public void Effects_go_flat_into_the_install_path_below_the_shaders_folder()
    {
        var r = AddonPayload.Analyse(["Xinp_Release/Xinp.addon64", "Xinp_Release/Xinp.fx", "Xinp_Release/2DSDFunctions.fxh", "Xinp_Release/README.md"],
            @".\reshade-shaders\Shaders\Xinp", "both");

        Assert.Equal(["Xinp/Xinp.fx", "Xinp/2DSDFunctions.fxh"], r.Items.Where(i => i.Kind == "effect").Select(i => i.Dest!).ToArray());
        Assert.Empty(r.Unplaced);
    }

    [Fact]
    public void An_install_path_that_is_the_shaders_folder_itself_gives_no_subfolder()
    {
        var r = AddonPayload.Analyse(["a.fx"], @".\reshade-shaders\Shaders", "both");

        Assert.Equal("a.fx", r.Items.Single().Dest);
    }

    [Fact]
    public void A_Shaders_folder_in_the_zip_decides_the_destination_without_an_install_path()
    {
        var r = AddonPayload.Analyse(["Shaders/IgcsDof.fx", "reshade-shaders/Shaders/VREM/VREM.fxh"], null, "x64");

        Assert.Equal(["IgcsDof.fx", "VREM/VREM.fxh"], r.Items.Select(i => i.Dest!).ToArray());
    }

    [Fact]
    public void An_effect_with_nowhere_to_go_is_unplaced()
    {
        var r = AddonPayload.Analyse(["loose.fx"], null, "x64");

        Assert.Empty(r.Items);
        Assert.Equal(["loose.fx"], r.Unplaced);
    }

    [Fact]
    public void An_install_path_outside_the_shaders_folder_places_nothing()
    {
        var r = AddonPayload.Analyse(["loose.fx"], @".\somewhere\else", "x64");

        Assert.Equal(["loose.fx"], r.Unplaced);
    }

    [Fact]
    public void Files_the_app_does_not_place_are_reported_not_ignored()
    {
        var r = AddonPayload.Analyse(["x.addon64", "VREM_shaderreplace/a.cso", "DO_NOT_ENABLE.addonfx", "launcher.exe", "docs/notes.txt"], null, "x64");

        Assert.Equal(["VREM_shaderreplace/a.cso", "DO_NOT_ENABLE.addonfx", "launcher.exe"], r.Unplaced.ToArray());
    }

    [Theory]
    [InlineData("../evil.addon64")]
    [InlineData("a/../../evil.addon64")]
    [InlineData("C:/evil.addon64")]
    public void Entries_that_climb_out_or_name_a_drive_are_never_payload(string entry)
    {
        var r = AddonPayload.Analyse([entry], null, "x64");

        Assert.Empty(r.Items);
        Assert.Single(r.Unplaced);
    }

    [Fact]
    public void Backslashes_and_directory_entries_are_handled()
    {
        var r = AddonPayload.Analyse([@"Pack\Foo.addon64", "Pack/"], null, "x64");

        Assert.Equal("Pack/Foo.addon64", r.Items.Single().Entry);
        Assert.Empty(r.Unplaced);
    }
}
