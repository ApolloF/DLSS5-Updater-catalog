using System.Text.Json.Serialization;

namespace CatalogGen;

/// <summary>Where the files of a shader pack sit inside its zip. The app extracts everything below these prefixes.</summary>
public sealed class PackLayout
{
    /// <summary>Zip prefix of the Shaders folder, ending in '/', e.g. "ReShade_HDR_shaders-ab12cd3/Shaders/".</summary>
    public string ShadersDir { get; set; } = "";
    /// <summary>Zip prefix of the Textures folder; absent when the pack has none.</summary>
    public string? TexturesDir { get; set; }
    public int ShaderFiles { get; set; }
    public int TextureFiles { get; set; }
}

public sealed class ShaderPack
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>owner/repo on GitHub.</summary>
    public string Repo { get; set; } = "";
    public string Homepage { get; set; } = "";
    /// <summary>"hdr", "general", "color", "depth-3d" or "cinematic".</summary>
    public string Category { get; set; } = "";
    /// <summary>Pre-selected for new setups (the HDR essentials).</summary>
    [JsonPropertyName("default")]
    public bool Default { get; set; }
    /// <summary>"branch" or "release-asset".</summary>
    public string Source { get; set; } = "";
    /// <summary>The branch name or the release tag the build was taken from.</summary>
    public string Ref { get; set; } = "";
    /// <summary>Commit the zip was made from (branch packs).</summary>
    public string? Commit { get; set; }
    /// <summary>What the user sees: the release tag, or the first seven characters of the commit.</summary>
    public string Version { get; set; } = "";
    /// <summary>SPDX id (GitHub's, or the one verified by hand in shader-packs.json when GitHub reports none); absent = no licence found.</summary>
    public string? License { get; set; }
    /// <summary>The zip, with its hash, size, first-seen version and history. Its <c>layout</c> says where the folders are.</summary>
    public FileEntry? File { get; set; }
}

public sealed class ShaderPacksSection
{
    public string? Updated { get; set; }
    public string Source { get; set; } = "shader-packs.json";
    public List<ShaderPack> Packs { get; set; } = [];
}

/// <summary>shader-packs.json: the list of upstream shader repositories we maintain by hand.</summary>
public sealed class ShaderPackList
{
    public int Schema { get; set; } = 1;
    public List<PackSource> Packs { get; set; } = [];
}

public sealed class PackSource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Repo { get; set; } = "";
    public PackSourceKind Source { get; set; } = new();
    public string Category { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>SPDX id we checked in the repository's licence file, used only when GitHub reports none (NOASSERTION).</summary>
    public string? License { get; set; }
    public bool Default { get; set; }
}

public sealed class PackSourceKind
{
    /// <summary>"branch" or "release-asset".</summary>
    public string Kind { get; set; } = "";
    public string? Branch { get; set; }
    /// <summary>release-asset: file-name pattern with * and ?, default "*.zip".</summary>
    public string? Asset { get; set; }
}
