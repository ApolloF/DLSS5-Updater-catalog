using System.Text.Json;

namespace CatalogGen;

public static class Program
{
    private const string Usage = """
        catalog-gen: builds catalog.json

          --catalog <file>     catalog.json to read and update (default catalog.json)
          --overrides <file>   hand-maintained overrides (default overrides.json)
          --temp <dir>         scratch folder for downloads (default: system temp)
          --repo <owner/repo>  repository that hosts the mirror assets (default GITHUB_REPOSITORY)
          --no-mirror          hash everything but upload nothing

        Environment: GITHUB_TOKEN (API calls and mirror uploads), CATALOG_ACCEPT_SHRINK=1.
        Exit code 0 = catalog current, 1 = a source broke (the last good section is kept).
        """;

    public static async Task<int> Main(string[] args)
    {
        string catalogPath = "catalog.json", overridesPath = "overrides.json";
        string tempDir = Path.Combine(Path.GetTempPath(), "catalog-gen");
        var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var mirrorOn = true;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--catalog" when i + 1 < args.Length: catalogPath = args[++i]; break;
                case "--overrides" when i + 1 < args.Length: overridesPath = args[++i]; break;
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
        var generator = new RenodxGenerator(new HttpText(http), sync, 4, Environment.GetEnvironmentVariable("CATALOG_ACCEPT_SHRINK") == "1",
            notices: mirror is null ? null : new MirrorNoticePublisher(licences, mirror));

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

        // A broken source for one section never stops the other.
        var dlssGenerator = new RenodxDlssGenerator(new GitHubText(http, token), sync);
        try
        {
            var output = await dlssGenerator.BuildAsync(CatalogFile.ReadSection<RenodxDlssSection>(root, "renodxDlss"), now, cts.Token);
            failed |= Report(output.Warnings, output.Errors);
            var changed = CatalogFile.SetSection(root, "renodxDlss", output.Section, now);
            Console.WriteLine(changed ? "renodxDlss section changed" : "renodxDlss section unchanged");
        }
        catch (SourceException ex)
        {
            Console.WriteLine($"::error::renodxDlss: {ex.Message}. Keeping the last good section.");
            failed = true;
        }

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

        var written = CatalogFile.Save(catalogPath, root);
        Console.WriteLine(written ? $"wrote {catalogPath}" : $"{catalogPath} is already current");
        return failed ? 1 : 0;
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
