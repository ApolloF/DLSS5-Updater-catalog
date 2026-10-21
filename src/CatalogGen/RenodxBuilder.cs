namespace CatalogGen;

/// <summary>Where the generic (not game-specific) RenoDX add-ons are published. Each url was checked to answer 200 with a PE file.</summary>
public static class RenodxSources
{
    public const string GamesIndexUrl = "https://github.com/clshortfuse/renodx/releases/download/snapshot/games-index.json";
    public const string WikiUrl = "https://raw.githubusercontent.com/wiki/clshortfuse/renodx/Mods.md";

    public static List<GenericEntry> Generics() =>
    [
        new()
        {
            Id = "unreal", Name = "Unreal Engine (generic)",
            Files = [File("https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-unrealengine.addon64", "x64")],
        },
        new()
        {
            Id = "unity", Name = "Unity Engine (generic)",
            Files =
            [
                File("https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon64", "x64"),
                File("https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon32", "x86"),
            ],
        },
        new()
        {
            Id = "ue-extended", Name = "Unreal Engine Extended",
            Files = [File("https://marat569.github.io/renodx/renodx-ue-extended.addon64", "x64")],
        },
    ];

    private static FileEntry File(string url, string arch) => new() { Name = url[(url.LastIndexOf('/') + 1)..], Arch = arch, Url = url };
}

/// <summary>Merges games-index.json (Steam AppIDs, file list) with the wiki Mods table (maintainers, links, notes, mods the index lacks).</summary>
public static class RenodxBuilder
{
    public sealed record Result(RenodxSection Section, List<string> Warnings);

    public static Result Build(IReadOnlyList<IndexGame> index, IReadOnlyList<WikiRow> wiki, OverrideBlock overrides)
    {
        var warnings = new List<string>();
        var mods = new List<ModEntry>();
        foreach (var g in index) mods.Add(FromIndex(g));

        var usedIds = new HashSet<string>(mods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        var byFile = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, ModEntry>();
        foreach (var m in mods) Register(m, byFile, byName);

        foreach (var row in wiki.Where(r => r.Table is WikiTable.List or WikiTable.Related or WikiTable.Deprecated))
        {
            var entry = FindEntry(row, byFile, byName);
            if (entry is null)
            {
                entry = new ModEntry { Id = UniqueId(Names.Slug(row.Name), usedIds), Name = row.Name, Listing = ListingOf(row.Table) };
                mods.Add(entry);
            }
            else if (row.Table == WikiTable.Deprecated && entry.Listing == "main") entry.Listing = "deprecated";
            Merge(entry, row);
            Register(entry, byFile, byName);
        }

        var section = new RenodxSection
        {
            Generic = RenodxSources.Generics(),
            Mods = mods,
            EngineGames = wiki
                .Where(r => r.Table is WikiTable.UeExtended or WikiTable.Unreal or WikiTable.Unity or WikiTable.Source)
                .Select(r => EngineGameOf(r, warnings))
                .OrderBy(e => e.Engine, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Overrides = overrides,
        };
        ApplyOverrides(section, overrides, warnings);

        foreach (var m in mods)
        {
            m.Files = m.Files.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            m.Aliases = m.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        section.Mods = mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        return new Result(section, warnings);
    }

    private static ModEntry FromIndex(IndexGame g)
    {
        var e = new ModEntry { Id = g.Id, Name = g.Title, Aliases = [.. g.Aliases], SteamAppIds = [.. g.SteamAppIds] };
        var notes = new List<string>();
        var links = new ModLinks();
        foreach (var m in g.Mods)
        {
            if (m.Id is "unrealengine" or "unityengine")
            {
                e.Generic = m.Id == "unrealengine" ? "unreal" : "unity";
                if (m.Notes.Count > 0 && e.Generic == "unreal") notes.AddRange(m.Notes);
                continue;
            }
            e.Listing = m.Category switch { "related" => "related", "engine" => "engine", _ => "main" };
            e.Status ??= m.Status;
            e.Compat ??= m.Compatibility is "working" or "in-progress" ? m.Compatibility : null;
            foreach (var a in m.Maintainers) if (!e.Authors.Contains(a, StringComparer.OrdinalIgnoreCase)) e.Authors.Add(a);
            foreach (var n in m.Notes) if (!notes.Contains(n)) notes.Add(n);
            foreach (var (kind, url) in m.Urls) AddLink(links, kind, url);
            foreach (var a in m.Artifacts)
                if (!e.Files.Any(f => f.Url == a.Url))
                    e.Files.Add(new FileEntry { Name = a.Name, Arch = a.Arch, Url = a.Url, Mod = a.Mod });
        }
        if (notes.Count > 0) e.Notes = string.Join("\n", notes);
        if (!links.IsEmpty) e.Links = links;
        return e;
    }

    private static void Register(ModEntry e, Dictionary<string, ModEntry> byFile, Dictionary<string, ModEntry> byName)
    {
        foreach (var f in e.Files) byFile.TryAdd(f.Name, e);
        foreach (var n in e.Aliases.Prepend(e.Name))
            if (Names.Normalize(n) is { Length: > 0 } key) byName.TryAdd(key, e);
    }

    private static ModEntry? FindEntry(WikiRow row, Dictionary<string, ModEntry> byFile, Dictionary<string, ModEntry> byName)
    {
        foreach (var l in row.Links.Where(l => l.IsFile))
            if (byFile.TryGetValue(FileName(l.Url), out var byFileHit)) return byFileHit;
        return byName.TryGetValue(Names.Normalize(row.Name), out var hit) ? hit : null;
    }

    private static void Merge(ModEntry e, WikiRow row)
    {
        if (row.Maintainer is not null && !e.Authors.Contains(row.Maintainer, StringComparer.OrdinalIgnoreCase)) e.Authors.Add(row.Maintainer);
        if (row.Status is not null) e.Compat = row.Status;
        var note = string.Join("\n", new[] { row.StatusNote, row.Notes }.Where(n => !string.IsNullOrEmpty(n)));
        if (note.Length > 0 && !(e.Notes ?? "").Contains(note)) e.Notes = e.Notes is null ? note : e.Notes + "\n" + note;

        var links = e.Links ?? new ModLinks();
        if (row.NameUrl is not null) AddLink(links, "discussion", row.NameUrl);
        foreach (var l in row.Links.Where(l => !l.IsFile)) AddLink(links, l.Kind, l.Url);
        e.Links = links.IsEmpty ? null : links;

        // Deprecated mods are listed but not downloaded; the index's own files (if it has any) stay.
        if (row.Table == WikiTable.Deprecated) return;
        foreach (var l in row.Links.Where(l => l.IsFile))
        {
            var name = FileName(l.Url);
            if (e.Files.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            e.Files.Add(new FileEntry { Name = name, Arch = name.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase) ? "x86" : "x64", Url = l.Url });
        }
    }

    private static void AddLink(ModLinks links, string kind, string url)
    {
        switch (kind)
        {
            case "nexus" or "nexus_mods": links.Nexus ??= url; break;
            case "discord": links.Discord ??= url; break;
            case "discussion": links.Discussion ??= url; break;
            case "gamebanana": links.GameBanana ??= url; break;
            case "steam_store" or "epic_store" or "xbox_store" or "snapshot": break;
            default: if (!links.Other.Contains(url)) links.Other.Add(url); break;
        }
    }

    private static EngineGame EngineGameOf(WikiRow r, List<string> warnings)
    {
        var game = new EngineGame { Engine = EngineOf(r.Table), Name = r.Name, Compat = r.Status, Notes = r.Notes ?? r.StatusNote };
        if (r.Table != WikiTable.UeExtended) return game;
        var info = UeExtendedNotes.Parse(game.Notes);
        game.Method = info.Method;
        game.Upgrades = info.Upgrades.Count > 0 ? info.Upgrades : null;
        game.CopyDestinations = info.CopyDestinations;
        game.EngineIni = info.EngineIni.Count > 0 ? info.EngineIni : null;
        warnings.AddRange(info.Warnings.Select(w => $"{r.Name}: {w}"));
        return game;
    }

    private static string ListingOf(WikiTable t) => t switch { WikiTable.Related => "related", WikiTable.Deprecated => "deprecated", _ => "main" };

    private static string EngineOf(WikiTable t) => t switch
    {
        WikiTable.UeExtended => "ue-extended", WikiTable.Unreal => "unreal", WikiTable.Unity => "unity", _ => "source",
    };

    private static string FileName(string url) => url[(url.LastIndexOf('/') + 1)..];

    private static string UniqueId(string slug, HashSet<string> used)
    {
        var id = slug.Length > 0 ? slug : "mod";
        for (var n = 2; !used.Add(id); n++) id = $"{slug}-{n}";
        return id;
    }

    private static void ApplyOverrides(RenodxSection section, OverrideBlock o, List<string> warnings)
    {
        var byId = section.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var fix in o.NameFixes)
        {
            if (!byId.TryGetValue(fix.Id, out var m)) { warnings.Add($"overrides: nameFixes has no entry '{fix.Id}'"); continue; }
            if (!string.IsNullOrWhiteSpace(fix.Name) && fix.Name != m.Name) { m.Aliases.Add(m.Name); m.Name = fix.Name; }
            m.Aliases.AddRange(fix.Aliases);
        }
        foreach (var fix in o.AppIdFixes)
        {
            if (!byId.TryGetValue(fix.Id, out var m)) { warnings.Add($"overrides: appIdFixes has no entry '{fix.Id}'"); continue; }
            m.SteamAppIds = [.. fix.SteamAppIds.Distinct()];
        }
        foreach (var block in o.Blocklist)
        {
            var hits = section.Mods.Where(m => block.Id is not null
                ? m.Id.Equals(block.Id, StringComparison.OrdinalIgnoreCase)
                : block.Name is not null && Names.Normalize(m.Name) == Names.Normalize(block.Name)).ToList();
            if (hits.Count == 0) warnings.Add($"overrides: blocklist matches nothing ({block.Id ?? block.Name})");
            foreach (var m in hits)
            {
                m.Blocked = true;
                m.BlockReason = block.Reason;
                m.Files = [];
            }
        }
    }
}
