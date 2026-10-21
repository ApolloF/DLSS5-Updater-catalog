using System.Text;

namespace CatalogGen;

/// <summary>Everything the release notes say about one source repository whose files are mirrored in a month.</summary>
public sealed record RepoNotes(string Repo, string? Spdx, string? LicenceUrl, IReadOnlyList<string> Copyright, IReadOnlyList<string> Files);

/// <summary>
/// MIT, BSD and Apache require the copyright and licence notice to travel with copies, so the monthly mirror release lists,
/// per source repository, its licence, the copyright lines and the mirrored files. The full licence text is not copied.
/// </summary>
public static class MirrorNotes
{
    public const string Intro = "Immutable copies of upstream mod files, named by SHA-256 prefix. They belong to their authors under the licences listed in catalog.json; see the README.";

    /// <summary>The "Copyright ..." lines of a licence text, trimmed and without duplicates. The template line of the Apache appendix is not a notice.</summary>
    public static List<string> CopyrightLines(string? licenceText)
    {
        var lines = new List<string>();
        foreach (var raw in (licenceText ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("[yyyy]", StringComparison.OrdinalIgnoreCase) || line.Contains("[name of copyright owner]", StringComparison.OrdinalIgnoreCase)) continue;
            if (!lines.Contains(line, StringComparer.Ordinal)) lines.Add(line);
        }
        return lines;
    }

    /// <summary>The release body: the intro, then one block per repository sorted by name with its files sorted. Same input, same text.</summary>
    public static string Build(IEnumerable<RepoNotes> repos)
    {
        var sb = new StringBuilder(Intro);
        foreach (var r in repos.OrderBy(r => r.Repo, StringComparer.OrdinalIgnoreCase))
        {
            var repoUrl = $"https://github.com/{r.Repo}";
            var licenceUrl = r.LicenceUrl ?? repoUrl;
            sb.Append("\n\n### ").Append(r.Repo);
            sb.Append("\n- Repository: ").Append(repoUrl);
            sb.Append("\n- Licence: ").Append(r.Spdx ?? "see the licence file").Append(" (").Append(licenceUrl).Append(')');
            if (r.Copyright.Count == 0)
            {
                sb.Append("\n- Copyright holder: see the licence file at ").Append(licenceUrl);
            }
            else
            {
                sb.Append("\n- Copyright:");
                foreach (var line in r.Copyright) sb.Append("\n  - ").Append(line);
            }
            sb.Append("\n- Files:");
            foreach (var file in r.Files.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)) sb.Append("\n  - ").Append(file);
        }
        return sb.ToString();
    }

    /// <summary>Takes the first paragraph of <paramref name="existing"/> (a release may have had its intro edited by hand) and the rest of <paramref name="body"/>.</summary>
    public static string WithIntroOf(string? existing, string body)
    {
        var intro = FirstParagraph(Normalize(existing));
        var rest = Normalize(body);
        var cut = rest.IndexOf("\n\n", StringComparison.Ordinal);
        var tail = cut < 0 ? "" : rest[cut..];
        return (intro.Length > 0 ? intro : cut < 0 ? rest : rest[..cut]) + tail;
    }

    public static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Trim();

    private static string FirstParagraph(string text)
    {
        var cut = text.IndexOf("\n\n", StringComparison.Ordinal);
        return cut < 0 ? text : text[..cut];
    }
}
