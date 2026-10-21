using System.IO.Compression;

namespace CatalogGen;

/// <summary>Tells a real download from an HTML error page: a PE file, or a zip that holds the add-on.</summary>
public static class DownloadCheck
{
    public const string ZipEntryName = "renodx-dlss.addon64";

    /// <summary>Null when the file is acceptable, else why not. <paramref name="head"/> is the first bytes of the file.</summary>
    public static string? Problem(string path, ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && head[0] == 'M' && head[1] == 'Z') return null;
        if (head.Length >= 4 && head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4) return ZipProblem(path);
        return "not a Windows executable or zip (no MZ or PK header, probably an HTML page)";
    }

    private static string? ZipProblem(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Any(e => e.FullName.Replace('\\', '/').Split('/')[^1].Equals(ZipEntryName, StringComparison.OrdinalIgnoreCase))
                ? null
                : $"zip has no {ZipEntryName}";
        }
        catch (InvalidDataException ex)
        {
            return $"not a readable zip ({ex.Message})";
        }
    }
}
