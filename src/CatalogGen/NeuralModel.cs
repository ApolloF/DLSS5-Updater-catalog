namespace CatalogGen;

/// <summary>One mirrored binary: a copy in this repository's release assets, named and hashed.</summary>
public sealed class NeuralBuild
{
    /// <summary>The file's name for the reader to save it under, e.g. nvngx_dlssnr.dll.</summary>
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>The PE file version as digits (0.2026.828.517). Tells apart builds that share one <c>version</c> label; absent when the file has none.</summary>
    public string? FileVersion { get; set; }
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string FirstSeen { get; set; } = "";
    public string MirrorUrl { get; set; } = "";
    /// <summary>Lecram only: "v10" (renodx-dlss5.addon64, v10 and older) or "v11" (dlss5-nr.addon64).</summary>
    public string? Generation { get; set; }
    /// <summary>The upstream download this build was taken from; absent for a file that was dropped in by hand. Provenance, readers ignore it.</summary>
    public string? Url { get; set; }
}

public class NeuralStream
{
    /// <summary>The newest build; absent until one has been mirrored.</summary>
    public NeuralBuild? Current { get; set; }
    /// <summary>Older builds, newest first, at most 19.</summary>
    public List<NeuralBuild> History { get; set; } = [];
}

public sealed class NeuralLecram : NeuralStream
{
    /// <summary>Generation of <c>current</c>; the two generations can't run together in one game.</summary>
    public string? Generation { get; set; }
}

public sealed class NeuralSection
{
    public string? Updated { get; set; }
    public string Source { get; set; } = "";
    public NeuralStream Dlssnr { get; set; } = new();
    public NeuralLecram Lecram { get; set; } = new();
}
