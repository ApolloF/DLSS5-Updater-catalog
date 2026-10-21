namespace CatalogGen;

/// <summary>One add-on package from crosire's list, with the files the app can download for it.</summary>
public sealed class AddonEntry
{
    /// <summary>Stable slug of the package name, e.g. "shadertoggler-by-otis-inf".</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? RepositoryUrl { get; set; }
    /// <summary>Where the package's effect files go, as crosire's list writes it (relative to the game folder).</summary>
    public string? EffectInstallPath { get; set; }
    /// <summary>SPDX id of the repository the files are published from, when GitHub reports one.</summary>
    public string? License { get; set; }
    /// <summary>The app already manages this add-on as a component of its own (e.g. "renodx", "display-commander"); no files are listed.</summary>
    public string? ManagedBy { get; set; }
    /// <summary>Why the app can't install the package by itself (no download, or files it doesn't place); the repository link is the way in.</summary>
    public string? Manual { get; set; }
    public List<FileEntry> Files { get; set; } = [];
}

public sealed class AddonsSection
{
    public string? Updated { get; set; }
    public string Source { get; set; } = AddonsIni.Url;
    public List<AddonEntry> Addons { get; set; } = [];
}
