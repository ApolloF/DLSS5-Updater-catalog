using System.Text.Json;

namespace CatalogGen;

public static class Program
{
    private const string Usage = """
        catalog-gen: builds catalog.json

          --catalog <file>     catalog.json to read and update (default catalog.json)
          --overrides <file>   hand-maintained overrides (default overrides.json)
          --packs <file>       hand-maintained shader pack list (default shader-packs.json)
          --temp <dir>         scratch folder for downloads (default: system temp)
          --repo <owner/repo>  repository that hosts the mirror assets (default GITHUB_REPOSITORY)
          --no-mirror          hash everything but upload nothing

        Environment: GITHUB_TOKEN (API calls and mirror uploads), CATALOG_ACCEPT_SHRINK=1.
        Exit code 0 = catalog current, 1 = a source broke (the last good section is kept).
        """;

    public static async Task<int> Main(string[] args)
    {
        string catalogPath = "catalog.json", overridesPath = "overrides.json", packsPath = "shader-packs.json";
        string tempDir = Path.Combine(Path.GetTempPath(), "catalog-gen");
        var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var mirrorOn = true;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--catalog" when i + 1 < args.Length: catalogPath = args[++i]; break;
                case "--overrides" when i + 1 < args.Length: overridesPath = args[++i]; break;
                case "--packs" when i + 1 < args.Length: packsPath = args[++i]; break;
                case "--temp" when i + 1 < args.Length: tempDir = args[++i]; break;
                case "--repo" when i + 1 < args.Length: repo = args[++i]; break;
                case "--no-mirror": mirrorOn = false; break;
                default: Console.Error.WriteLine(Usage); return 2;
            }
        }

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        OverrideBlock overrides;
        try { overrides = LoadOverrides(overridesPath); }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.Error.WriteLine($"::error::{overridesPath}: {ex.Message}");
            return 1;
        }

        var root = CatalogFile.Load(catalogPath);
        var previous = CatalogFile.ReadSection<RenodxSection>(root, "renodx");
        var now = DateTimeOffset.UtcNow;

        using var http = UpstreamFetcher.CreateHttp();
        IMirror? mirror = mirrorOn && !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(repo)
            ? new GitHubMirror(http, repo!, token!) : null;
        if (mirror is null) Console.WriteLine("Mirroring is off (no token, no repository, or --no-mirror): files are hashed and pinned only.");

        var licences = new GitHubLicences(http, token);
        var sync = new FileSync(new UpstreamFetcher(http, tempDir), licences, mirror);
        var acceptShrink = Environment.GetEnvironmentVariable("CATALOG_ACCEPT_SHRINK") == "1";
        var generator = new RenodxGenerator(new HttpText(http), sync, 4, acceptShrink);

        var failed = false;
        try
        {
            var output = await generator.BuildAsync(previous, overrides, now, cts.Token);
            failed |= Report(output.Warnings, output.Errors);
            var changed = CatalogFile.SetSection(root, "renodx", output.Section, now);
            Console.WriteLine(changed ? "renodx section changed" : "renodx section unchanged");
        }
        catch (SourceException ex)
        {
            Console.WriteLine($"::error::renodx: {ex.Message}. Keeping the last good section.");
            failed = true;
        }

        // Files the owner dropped into the "incoming" release; the sections below mirror them, the files are removed once the catalog is written.
        var drops = new List<Drop>();
        IIncoming? incoming = mirror is not null ? new GitHubIncoming(http, repo!, token!) : null;
        if (incoming is not null)
        {
            var dropWarnings = new List<string>();
            try { drops = await IncomingDrops.ReadAsync(incoming, new UpstreamFetcher(http, tempDir, requiredZipEntry: null), dropWarnings, cts.Token); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException && !cts.IsCancellationRequested)
            {
                dropWarnings.Add($"incoming could not be read: {ex.Message}");
                failed = true;
            }
            Report(dropWarnings, []);
        }

        // A broken source for one section never stops the other. Every build of this section is mirrored, whatever the licence of its source.
        var dlssSync = new FileSync(new UpstreamFetcher(http, tempDir), licences, mirror, mirrorAnyLicence: true);
        var dlssGenerator = new RenodxDlssGenerator(new GitHubText(http, token), dlssSync, mirror);
        try
        {
            var output = await dlssGenerator.BuildAsync(CatalogFile.ReadSection<RenodxDlssSection>(root, "renodxDlss"), now, cts.Token, drops);
            failed |= Report(output.Warnings, output.Errors);
            var changed = CatalogFile.SetSection(root, "renodxDlss", output.Section, now);
            Console.WriteLine(changed ? "renodxDlss section changed" : "renodxDlss section unchanged");
        }
        catch (SourceException ex)
        {
            Console.WriteLine($"::error::renodxDlss: {ex.Message}. Keeping the last good section.");
            failed = true;
        }

        if (mirror is not null)
        {
            var neural = new NeuralGenerator(new GitHubText(http, token), new UpstreamFetcher(http, tempDir, requiredZipEntry: null), mirror, tempDir);
            var output = await neural.BuildAsync(CatalogFile.ReadSection<NeuralSection>(root, "neural"), drops, now, cts.Token);
            failed |= Report(output.Warnings, output.Errors);
            var changed = CatalogFile.SetSection(root, "neural", output.Section, now);
            Console.WriteLine(changed ? "neural section changed" : "neural section unchanged");
        }
        else Console.WriteLine("neural section kept as it is: the mirrored binaries need mirroring on.");

        var compatGenerator = new OptiScalerCompatGenerator(new HttpText(http));
        try
        {
            var output = await compatGenerator.BuildAsync(cts.Token);
            failed |= Report(output.Warnings, []);
            var changed = CatalogFile.SetSection(root, "optiscalerCompat", output.Section, now);
            Console.WriteLine(changed ? "optiscalerCompat section changed" : "optiscalerCompat section unchanged");
        }
        catch (SourceException ex)
        {
            Console.WriteLine($"::error::optiscalerCompat: {ex.Message}. Keeping the last good section.");
            failed = true;
        }

        // Zips and plain add-on binaries: any readable zip passes the download check.
        var zipSync = new FileSync(new UpstreamFetcher(http, tempDir, requiredZipEntry: null), licences, mirror);

        try
        {
            var addons = new AddonsGenerator(new HttpText(http), zipSync, 4, acceptShrink);
            var output = await addons.BuildAsync(CatalogFile.ReadSection<AddonsSection>(root, "addons"), now, cts.Token);
            failed |= Report(output.Warnings, output.Errors);
            var changed = CatalogFile.SetSection(root, "addons", output.Section, now);
            Console.WriteLine(changed ? "addons section changed" : "addons section unchanged");
        }
        catch (SourceException ex)
        {
            Console.WriteLine($"::error::addons: {ex.Message}. Keeping the last good section.");
            failed = true;
        }

        try
        {
            var limiters = new FrameLimitersGenerator(new GitHubText(http, token), zipSync);
            var output = await limiters.BuildAsync(CatalogFile.ReadSection<FrameLimitersSection>(root, "frameLimiters"), now, cts.Token);
            failed |= Report(output.Warnings, output.Errors);
            var changed = CatalogFile.SetSection(root, "frameLimiters", output.Section, now);
            Console.WriteLine(changed ? "frameLimiters section changed" : "frameLimiters section unchanged");
        }
        catch (SourceException ex)
        {
            Console.WriteLine($"::error::frameLimiters: {ex.Message}. Keeping the last good section.");
            failed = true;
        }

        try
        {
            var luma = new LumaGenerator(new HttpText(http), new GitHubText(http, token), zipSync);
            var output = await luma.BuildAsync(CatalogFile.ReadSection<LumaSection>(root, "luma"), now, cts.Token);
            failed |= Report(output.Warnings, output.Errors);
            var changed = CatalogFile.SetSection(root, "luma", output.Section, now);
            Console.WriteLine(changed ? "luma section changed" : "luma section unchanged");
        }
        catch (SourceException ex)
        {
            Console.WriteLine($"::error::luma: {ex.Message}. Keeping the last good section.");
            failed = true;
        }

        try
        {
            var list = ShaderPackLists.Load(packsPath);
            var verified = list.Packs.Where(p => p.License is not null).ToDictionary(p => p.Repo, p => p.License!, StringComparer.OrdinalIgnoreCase);
            var packSync = new FileSync(new UpstreamFetcher(http, tempDir, requiredZipEntry: null), new VerifiedLicences(licences, verified), mirror);
            var packs = new ShaderPacksGenerator(new GitHubText(http, token), packSync);
            var output = await packs.BuildAsync(list, CatalogFile.ReadSection<ShaderPacksSection>(root, "shaderPacks"), now, cts.Token);
            failed |= Report(output.Warnings, output.Errors);
            var changed = CatalogFile.SetSection(root, "shaderPacks", output.Section, now);
            Console.WriteLine(changed ? "shaderPacks section changed" : "shaderPacks section unchanged");
        }
        catch (SourceException ex)
        {
            Console.WriteLine($"::error::shaderPacks: {ex.Message}. Keeping the last good section.");
            failed = true;
        }

        // One notice pass over every mirrored file of every section: each monthly release gets the licences of all of them.
        if (mirror is not null)
        {
            var errors = new List<string>();
            await new MirrorNoticePublisher(licences, mirror).PublishAsync(MirroredFiles(root), errors, cts.Token);
            failed |= Report([], errors);
        }

        var written = CatalogFile.Save(catalogPath, root);
        Console.WriteLine(written ? $"wrote {catalogPath}" : $"{catalogPath} is already current");

        // Only now that the catalog lists them do the mirrored drop-ins leave "incoming".
        foreach (var drop in drops.Where(d => d.Handled))
        {
            try
            {
                await incoming!.DeleteAsync(drop.Asset.Id, cts.Token);
                Console.WriteLine($"incoming/{drop.Asset.Name}: mirrored and removed");
            }
            catch (HttpRequestException ex)
            {
                failed |= Report([], [$"incoming/{drop.Asset.Name}: mirrored but could not be removed: {ex.Message}"]);
            }
        }
        foreach (var drop in drops) drop.Dispose();
        return failed ? 1 : 0;
    }

    /// <summary>Every file entry of the sections that can be mirrored, as they stand in the catalog.</summary>
    public static List<FileEntry> MirroredFiles(System.Text.Json.Nodes.JsonObject root)
    {
        var files = new List<FileEntry>();
        if (CatalogFile.ReadSection<RenodxSection>(root, "renodx") is { } renodx)
            files.AddRange(renodx.Mods.SelectMany(m => m.Files).Concat(renodx.Generic.SelectMany(g => g.Files)));
        if (CatalogFile.ReadSection<AddonsSection>(root, "addons") is { } addons) files.AddRange(addons.Addons.SelectMany(a => a.Files));
        if (CatalogFile.ReadSection<FrameLimitersSection>(root, "frameLimiters") is { } limiters)
            files.AddRange(limiters.Limiters.SelectMany(l => l.Files));
        if (CatalogFile.ReadSection<LumaSection>(root, "luma") is { } luma) files.AddRange(luma.Packages.SelectMany(p => p.Files));
        if (CatalogFile.ReadSection<ShaderPacksSection>(root, "shaderPacks") is { } packs)
            files.AddRange(packs.Packs.Select(p => p.File).OfType<FileEntry>());
        return files;
    }

    /// <summary>Prints the run's warnings and errors as workflow annotations; true when there was an error.</summary>
    private static bool Report(IEnumerable<string> warnings, IReadOnlyCollection<string> errors)
    {
        foreach (var w in warnings) Console.WriteLine($"::warning::{w}");
        foreach (var e in errors) Console.WriteLine($"::error::{e}");
        return errors.Count > 0;
    }

    public static OverrideBlock LoadOverrides(string path)
    {
        if (!File.Exists(path)) return new OverrideBlock();
        var file = JsonSerializer.Deserialize<OverridesFile>(File.ReadAllText(path), Json.Options);
        return file?.Renodx ?? new OverrideBlock();
    }

    private sealed class HttpText(HttpClient http) : ITextSource
    {
        public async Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            for (var attempt = 1; ; attempt++)
            {
                try { return await http.GetStringAsync(url, ct); }
                catch (HttpRequestException) when (attempt < 3 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
                }
            }
        }
    }
}
