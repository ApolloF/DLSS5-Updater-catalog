using System.Text.RegularExpressions;

namespace CatalogGen;

/// <summary>
/// Add-ons in crosire's list that DLSS5 Updater already manages as components of their own (HDR mods, frame limiters,
/// DLSS add-ons). The catalog marks them <c>managedBy</c> and lists no files, so the app points at the component instead
/// of installing a second copy.
/// </summary>
public static class ManagedComponents
{
    private static readonly (string Id, Regex Match)[] Rules =
    [
        ("renodx", new(@"clshortfuse/renodx|\brenodx\b", RegexOptions.IgnoreCase)),
        ("display-commander", new(@"display[- ]?commander", RegexOptions.IgnoreCase)),
        ("relimiter", new(@"re-?limiter", RegexOptions.IgnoreCase)),
        ("dfc", new(@"deep[- ]?fried[- ]?chicken|\bdfc\b", RegexOptions.IgnoreCase)),
        ("lecram-dlss5", new(@"lecram", RegexOptions.IgnoreCase)),
        ("gitc-uplift", new(@"gitc|uplift", RegexOptions.IgnoreCase)),
        ("dlss5-feeder", new(@"dlss5[- ]?feeder", RegexOptions.IgnoreCase)),
    ];

    /// <summary>The component that manages this package, or null. Looks at the name, the repository and the download addresses.</summary>
    public static string? For(AddonSource s)
    {
        var haystack = string.Join(' ', new[] { s.PackageName, s.Repository, s.Url, s.Url32, s.Url64 }.Where(x => x is not null));
        return Rules.FirstOrDefault(r => r.Match.IsMatch(haystack)).Id;
    }
}
