namespace CatalogGen;

/// <summary>One release zip of the Luma Framework (<c>Luma-&lt;Game&gt;.zip</c>): a file per bitness, the 32-bit one named <c>-x32</c> upstream.</summary>
public sealed class LumaPackage
{
    /// <summary>The asset name without "Luma-", the bitness and ".zip": "Prey", "BioShock_Series", "Unity_Engine", "Unreal_Engine".</summary>
    public string Id { get; set; } = "";
    public List<FileEntry> Files { get; set; } = [];
}

/// <summary>A game with a Luma mod: a wiki row of the completed table, or a release zip the wiki doesn't list (then <see cref="Status"/> is absent).</summary>
public sealed class LumaMod
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Authors { get; set; } = [];
    /// <summary>"working" or "in-progress", as the wiki's legend says.</summary>
    public string? Status { get; set; }
    public string? Notes { get; set; }
    public ModLinks? Links { get; set; }
    /// <summary>Id of the <see cref="LumaPackage"/> that holds the mod; absent when the author publishes it elsewhere (Nexus Mods).</summary>
    public string? Package { get; set; }
    public bool RequiresDgVoodoo { get; set; }
    public List<string> DgVoodooVersions { get; set; } = [];
    public List<string> DgVoodooSetup { get; set; } = [];
    /// <summary>The wiki's "Mod Features" anchor for the game.</summary>
    public string? FeaturesUrl { get; set; }
}

/// <summary>A row of the wiki's generic Unreal Engine table.</summary>
public sealed class LumaUnrealGame
{
    public string Name { get; set; } = "";
    /// <summary>"yes", "blocked" or "in-progress"; absent when the cell is empty.</summary>
    public string? Dlss { get; set; }
    /// <summary>Same values for native HDR.</summary>
    public string? Hdr { get; set; }
    public string? UeVersion { get; set; }
    public string? Notes { get; set; }
    public List<IniSetting> EngineIni { get; set; } = [];
    public List<string> LaunchArgs { get; set; } = [];
}

public sealed class LumaSection
{
    public string? Updated { get; set; }
    public List<SourceInfo> Sources { get; set; } = [];
    /// <summary>The upstream release tag the files were read from ("latest-755").</summary>
    public string Release { get; set; } = "";
    /// <summary>SPDX id of the repository's licence; absent when GitHub can't classify it (NOASSERTION). Either way the files are pinned, never mirrored.</summary>
    public string? License { get; set; }
    public List<LumaPackage> Packages { get; set; } = [];
    public List<LumaMod> Mods { get; set; } = [];
    /// <summary>Id of the package that is the generic Unreal Engine mod.</summary>
    public string UnrealPackage { get; set; } = "Unreal_Engine";
    public List<LumaUnrealGame> UnrealGames { get; set; } = [];
}
