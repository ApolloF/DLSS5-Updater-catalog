using CatalogGen;

namespace CatalogGen.Tests;

public class LicenceTests
{
    [Theory]
    [InlineData("MIT")]
    [InlineData("BSD-2-Clause")]
    [InlineData("BSD-3-Clause")]
    [InlineData("Apache-2.0")]
    [InlineData("ISC")]
    [InlineData("Unlicense")]
    [InlineData("mit")]
    public void Permissive_licences_allow_a_mirror(string spdx) => Assert.True(Licence.AllowsMirror(spdx));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NOASSERTION")]
    [InlineData("OTHER")]
    [InlineData("GPL-3.0")]
    [InlineData("AGPL-3.0")]
    [InlineData("CC-BY-NC-4.0")]
    [InlineData("LicenseRef-proprietary")]
    public void Anything_else_stays_pinned(string? spdx) => Assert.False(Licence.AllowsMirror(spdx));

    [Theory]
    [InlineData("https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-x.addon64", "clshortfuse/renodx")]
    [InlineData("https://github.com/TheEdmaster87/Steel-Seed-RenoDX-HDR/releases/download/v1.0.4/renodx-steelseed.addon64", "TheEdmaster87/Steel-Seed-RenoDX-HDR")]
    [InlineData("https://marat569.github.io/renodx/renodx-ue-extended.addon64", "marat569/renodx")]
    [InlineData("https://NotVoosh.github.io/renodx-unity/renodx-unityengine.addon32", "notvoosh/renodx-unity")]
    [InlineData("https://example.com/renodx/file.addon64", null)]
    [InlineData("https://github.io/renodx/file.addon64", null)]
    [InlineData("https://marat569.github.io/file.addon64", null)]
    [InlineData("not a url", null)]
    public void The_source_repository_comes_from_the_url(string url, string? repo) => Assert.Equal(repo, Licence.RepoOf(url));
}
