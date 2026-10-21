using System.Text.Json;

namespace CatalogGen;

public sealed record IndexArtifact(string Name, string Arch, string Url, string Mod);

public sealed record IndexMod(
    string Id, string Title, string Category, string? Status, string? Compatibility, string? Support,
    List<string> Maintainers, List<string> Notes, string? Summary, List<(string Kind, string Url)> Urls,
    List<int> SteamAppIds, List<IndexArtifact> Artifacts);

public sealed record IndexGame(string Id, string Title, List<string> Aliases, List<int> SteamAppIds, List<IndexMod> Mods);

/// <summary>Reads RenoDX's published games-index.json. Artifact urls are relative to the file's own location.</summary>
public static class GamesIndex
{
    public static List<IndexGame> Parse(string json, Uri baseUrl)
    {
        using var doc = JsonDocument.Parse(json);
        var games = new List<IndexGame>();
        if (!doc.RootElement.TryGetProperty("games", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new FormatException("games-index.json has no games array");

        foreach (var g in list.EnumerateArray())
        {
            var id = Str(g, "id");
            var title = Str(g, "title");
            if (id is null || title is null) continue;
            var appIds = new List<int>();
            AddAppId(appIds, g);

            var mods = new List<IndexMod>();
            if (g.TryGetProperty("mods", out var modList) && modList.ValueKind == JsonValueKind.Array)
                foreach (var m in modList.EnumerateArray())
                    if (ParseMod(m, baseUrl) is { } mod)
                    {
                        mods.Add(mod);
                        foreach (var a in mod.SteamAppIds) if (!appIds.Contains(a)) appIds.Add(a);
                    }
            games.Add(new IndexGame(id, title, Strings(g, "aliases"), appIds, mods));
        }
        return games;
    }

    private static IndexMod? ParseMod(JsonElement m, Uri baseUrl)
    {
        var id = Str(m, "id");
        if (id is null) return null;
        var appIds = new List<int>();
        AddAppId(appIds, m);

        var artifacts = new List<IndexArtifact>();
        if (m.TryGetProperty("artifacts", out var arts) && arts.ValueKind == JsonValueKind.Array)
            foreach (var a in arts.EnumerateArray())
            {
                var name = Str(a, "name");
                var rel = Str(a, "url");
                if (name is null || rel is null || !Uri.TryCreate(baseUrl, rel, out var abs)) continue;
                artifacts.Add(new IndexArtifact(name, ArchOf(Str(a, "arch"), name), abs.AbsoluteUri, id));
            }

        var urls = new List<(string, string)>();
        if (m.TryGetProperty("urls", out var us) && us.ValueKind == JsonValueKind.Array)
            foreach (var u in us.EnumerateArray())
                if (Str(u, "url") is { } url) urls.Add((Str(u, "kind") ?? "", url));

        return new IndexMod(id, Str(m, "title") ?? id, Str(m, "category") ?? "game", Str(m, "status"), Str(m, "compatibility"),
            Str(m, "support"), Strings(m, "maintainers"), Strings(m, "notes"), Str(m, "summary"), urls, appIds, artifacts);
    }

    private static string ArchOf(string? arch, string fileName)
    {
        if (arch is "x86" or "x32") return "x86";
        if (arch is "x64") return "x64";
        return fileName.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase) ? "x86" : "x64";
    }

    private static void AddAppId(List<int> ids, JsonElement e)
    {
        if (e.TryGetProperty("steam_appid", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var id) && id > 0 && !ids.Contains(id))
            ids.Add(id);
        if (e.TryGetProperty("deploy", out var d) && d.ValueKind == JsonValueKind.Object) AddAppId(ids, d);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(s => s.Length > 0).ToList()
            : [];
}
