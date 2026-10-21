using System.Text.RegularExpressions;

namespace CatalogGen;

public sealed record LumaWikiMod(
    string Name, List<string> Authors, string? Status, string? Notes, ModLinks Links, string? Asset, bool RequiresDgVoodoo,
    List<string> DgVoodooVersions, List<string> DgVoodooSetup, string? FeaturesUrl);

public sealed record LumaWiki(List<LumaWikiMod> Mods, List<LumaUnrealGame> UnrealGames);

/// <summary>Reads the Luma Framework wiki home page (markdown tables): the "Completed Mods" table and the generic "Unreal Engine" table.</summary>
public static partial class LumaWikiParser
{
    private const string ReleaseDownload = "/Luma-Framework/releases/latest/download/";
    private const string DefaultIniSection = "SystemSettings";

    public static LumaWiki Parse(string markdown)
    {
        var mods = new List<LumaWikiMod>();
        var games = new List<LumaUnrealGame>();
        string? section = null;
        string[]? columns = null;
        var fenced = false;

        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```")) { fenced = !fenced; continue; }
            if (fenced) continue;
            if (!line.StartsWith('|'))
            {
                columns = null;
                if (line.StartsWith("# ")) section = line[2..].Trim().ToLowerInvariant();
                continue;
            }
            if (section is not ("completed mods" or "unreal engine")) continue;

            var cells = SplitCells(line);
            if (columns is null) { columns = cells.Select(ColumnOf).ToArray(); continue; }
            if (cells.All(c => SeparatorCell().IsMatch(c))) continue;
            if (section == "completed mods")
            {
                if (ModRow(columns, cells) is { } mod) mods.Add(mod);
            }
            else if (UnrealRow(columns, cells) is { } game) games.Add(game);
        }
        return new LumaWiki(mods, games);
    }

    private static LumaWikiMod? ModRow(string[] columns, string[] cells)
    {
        var name = Names.Clean(StripHtml(Cell(columns, cells, "name")));
        if (name.Length == 0) return null;

        var links = new ModLinks();
        string? asset = null;
        foreach (Match m in MarkdownLink().Matches(Cell(columns, cells, "links")))
        {
            var url = m.Groups[1].Value;
            var at = url.IndexOf(ReleaseDownload, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) asset ??= url[(at + ReleaseDownload.Length)..];
            else if (url.Contains("nexusmods.com", StringComparison.OrdinalIgnoreCase)) links.Nexus ??= url;
            else if (url.Contains("gamebanana.com", StringComparison.OrdinalIgnoreCase)) links.GameBanana ??= url;
            else if (url.Contains("discord.", StringComparison.OrdinalIgnoreCase)) links.Discord ??= url;
            else if (!links.Other.Contains(url)) links.Other.Add(url);
        }

        var statusCell = Cell(columns, cells, "status");
        var status = statusCell.Contains('✅') ? "working" : statusCell.Contains("\U0001F6A7") ? "in-progress" : null;
        var rawNotes = Cell(columns, cells, "notes");
        var versions = new List<string>();
        foreach (Match m in DgVoodooVersion().Matches(Details().Replace(rawNotes, " ")))
            if (!versions.Contains(m.Groups[1].Value)) versions.Add(m.Groups[1].Value);
        var setup = new List<string>();
        foreach (Match d in Details().Matches(rawNotes))
            if (d.Groups[1].Value.Contains("dgVoodoo", StringComparison.OrdinalIgnoreCase)) setup.AddRange(Lines(d.Groups[2].Value));

        var features = FeaturesLink().Match(Cell(columns, cells, "features"));
        return new LumaWikiMod(name, Authors(Cell(columns, cells, "author")), status, NullIfEmpty(CleanNotes(rawNotes)), links, asset,
            rawNotes.Contains("dgVoodoo", StringComparison.OrdinalIgnoreCase), versions, setup, features.Success ? features.Groups[1].Value : null);
    }

    private static LumaUnrealGame? UnrealRow(string[] columns, string[] cells)
    {
        var name = Names.Clean(StripHtml(Cell(columns, cells, "name")));
        if (name.Length == 0) return null;
        var rawNotes = Cell(columns, cells, "notes");

        var ini = new List<IniSetting>();
        foreach (Match d in Details().Matches(rawNotes))
        {
            if (!d.Groups[1].Value.Contains("Engine.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var section = DefaultIniSection;
            foreach (var l in Lines(d.Groups[2].Value))
            {
                if (l.StartsWith('[') && l.EndsWith(']')) { section = l[1..^1]; continue; }
                var eq = l.IndexOf('=');
                if (eq <= 0) continue;
                var setting = new IniSetting(section, l[..eq].Trim(), l[(eq + 1)..].Trim());
                if (!ini.Contains(setting)) ini.Add(setting);
            }
        }

        var args = new List<string>();
        foreach (var rx in new[] { CodeArgument(), BareArgument() })
            foreach (Match m in rx.Matches(rawNotes))
                if (!args.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)) args.Add(m.Groups[1].Value);

        return new LumaUnrealGame
        {
            Name = name,
            Dlss = Flag(Cell(columns, cells, "dlss")),
            Hdr = Flag(Cell(columns, cells, "hdr")),
            UeVersion = NullIfEmpty(Cell(columns, cells, "version")),
            Notes = NullIfEmpty(CleanNotes(rawNotes)),
            EngineIni = ini,
            LaunchArgs = args,
        };
    }

    private static string? Flag(string cell) =>
        cell.Contains('✅') ? "yes" : cell.Contains('⛔') ? "blocked" : cell.Contains("\U0001F6A7") ? "in-progress" : null;

    private static List<string> Authors(string cell) =>
        StripHtml(cell).Split(['&', '+', ','], StringSplitOptions.RemoveEmptyEntries).Select(Names.Clean).Where(a => a.Length > 0).ToList();

    /// <summary>The lines of a markdown fragment with <c>&lt;br&gt;</c> breaks: html, emphasis, bullets and code ticks removed.</summary>
    private static IEnumerable<string> Lines(string fragment) =>
        BreakTag().Replace(fragment, "\n").Split('\n')
            .Select(l => Names.Clean(StripHtml(l).Replace("`", "").Replace("**", "").Replace("•", "")))
            .Where(l => l.Length > 0);

    /// <summary>Notes as plain text: the Engine.ini and dgVoodoo blocks (kept as data) are dropped, other collapsible blocks stay.</summary>
    private static string CleanNotes(string notes)
    {
        var s = Details().Replace(notes, m =>
            m.Groups[1].Value.Contains("Engine.ini", StringComparison.OrdinalIgnoreCase)
            || m.Groups[1].Value.Contains("dgVoodoo", StringComparison.OrdinalIgnoreCase) ? " " : " " + m.Groups[2].Value + " ");
        return string.Join("\n", Lines(s));
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
        if (h.Contains("dlss")) return "dlss";
        if (h.Contains("hdr")) return "hdr";
        if (h.Contains("version")) return "version";
        if (h.Contains("name")) return "name";
        if (h.Contains("author")) return "author";
        if (h.Contains("download") || h.Contains("link")) return "links";
        if (h.Contains("status")) return "status";
        if (h.Contains("note")) return "notes";
        if (h.Contains("feature")) return "features";
        return "";
    }

    private static string Cell(string[] columns, string[] cells, string column)
    {
        var i = Array.IndexOf(columns, column);
        return i >= 0 && i < cells.Length ? cells[i] : "";
    }

    private static string StripHtml(string s) => HtmlTag().Replace(s, "");

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [GeneratedRegex(@"^:?-{3,}:?$")]
    private static partial Regex SeparatorCell();

    // [text](url) and [![alt][ref]](url): the target is the outer url.
    [GeneratedRegex("""\]\((https?://[^)\s]+)\)""")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex("""\[[^\]]*\]\((https://github\.com/Filoppi/Luma-Framework/wiki#[^)\s]+)\)""")]
    private static partial Regex FeaturesLink();

    [GeneratedRegex("<details>\\s*<summary>(.*?)</summary>(.*?)</details>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Details();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"dgVoodoo2?\s*v?(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex DgVoodooVersion();

    // `-dx11`
    [GeneratedRegex(@"`(-[A-Za-z][\w=.]*)`")]
    private static partial Regex CodeArgument();

    // -DX11 launch argument
    [GeneratedRegex(@"(?<![\w`-])(-[A-Za-z]\w*)\s+launch\b", RegexOptions.IgnoreCase)]
    private static partial Regex BareArgument();
}
