using System.Text;
using System.Text.RegularExpressions;

namespace CatalogGen;

public sealed record CompatOutput(OptiScalerCompatSection Section, List<string> Warnings);

/// <summary>
/// Builds the <c>optiscalerCompat</c> section from two OptiScaler wiki pages (raw Markdown): the compatibility list and the
/// "HUDfix incompatible" list. Nothing is downloaded or mirrored. The wiki text has no stated licence, so only facts are
/// copied (name, status, inputs, a short plain-text note) and every entry carries the wiki page it came from.
/// </summary>
public sealed partial class OptiScalerCompatGenerator(ITextSource text, int minMainGames = 100, int minHudfix = 5)
{
    public const string CompatUrl = "https://raw.githubusercontent.com/wiki/optiscaler/OptiScaler/Compatibility-List.md";
    public const string HudfixUrl = "https://raw.githubusercontent.com/wiki/optiscaler/OptiScaler/Hudfix-incompatible.md";

    public async Task<CompatOutput> BuildAsync(CancellationToken ct)
    {
        var compat = await ReadAsync(CompatUrl, "Compatibility-List", ct);
        var hudfix = await ReadAsync(HudfixUrl, "Hudfix-incompatible", ct);

        var warnings = new List<string>();
        var games = ParseGames(compat, warnings);
        var main = games.Count(g => g.List == "main");
        if (main < minMainGames) throw new SourceException($"the OptiScaler compatibility list has only {main} main games, looks truncated");
        var names = ParseHudfix(hudfix);
        if (names.Count < minHudfix) throw new SourceException($"the HUDfix list has only {names.Count} games, looks truncated");

        return new CompatOutput(new OptiScalerCompatSection { Source = CompatUrl, HudfixSource = HudfixUrl, Games = games, HudfixIncompatible = names }, warnings);
    }

    private async Task<string> ReadAsync(string url, string label, CancellationToken ct)
    {
        try { return await text.GetStringAsync(url, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SourceException($"wiki {label}: {ex.Message}");
        }
    }

    public static List<CompatGame> ParseGames(string markdown, List<string> warnings)
    {
        var games = new List<CompatGame>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = "main";
        var columns = 0;
        var hasPatcher = false;

        foreach (var raw in CommentRule().Replace(markdown, "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("## "))
            {
                if (line.Contains("Upscaler mods support", StringComparison.OrdinalIgnoreCase)) list = "upscalerMods";
                else if (line.Contains("Luma Unreal Engine", StringComparison.OrdinalIgnoreCase)) list = "lumaUe";
                columns = 0;
                continue;
            }
            // A row may lack its leading pipe upstream (Metro Exodus does); inside a table that is still a row.
            if (!line.StartsWith('|') && (columns == 0 || !line.Contains(" | "))) continue;

            var cells = SplitRow(line);
            if (cells[0] == "Game")
            {
                columns = cells.Count;
                hasPatcher = cells.Any(c => c.Contains("OptiPatcher", StringComparison.OrdinalIgnoreCase));
                continue;
            }
            if (columns == 0 || cells.All(c => SeparatorCell().IsMatch(c))) continue;

            // Name, status and inputs (and the OptiPatcher mark) come first; the last cell is the images, which the wiki
            // sometimes leaves out. Whatever sits in between is the note, so an unescaped pipe in it survives.
            var fixedCells = hasPatcher ? 4 : 3;
            if (cells.Count < fixedCells + 1) { warnings.Add($"{list}: row has {cells.Count} cells, expected {columns}: {Shorten(line)}"); continue; }

            var (name, page) = ParseGame(cells[0]);
            var status = cells[1].Contains('✅') ? "working" : cells[1].Contains('❌') ? "not-working" : cells[1].Contains('➖') ? "single-os" : null;
            if (name.Length == 0 || status is null) { warnings.Add($"{list}: unreadable row skipped: {Shorten(line)}"); continue; }

            var noteEnd = cells.Count >= columns ? cells.Count - 1 : cells.Count;
            var notes = Notes(string.Join(" | ", cells.Skip(fixedCells).Take(noteEnd - fixedCells)));
            if (!seen.Add(list + "\n" + name)) { warnings.Add($"{list}: duplicate game '{name}' ignored"); continue; }

            games.Add(new CompatGame
            {
                Name = name,
                Page = page,
                List = list,
                Compat = status,
                Inputs = cells[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
                OptiPatcher = hasPatcher && cells[3].Length > 0,
                Notes = notes.Length > 0 ? notes : null,
            });
        }
        return games;
    }

    /// <summary>Bullets before the first <c>---</c> line are game names; the ones after it are notes.</summary>
    public static List<string> ParseHudfix(string markdown)
    {
        var names = new List<string>();
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line == "---") break;
            if (!line.StartsWith("* ")) continue;
            var name = line[2..].Trim();
            if (name.Length > 0 && !names.Contains(name)) names.Add(name);
        }
        return names;
    }

    private static List<string> SplitRow(string line)
    {
        var body = line.StartsWith('|') ? line[1..] : line;
        if (body.EndsWith('|') && !body.EndsWith("\\|")) body = body[..^1];
        return UnescapedPipe().Split(body).Select(c => c.Trim()).ToList();
    }

    /// <summary>The game cell is <c>[Name](Slug)</c> or plain text. The slug may hold parentheses, so the link is closed by balancing.</summary>
    private static (string Name, string? Page) ParseGame(string cell)
    {
        if (cell.StartsWith('[') && ReadLink(cell, 0) is { } link && link.End == cell.Length)
            return (link.Text.Trim(), link.Target.Trim());
        return (cell, null);
    }

    private readonly record struct Link(string Text, string Target, int End);

    /// <summary>Reads <c>[text](target)</c> at <paramref name="start"/>; null when it isn't a complete link.</summary>
    private static Link? ReadLink(string s, int start)
    {
        var depth = 0;
        var i = start;
        for (; i < s.Length; i++)
        {
            if (s[i] == '[') depth++;
            else if (s[i] == ']' && --depth == 0) break;
        }
        if (i + 1 >= s.Length || s[i + 1] != '(') return null;

        var close = i + 1;
        depth = 0;
        for (; close < s.Length; close++)
        {
            if (s[close] == '(') depth++;
            else if (s[close] == ')' && --depth == 0) break;
        }
        if (close >= s.Length) return null;
        return new Link(s[(start + 1)..i], s[(i + 2)..close], close + 1);
    }

    /// <summary>Plain text: <c>&lt;br&gt;</c> becomes a space, links keep their text, whitespace is collapsed.</summary>
    private static string Notes(string note)
    {
        var s = BreakRule().Replace(note, " ");
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '[' && ReadLink(s, i) is { } link)
            {
                sb.Append(link.Text);
                i = link.End - 1;
            }
            else sb.Append(s[i]);
        }
        return WhitespaceRule().Replace(sb.ToString(), " ").Trim();
    }

    private static string Shorten(string line) => line.Length <= 80 ? line : line[..80] + "...";

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRule();

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex UnescapedPipe();

    [GeneratedRegex(@"^:?-{2,}:?$")]
    private static partial Regex SeparatorCell();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRule();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRule();
}
