using CatalogGen;

namespace CatalogGen.Tests;

public class UeExtendedNotesTests
{
    [Theory]
    [InlineData("Upgrade Path: On, B8G8R8A8_TYPELESS Output Size.", "upgrade")]
    [InlineData("upgrade path: on", "upgrade")]
    [InlineData("Native HDR", "native")]
    [InlineData("Native HDR, need to load in-game for HDR to toggle.", "native")]
    [InlineData("Upgrade Path: Off. Broken rendering when enabled.", "native")]
    [InlineData("Engine.ini", "ini")]
    [InlineData("Engine.ini, Only add r.HDR.EnableHDROutput=1 to Engine.ini.", "ini")]
    [InlineData("Upgrade Path: On, R10G10B10A2_UNORM Output Size. Engine.ini HDR can't be used due to issues with saving game progress.", "upgrade")]
    [InlineData("Upgrade Path: On, R10G10B10A2_UNORM Output Size. Native HDR is broken.", "upgrade")]
    [InlineData("Lacks testing.", null)]
    [InlineData(null, null)]
    public void The_method_follows_the_priority_upgrade_native_ini(string? notes, string? method) =>
        Assert.Equal(method, UeExtendedNotes.Parse(notes).Method);

    [Fact]
    public void Upgrade_on_with_a_format_and_output_size()
    {
        var info = UeExtendedNotes.Parse("Upgrade Path: On, B8G8R8A8_TYPELESS Output Size.");

        Assert.Equal([new UpgradeFormat("B8G8R8A8_TYPELESS", "output-size")], info.Upgrades);
        Assert.Null(info.CopyDestinations);
        Assert.Empty(info.EngineIni);
        Assert.Empty(info.Warnings);
    }

    [Fact]
    public void Only_the_mode_directly_after_the_format_counts()
    {
        var info = UeExtendedNotes.Parse("Upgrade Path: On\nB8G8R8A8_TYPELESS Output Size for 100% render resolution or Output Ratio for other percentages.");

        Assert.Equal("upgrade", info.Method);
        Assert.Equal([new UpgradeFormat("B8G8R8A8_TYPELESS", "output-size")], info.Upgrades);
    }

    [Fact]
    public void Output_ratio_and_any_size_are_modes_too()
    {
        var info = UeExtendedNotes.Parse("Upgrade Path: On. R11G11B10_FLOAT Output Ratio, then R8G8B8A8_UNORM Any Size, R8G8B8A8_UNORM Any Size.");

        Assert.Equal([new UpgradeFormat("R11G11B10_FLOAT", "output-ratio"), new UpgradeFormat("R8G8B8A8_UNORM", "any-size")], info.Upgrades);
    }

    [Fact]
    public void Copy_destinations_off_with_any_size()
    {
        var info = UeExtendedNotes.Parse("Upgrade Path: On as Engine.ini edits overexposed the shading. DX11; refused to boot up DX12 as Low Level Fatal Error Line: 266\n" +
            "Camped to UI brightness has issues with Commu's blue lighting.\nUpgrade Copy Destinations Off\nB8G8R8A8_TYPELESS Any Size");

        Assert.Equal("upgrade", info.Method);
        Assert.False(info.CopyDestinations);
        Assert.Equal([new UpgradeFormat("B8G8R8A8_TYPELESS", "any-size")], info.Upgrades);
        Assert.Empty(info.EngineIni);
    }

    [Fact]
    public void Copy_destinations_on_is_true()
    {
        Assert.True(UeExtendedNotes.Parse("Upgrade Path: On\nUpgrade Copy Destinations On\nR8G8B8A8_UNORM Any Size").CopyDestinations);
    }

    [Fact]
    public void A_format_that_is_not_one_of_the_twelve_is_dropped_and_reported()
    {
        var info = UeExtendedNotes.Parse("Upgrade Path: On as Engine.ini edits overexposed the shading. Gameplay and highlights are clamped to UI brightness.\n" +
            "Upgrade Copy Destinations Off\nR8G8R8A8_TYPELESS Any Size");

        Assert.Equal("upgrade", info.Method);
        Assert.False(info.CopyDestinations);
        Assert.Empty(info.Upgrades);
        Assert.Contains("R8G8R8A8_TYPELESS", Assert.Single(info.Warnings));
    }

    [Fact]
    public void Engine_ini_tokens_become_settings_in_system_settings()
    {
        var info = UeExtendedNotes.Parse("Engine.ini, Only add r.HDR.EnableHDROutput=1 to Engine.ini.");

        Assert.Equal("ini", info.Method);
        Assert.Equal([new IniSetting("SystemSettings", "r.HDR.EnableHDROutput", "1")], info.EngineIni);
    }

    [Fact]
    public void Several_settings_are_listed_once_each_and_a_trailing_dot_is_not_part_of_the_value()
    {
        var info = UeExtendedNotes.Parse("Engine.ini: r.HDR.EnableHDROutput=1, r.HDR.Display.OutputDevice=5 and r.HDR.EnableHDROutput=1.");

        Assert.Equal(
            [new IniSetting("SystemSettings", "r.HDR.EnableHDROutput", "1"), new IniSetting("SystemSettings", "r.HDR.Display.OutputDevice", "5")],
            info.EngineIni);
    }

    [Fact]
    public void Words_that_only_end_in_r_are_not_settings()
    {
        Assert.Empty(UeExtendedNotes.Parse("Tweak for.me=1 and other.thing=2").EngineIni);
    }

    [Fact]
    public void Notes_without_any_of_it_give_an_empty_result()
    {
        var info = UeExtendedNotes.Parse("Lacks testing.");

        Assert.Null(info.Method);
        Assert.Empty(info.Upgrades);
        Assert.Null(info.CopyDestinations);
        Assert.Empty(info.EngineIni);
        Assert.Empty(info.Warnings);
    }

    [Fact]
    public void All_twelve_formats_are_known()
    {
        Assert.Equal(12, UeExtendedNotes.Formats.Count);
        foreach (var f in UeExtendedNotes.Formats)
            Assert.Equal([new UpgradeFormat(f, "output-size")], UeExtendedNotes.Parse($"Upgrade Path: On, {f} Output Size.").Upgrades);
    }
}
