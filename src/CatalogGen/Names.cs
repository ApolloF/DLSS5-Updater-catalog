using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CatalogGen;

public static partial class Names
{
    /// <summary>Lowercase alphanumerics only, diacritics and trademark signs gone: "Assassin's Creed® II" and "assassins creed ii" compare equal.</summary>
    public static string Normalize(string name)
    {
        var sb = new StringBuilder(name.Length);
        // Trademark signs first: their compatibility decomposition would otherwise leave "TM" in the name.
        foreach (var c in RemoveMarks(name).Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Display name from the wiki: trademark signs and broken-encoding marks removed, a mangled apostrophe before "s" restored.</summary>
    public static string Clean(string name)
    {
        var s = MangledApostrophe().Replace(name, "’");
        s = RemoveMarks(s).Replace("�", "");
        return Spaces().Replace(s, " ").Trim();
    }

    private static string RemoveMarks(string s) => s.Replace("™", "").Replace("®", "").Replace("©", "").Replace("℠", "");

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        var dash = false;
        foreach (var c in RemoveMarks(name).Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsAsciiLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); dash = false; }
            else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
        }
        return sb.ToString().TrimEnd('-');
    }

    [GeneratedRegex(@"(?<=\p{L})�(?=s\b)")]
    private static partial Regex MangledApostrophe();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();
}
