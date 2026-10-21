using System.Text.RegularExpressions;

namespace CatalogGen;

/// <summary>Which table of the wiki page a row came from.</summary>
public enum WikiTable { List, UeExtended, Unreal, Unity, Source, Related, Deprecated }

public sealed record WikiLink(string Kind, string Url)
{
    public const string File = "file";

    public bool IsFile => Kind == File;
}

public sealed record WikiRow(
    WikiTable Table, string Name, string? NameUrl, string? Maintainer, string? Status, string? StatusNote, string? Notes,
    IReadOnlyList<WikiLink> Links);

/// <summary>Reads the RenoDX wiki "Mods" page (GitHub-flavoured markdown tables); no HTML scraping.</summary>
public static partial class WikiParser
{
    public static List<WikiRow> Parse(string markdown)
    {
        var rows = new List<WikiRow>();
        WikiTable? table = null;
        var fenced = false;
        string[]? columns = null;
        var pastSeparator = false;

        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```")) { fenced = !fenced; continue; }
            if (fenced) continue;

            if (!line.StartsWith('|'))
            {
                columns = null;
                pastSeparator = false;
                if (HeadingToTable(line) is { } t) table = t;
                continue;
            }
            if (table is null) continue;

            var cells = SplitCells(line);
            if (columns is null)
            {
                columns = cells.Select(ColumnOf).ToArray();
                pastSeparator = false;
                continue;
            }
            if (!pastSeparator)
            {
                pastSeparator = true;
                if (cells.All(c => SeparatorCell().IsMatch(c))) continue;
            }
            if (ToRow(table.Value, columns, cells) is { } row) rows.Add(row);
        }
        return rows;
    }

    private static WikiTable? HeadingToTable(string line)
    {
        var h = line.Trim();
        if (!h.StartsWith('#')) return null;
        var text = h.TrimStart('#');
        if (text.Length == 0 || text[0] != ' ') return null;
        text = text.Trim();
        // The page's own headings: "# List", "### Unreal Engine Extended [badge](url)", "# Related Mods", ...
        if (text.StartsWith("List", StringComparison.OrdinalIgnoreCase)) return WikiTable.List;
        if (text.StartsWith("Unreal Engine Extended", StringComparison.OrdinalIgnoreCase)) return WikiTable.UeExtended;
        if (text.StartsWith("Unreal Engine", StringComparison.OrdinalIgnoreCase)) return WikiTable.Unreal;
        if (text.StartsWith("Unity Engine", StringComparison.OrdinalIgnoreCase)) return WikiTable.Unity;
        if (text.StartsWith("Source Engine", StringComparison.OrdinalIgnoreCase)) return WikiTable.Source;
        if (text.StartsWith("Related Mods", StringComparison.OrdinalIgnoreCase)) return WikiTable.Related;
        if (text.StartsWith("Deprecated", StringComparison.OrdinalIgnoreCase)) return WikiTable.Deprecated;
        return null;
    }

    private static string[] SplitCells(string line)
    {
        var body = line.Trim();
        if (body.StartsWith('|')) body = body[1..];
        if (body.EndsWith('|')) body = body[..^1];
        return body.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static string ColumnOf(string header)
    {
        var h = header.ToLowerInvariant();
        if (h.Contains("name")) return "name";
        if (h.Contains("maintainer") || h.Contains("author") || h.Contains("developer")) return "maintainer";
        if (h.Contains("link") || h.Contains("download")) return "links";
        if (h.Contains("status")) return "status";
        if (h.Contains("note")) return "notes";
        return "";
    }

    private static WikiRow? ToRow(WikiTable table, string[] columns, string[] cells)
    {
        string Cell(string column)
        {
            var i = Array.IndexOf(columns, column);
            return i >= 0 && i < cells.Length ? cells[i] : "";
        }

        var nameCell = Cell("name");
        if (nameCell.Length == 0) return null;
        var (name, nameUrl) = LinkText(nameCell);
        name = Names.Clean(StripHtml(name));
        if (name.Length == 0) return null;

        var statusCell = Cell("status");
        var status = statusCell.Contains("white_check_mark") || statusCell.Contains('✅') ? "working"
            : statusCell.Contains("construction") || statusCell.Contains("\U0001F6A7") ? "in-progress"
            : null;
        var tooltip = Tooltip().Match(statusCell);
        var statusNote = tooltip.Success && tooltip.Groups[1].Value.Trim() is { Length: > 0 } t ? t : null;
        var notes = Cell("notes");
        var noteText = notes.Length > 0 ? CleanNotes(notes) : null;

        return new WikiRow(table, name, nameUrl, NullIfEmpty(StripHtml(Cell("maintainer"))), status, statusNote,
            string.IsNullOrEmpty(noteText) ? null : noteText, ExtractLinks(Cell("links")));
    }

    public static List<WikiLink> ExtractLinks(string cell)
    {
        var links = new List<WikiLink>();
        foreach (Match m in MarkdownLink().Matches(cell))
        {
            var url = m.Groups[1].Value;
            var kind = url.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase)
                ? WikiLink.File
                : url.Contains("nexusmods.com", StringComparison.OrdinalIgnoreCase) ? "nexus"
                : url.Contains("discord.", StringComparison.OrdinalIgnoreCase) ? "discord"
                : url.Contains("gamebanana.com", StringComparison.OrdinalIgnoreCase) ? "gamebanana"
                : url.Contains("/discussions/", StringComparison.OrdinalIgnoreCase) ? "discussion"
                : "other";
            if (!links.Any(l => l.Url == url)) links.Add(new WikiLink(kind, url));
        }
        return links;
    }

    private static (string Text, string? Url) LinkText(string cell)
    {
        var m = PlainLink().Match(cell.Trim());
        return m.Success && m.Length == cell.Trim().Length ? (m.Groups[1].Value, m.Groups[2].Value) : (cell, null);
    }

    private static string CleanNotes(string notes)
    {
        var s = Details().Replace(notes, " ");
        s = BreakTag().Replace(s, "\n");
        s = StripHtml(s).Replace("`", "");
        return string.Join("\n", s.Split('\n').Select(Names.Clean).Where(l => l.Length > 0));
    }

    private static string StripHtml(string s) => HtmlTag().Replace(s, "");

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : Names.Clean(s);

    [GeneratedRegex(@"^:?-{3,}:?$")]
    private static partial Regex SeparatorCell();

    // [text](url) and [![alt](img)](url): the target is the outer url.
    [GeneratedRegex("""\[(?:!\[[^\]]*\]\([^)]*\)|[^\]]*)\]\((https?://[^)\s]+)(?:\s+"[^"]*")?\)""")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^\[([^\]]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex PlainLink();

    [GeneratedRegex("""\(#\s*"([^"]*)"\)""")]
    private static partial Regex Tooltip();

    [GeneratedRegex("<details>.*?</details>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Details();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTag();
}
