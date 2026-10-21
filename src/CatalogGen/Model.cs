using System.Text.Json;
using System.Text.Json.Serialization;

namespace CatalogGen;

/// <summary>One downloadable binary: the current build at the top level, older builds in <see cref="History"/>.</summary>
public sealed class FileEntry
{
    public string Name { get; set; } = "";
    /// <summary>"x64" or "x86".</summary>
    public string Arch { get; set; } = "x64";
    /// <summary>Where the author publishes it.</summary>
    public string Url { get; set; } = "";
    public string? Sha256 { get; set; }
    public long Size { get; set; }
    /// <summary>First-seen UTC date plus short hash of this build, e.g. 2026-10-21-ab12cd3 (upstream snapshots have no version).</summary>
    public string? Version { get; set; }
    public string? FirstSeen { get; set; }
    /// <summary>Date of the last full download that confirmed the hash.</summary>
    public string? Verified { get; set; }
    [JsonPropertyName("etag")]
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    /// <summary>SPDX id of the source repository's licence, when it has one.</summary>
    public string? License { get; set; }
    /// <summary>Immutable copy in this repository's release assets. Absent = pinned: only the upstream URL and the hash are known.</summary>
    public string? MirrorUrl { get; set; }
    /// <summary>The games-index mod this file belongs to.</summary>
    public string? Mod { get; set; }
    public List<HistoryEntry> History { get; set; } = [];

    public FileEntry Clone()
    {
        var c = (FileEntry)MemberwiseClone();
        c.History = History.Select(h => h with { }).ToList();
        return c;
    }
}

public sealed record HistoryEntry(string Version, string Sha256, long Size, string FirstSeen, string? MirrorUrl);

public sealed class ModLinks
{
    public string? Nexus { get; set; }
    public string? Discord { get; set; }
    public string? Discussion { get; set; }
    public string? GameBanana { get; set; }
    public List<string> Other { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => Nexus is null && Discord is null && Discussion is null && GameBanana is null && Other.Count == 0;
}

public sealed class ModEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public List<int> SteamAppIds { get; set; } = [];
    public List<string> Authors { get; set; } = [];
    /// <summary>"main", "related", "engine" or "deprecated".</summary>
    public string Listing { get; set; } = "main";
    /// <summary>Upstream's own rating: stable, beta, experimental (games-index), when it has one.</summary>
    public string? Status { get; set; }
    /// <summary>"working" or "in-progress": the wiki table's mark, else the games-index rating. Absent when upstream hasn't said.</summary>
    public string? Compat { get; set; }
    public string? Notes { get; set; }
    /// <summary>"unreal" or "unity" when the game uses the generic engine mod instead of its own file.</summary>
    public string? Generic { get; set; }
    public ModLinks? Links { get; set; }
    public bool Blocked { get; set; }
    public string? BlockReason { get; set; }
    public List<FileEntry> Files { get; set; } = [];
}

public sealed class GenericEntry
{
    /// <summary>"unreal", "unity" or "ue-extended".</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<FileEntry> Files { get; set; } = [];
}

/// <summary>A row of the wiki's per-engine game tables (games known to work with the generic mod).</summary>
public sealed class EngineGame
{
    public string Engine { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Compat { get; set; }
    public string? Notes { get; set; }
}

public sealed class OverrideBlock
{
    public List<NameFix> NameFixes { get; set; } = [];
    public List<AppIdFix> AppIdFixes { get; set; } = [];
    public List<BlockItem> Blocklist { get; set; } = [];
    public List<UeExtendedOptOut> UeExtendedOptOut { get; set; } = [];
    public List<EngineIniHint> EngineIniPaths { get; set; } = [];
}

/// <summary>Renames the entry <see cref="Id"/> and/or adds store titles that should match it.</summary>
public sealed class NameFix
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public List<string> Aliases { get; set; } = [];
}

public sealed class AppIdFix
{
    public string Id { get; set; } = "";
    public List<int> SteamAppIds { get; set; } = [];
}

public sealed class BlockItem
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Reason { get; set; }
}

public sealed class UeExtendedOptOut
{
    public int? SteamAppId { get; set; }
    public string? Name { get; set; }
    public string? Reason { get; set; }
}

public sealed class EngineIniHint
{
    public int? SteamAppId { get; set; }
    public string? Name { get; set; }
    /// <summary>Path of the game's Engine.ini, relative to %LOCALAPPDATA% unless it is rooted or starts with a %variable%.</summary>
    public string Path { get; set; } = "";
}

public sealed class SourceInfo
{
    public string Url { get; set; } = "";
    public int Count { get; set; }
}

public sealed class RenodxSection
{
    public string? Updated { get; set; }
    public List<SourceInfo> Sources { get; set; } = [];
    public List<GenericEntry> Generic { get; set; } = [];
    public List<ModEntry> Mods { get; set; } = [];
    public List<EngineGame> EngineGames { get; set; } = [];
    public OverrideBlock Overrides { get; set; } = new();
}

/// <summary>overrides.json: hand-maintained, one block per section.</summary>
public sealed class OverridesFile
{
    public int Schema { get; set; } = 1;
    public OverrideBlock Renodx { get; set; } = new();
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
