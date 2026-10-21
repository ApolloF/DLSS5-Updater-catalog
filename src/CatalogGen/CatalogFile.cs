using System.Text.Json;
using System.Text.Json.Nodes;

namespace CatalogGen;

/// <summary>
/// catalog.json as a loose JSON tree, so a run only replaces the sections it owns and keeps the rest byte for byte.
/// Timestamps change only when a section's content did, so an unchanged run leaves no diff to commit.
/// </summary>
public static class CatalogFile
{
    public const int Schema = 1;
    public const string Repository = "ApolloF/DLSS5-Updater-catalog";

    public static JsonObject Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root) return Normalize(root);
        }
        catch (JsonException) { }
        return Normalize(new JsonObject());
    }

    private static JsonObject Normalize(JsonObject root)
    {
        root["schema"] = Schema;
        root["repository"] = Repository;
        if (root["sections"] is not JsonObject) root["sections"] = new JsonObject();
        return root;
    }

    public static T? ReadSection<T>(JsonObject root, string name) where T : class
    {
        try { return root["sections"]?[name]?.Deserialize<T>(Json.Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Replaces section <paramref name="name"/>. Returns true when its content changed (timestamps are then refreshed).</summary>
    public static bool SetSection<T>(JsonObject root, string name, T section, DateTimeOffset now)
    {
        var fresh = JsonSerializer.SerializeToNode(section, Json.Options)!.AsObject();
        var sections = root["sections"]!.AsObject();
        var old = sections[name] as JsonObject;

        fresh.Remove("updated");
        if (old is not null && Comparable(old) == fresh.ToJsonString(Json.Options))
            return false;

        var stamped = new JsonObject { ["updated"] = FileSync.Iso(now) };
        foreach (var (k, v) in fresh.ToList()) { fresh.Remove(k); stamped[k] = v; }
        sections[name] = stamped;
        root["generated"] = FileSync.Iso(now);
        return true;
    }

    private static string Comparable(JsonObject section)
    {
        var copy = (JsonObject)section.DeepClone();
        copy.Remove("updated");
        return copy.ToJsonString(Json.Options);
    }

    public static string Serialize(JsonObject root) => root.ToJsonString(Json.Options).Replace("\r\n", "\n") + "\n";

    /// <summary>Writes only when the text differs, so git sees no change for an unchanged catalog.</summary>
    public static bool Save(string path, JsonObject root)
    {
        var text = Serialize(root);
        if (File.Exists(path) && File.ReadAllText(path) == text) return false;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, true);
        return true;
    }
}
