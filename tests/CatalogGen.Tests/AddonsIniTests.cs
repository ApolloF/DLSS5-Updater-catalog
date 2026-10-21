using CatalogGen;

namespace CatalogGen.Tests;

public class AddonsIniTests
{
    private static List<AddonSource> Sources() => AddonsIni.Parse(File.ReadAllText(Path.Combine("Fixtures", "Addons.sample.ini")));

    [Fact]
    public void Numbered_sections_are_read_and_commented_out_ones_are_skipped()
    {
        var s = Sources();

        Assert.Equal(["00", "02", "03", "04", "05", "06", "07", "08", "09", "10"], s.Select(x => x.Number).ToArray());
        Assert.DoesNotContain(s, x => x.PackageName.StartsWith("Framerate Limiter"));
    }

    [Fact]
    public void All_keys_of_a_section_are_read()
    {
        var rest = Sources().Single(x => x.Number == "03");

        Assert.Equal("ReshadeEffectShaderToggler (REST) by 4lex4nder", rest.PackageName);
        Assert.Equal("Apply ReShade effects to render targets of shader groups based on a key press", rest.Description);
        Assert.Equal(@".\reshade-shaders\Shaders\REST", rest.EffectInstallPath);
        Assert.Equal("https://github.com/4lex4nder/ReshadeEffectShaderToggler/releases/latest/download/release.zip", rest.Url);
        Assert.Null(rest.Url32);
        Assert.Null(rest.Url64);
        Assert.Equal("https://github.com/4lex4nder/ReshadeEffectShaderToggler", rest.Repository);
    }

    [Fact]
    public void Separate_32_and_64_bit_addresses_are_kept_apart()
    {
        var s = Sources().Single(x => x.Number == "00");

        Assert.EndsWith("swapchain_override.addon32", s.Url32);
        Assert.EndsWith("swapchain_override.addon64", s.Url64);
        Assert.Null(s.Url);
    }

    [Fact]
    public void A_package_without_a_download_is_still_listed()
    {
        var py = Sources().Single(x => x.Number == "04");

        Assert.Null(py.Url);
        Assert.Null(py.Url32);
        Assert.Null(py.Url64);
        Assert.Equal("https://github.com/dwojtasik/PyHook", py.Repository);
    }

    [Fact]
    public void Values_are_trimmed_and_may_contain_equals_signs()
    {
        var s = AddonsIni.Parse("[00]\r\nPackageName = A by B \r\nPackageDescription= x=y \r\n");

        Assert.Equal("A by B", s.Single().PackageName);
        Assert.Equal("x=y", s.Single().Description);
    }

    [Fact]
    public void Text_without_sections_gives_nothing()
    {
        Assert.Empty(AddonsIni.Parse("PackageName=Orphan\n<html>rate limited</html>"));
    }
}
