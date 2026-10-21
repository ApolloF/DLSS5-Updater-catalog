using System.Text.Json;
using System.Text.RegularExpressions;

namespace CatalogGen;

/// <summary>Reads and checks shader-packs.json, the hand-maintained list of upstream shader repositories.</summary>
public static partial class ShaderPackLists
{
    public static readonly string[] Categories = ["hdr", "general", "color", "depth-3d", "cinematic"];
    public static readonly string[] Kinds = ["branch", "release-asset"];

    public static ShaderPackList Load(string path)
    {
        if (!File.Exists(path)) throw new SourceException($"{path} is missing");
        return Parse(File.ReadAllText(path));
    }

    public static ShaderPackList Parse(string json)
    {
        ShaderPackList list;
        try { list = JsonSerializer.Deserialize<ShaderPackList>(json, Json.Options) ?? new(); }
        catch (JsonException ex) { throw new SourceException($"shader-packs.json is not valid: {ex.Message}"); }
        var problems = Validate(list);
        if (problems.Count > 0) throw new SourceException("shader-packs.json: " + string.Join("; ", problems));
        return list;
    }

    public static List<string> Validate(ShaderPackList list)
    {
        var problems = new List<string>();
        if (list.Packs.Count == 0) problems.Add("no packs");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in list.Packs)
        {
            var who = string.IsNullOrWhiteSpace(p.Id) ? "(no id)" : p.Id;
            if (!IdRule().IsMatch(p.Id)) problems.Add($"{who}: id must be lowercase letters, digits and '-'");
            else if (!ids.Add(p.Id)) problems.Add($"{who}: duplicate id");
            if (string.IsNullOrWhiteSpace(p.Name)) problems.Add($"{who}: no name");
            if (!RepoRule().IsMatch(p.Repo)) problems.Add($"{who}: repo must be owner/repo");
            if (!Categories.Contains(p.Category)) problems.Add($"{who}: category must be one of {string.Join(", ", Categories)}");
            if (!Kinds.Contains(p.Source.Kind)) problems.Add($"{who}: source.kind must be one of {string.Join(", ", Kinds)}");
            else if (p.Source.Kind == "branch" && string.IsNullOrWhiteSpace(p.Source.Branch)) problems.Add($"{who}: a branch source needs a branch");
        }
        return problems;
    }

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdRule();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoRule();
}
