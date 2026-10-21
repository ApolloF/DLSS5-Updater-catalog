using System.Text.RegularExpressions;

namespace CatalogGen;

/// <summary>Decides from a source repository's licence whether we may keep a copy of its files.</summary>
public static partial class Licence
{
    private static readonly HashSet<string> Permissive = new(StringComparer.OrdinalIgnoreCase)
    {
        "MIT", "BSD-2-Clause", "BSD-3-Clause", "Apache-2.0", "ISC", "Unlicense",
    };

    /// <summary>MIT, BSD-2/3, Apache-2.0, ISC and Unlicense only. No licence, NOASSERTION, OTHER and copyleft are not enough.</summary>
    public static bool AllowsMirror(string? spdx) => spdx is not null && Permissive.Contains(spdx.Trim());

    /// <summary>"owner/repo" the file at <paramref name="url"/> is published from, or null when we can't tell.</summary>
    public static string? RepoOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var host = uri.Host.ToLowerInvariant();
        if (host is "github.com" or "www.github.com")
            return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : null;
        // https://<owner>.github.io/<repo>/file
        var m = PagesHost().Match(host);
        if (m.Success && parts.Length >= 2) return $"{m.Groups[1].Value}/{parts[0]}";
        return null;
    }

    [GeneratedRegex(@"^([a-z0-9-]+)\.github\.io$")]
    private static partial Regex PagesHost();
}
