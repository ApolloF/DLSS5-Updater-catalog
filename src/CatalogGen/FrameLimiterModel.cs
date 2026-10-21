namespace CatalogGen;

/// <summary>One frame-limiter add-on: the release its files came from and the add-on binaries (64-bit, 32-bit when the release has one).</summary>
public sealed class LimiterEntry
{
    /// <summary>"relimiter" or "display-commander": the ids the app and the add-ons section use for these components.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>owner/repo the files are published from.</summary>
    public string Repository { get; set; } = "";
    /// <summary>The release tag the files were read from (ReLimiter: v3.3.5; Display Commander republishes the tag "latest").</summary>
    public string Release { get; set; } = "";
    /// <summary>SPDX id of the repository's licence; absent when it has none (then the files are pinned, never mirrored).</summary>
    public string? License { get; set; }
    public List<FileEntry> Files { get; set; } = [];
}

public sealed class FrameLimitersSection
{
    public string? Updated { get; set; }
    public List<SourceInfo> Sources { get; set; } = [];
    public List<LimiterEntry> Limiters { get; set; } = [];
}
