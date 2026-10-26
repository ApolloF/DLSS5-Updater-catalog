using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CatalogGen;

public sealed record NeuralOutput(NeuralSection Section, List<string> Warnings, List<string> Errors);

/// <summary>
/// Builds the <c>neural</c> section: NVIDIA's <c>nvngx_dlssnr.dll</c> and Lecram's DLSS5 add-on builds, kept in this repository's
/// own mirror. Builds come from the GitHub releases of RankFTW/rhi-repo (one zip each, the binary extracted and mirrored) and from
/// raw files in the <c>incoming</c> release, which are identified by what is inside them. A binary is known by its SHA-256, so the
/// same file arriving twice is a no-op. The newest 20 of each kind are listed.
/// </summary>
public sealed partial class NeuralGenerator(ITextSource text, IFetcher fetcher, IMirror mirror, string tempDir, int keep = 20)
{
    private sealed record Candidate(NeuralKind Kind, DateTimeOffset Published, string Tag, string AssetName, string AssetUrl);

    public async Task<NeuralOutput> BuildAsync(NeuralSection? previous, IReadOnlyList<Drop> drops, DateTimeOffset now, CancellationToken ct)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var dlssnr = All(previous?.Dlssnr);
        var lecram = All(previous?.Lecram);
        List<NeuralBuild> ListOf(NeuralKind kind) => kind == NeuralKind.DlssNr ? dlssnr : lecram;

        foreach (var candidate in await ReadReleasesAsync(errors, warnings, ct))
        {
            var known = ListOf(candidate.Kind);
            if (known.Any(b => b.Url == candidate.AssetUrl)) continue;

            var result = await fetcher.FetchAsync(candidate.AssetUrl, null, ct);
            if (result is not FetchResult.Fetched fetched)
            {
                warnings.Add($"{candidate.Tag}: skipped, {(result as FetchResult.Failed)?.Reason ?? "could not be downloaded"}");
                continue;
            }

            string? binary = null;
            try
            {
                binary = ExtractSingle(fetched.Path);
                var identified = NeuralIdentify.Identify(binary, candidate.AssetName);
                if (identified is null || identified.Kind != candidate.Kind)
                {
                    warnings.Add($"{candidate.Tag}: the zip does not hold the expected {(candidate.Kind == NeuralKind.DlssNr ? "nvngx_dlssnr.dll" : "Lecram add-on")}; skipped");
                    continue;
                }
                await IngestAsync(known, binary, identified, candidate.AssetUrl, now, errors, ct);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{candidate.Tag}: skipped, {ex.Message}");
            }
            finally
            {
                TryDelete(fetched.Path);
                if (binary is not null) TryDelete(binary);
            }
        }

        foreach (var drop in drops.Where(d => d.Identified.Kind is NeuralKind.DlssNr or NeuralKind.Lecram))
        {
            var known = ListOf(drop.Identified.Kind);
            if (await IngestAsync(known, drop.Path, drop.Identified, null, now, errors, ct, drop.Sha256, drop.Size)) drop.Handled = true;
        }

        LabelFromTags(lecram);
        await BackfillFileVersionsAsync(dlssnr.Concat(lecram), warnings, ct);

        var section = new NeuralSection { Source = RenodxDlssGenerator.ReleasesUrl, Dlssnr = Order(dlssnr, new NeuralStream(), keep), Lecram = Order(lecram, new NeuralLecram(), keep) };
        section.Lecram.Generation = section.Lecram.Current?.Generation;
        return new NeuralOutput(section, warnings, errors);
    }

    private static List<NeuralBuild> All(NeuralStream? stream) =>
        stream is null ? [] : (stream.Current is null ? [] : new List<NeuralBuild> { stream.Current }).Concat(stream.History).ToList();

    /// <summary>Hashes and mirrors <paramref name="path"/> unless a build with that hash is known. False when the mirror upload failed.</summary>
    private async Task<bool> IngestAsync(List<NeuralBuild> known, string path, Identified identified, string? url, DateTimeOffset now,
        List<string> errors, CancellationToken ct, string? sha256 = null, long? size = null)
    {
        sha256 ??= await HashAsync(path, ct);
        size ??= new FileInfo(path).Length;
        if (known.FirstOrDefault(b => b.Sha256 == sha256) is { } same)
        {
            same.Url ??= url;
            same.FileVersion ??= identified.FileVersion;
            return true;
        }

        var firstSeen = FileSync.Iso(now);
        try
        {
            var mirrorUrl = await mirror.PublishAsync(identified.FileName, sha256, firstSeen[..7], path, ct);
            known.Add(new NeuralBuild
            {
                Name = identified.FileName,
                Version = identified.Version ?? FileSync.VersionOf(now, sha256),
                FileVersion = identified.FileVersion,
                Sha256 = sha256,
                Size = size.Value,
                FirstSeen = firstSeen,
                MirrorUrl = mirrorUrl,
                Generation = identified.Generation,
                Url = url,
            });
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            errors.Add($"mirroring {identified.FileName} ({sha256[..7]}) failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// A Lecram build taken from an rhi-repo release is labelled by its tag. Older v10 binaries hold no label of their own, only the
    /// DLSS SDK's v310.8.0, which outranked every real build; entries listed with that label are corrected here.
    /// </summary>
    private static void LabelFromTags(IEnumerable<NeuralBuild> builds)
    {
        foreach (var build in builds)
            if (build.Url is not null && ReleaseDownload().Match(build.Url) is { Success: true } m && TagVersion(m.Groups[1].Value) is { } version)
                build.Version = version;
    }

    /// <summary>"renodx-dlss5-7.0.0-rc8" -> "v7.0.0-rc8"; null for a tag that names no version.</summary>
    internal static string? TagVersion(string tag) => LecramTag().Match(tag) is { Success: true } m ? "v" + m.Groups[1].Value : null;

    private static T Order<T>(List<NeuralBuild> builds, T stream, int keep) where T : NeuralStream
    {
        var sorted = builds
            .OrderByDescending(b => b.Generation, StringComparer.Ordinal)
            // A hand-dropped Lecram build has no tag; when its only label is the SDK's, it ranks below the labelled builds of its generation.
            .ThenBy(b => b.Generation is not null && SdkLabel().IsMatch(b.Version))
            .ThenBy(b => b, Comparer<NeuralBuild>.Create((a, b) => CompareVersions(b.Version, a.Version)))
            .ThenBy(b => b, Comparer<NeuralBuild>.Create((a, b) => CompareFileVersions(b.FileVersion, a.FileVersion)))
            .ThenByDescending(b => b.FirstSeen, StringComparer.Ordinal)
            .ThenBy(b => b.Sha256, StringComparer.Ordinal)
            .Take(keep)
            .ToList();
        stream.Current = sorted.FirstOrDefault();
        stream.History = sorted.Skip(1).ToList();
        return stream;
    }

    /// <summary>Numeric per dotted part; a build without a file version ranks below every build that has one.</summary>
    internal static int CompareFileVersions(string? a, string? b) =>
        a is null || b is null ? (a is null ? 0 : 1) - (b is null ? 0 : 1) : CompareVersions(a, b);

    /// <summary>
    /// Builds listed before <c>fileVersion</c> existed get it from their mirror copy (nvngx_dlssnr.dll from its version; the rest checked against the listed size and SHA-256).
    /// At most <see cref="BackfillLimit"/> files are fetched per run; a failure leaves the entry as it is.
    /// </summary>
    private async Task BackfillFileVersionsAsync(IEnumerable<NeuralBuild> builds, List<string> warnings, CancellationToken ct)
    {
        var budget = BackfillLimit;
        foreach (var build in builds.Where(b => b.FileVersion is null && b.MirrorUrl.Length > 0))
        {
            // nvngx_dlssnr.dll is large and its version is already its file version.
            if (build.Name == NeuralIdentify.DlssNrFile && DottedDigits().IsMatch(build.Version)) { build.FileVersion = build.Version; continue; }
            if (budget-- <= 0) break;
            var label = $"{build.Name} ({build.Sha256[..7]})";
            var result = await fetcher.FetchAsync(build.MirrorUrl, null, ct);
            if (result is not FetchResult.Fetched fetched)
            {
                warnings.Add($"{label}: file version not read, {(result as FetchResult.Failed)?.Reason ?? "could not be downloaded"}");
                continue;
            }
            try
            {
                if (fetched.Sha256 != build.Sha256 || fetched.Size != build.Size)
                    warnings.Add($"{label}: file version not read, the mirror copy does not match the listed size and SHA-256");
                else build.FileVersion = NeuralIdentify.FileVersionOf(fetched.Path);
            }
            finally { TryDelete(fetched.Path); }
        }
    }

    internal const int BackfillLimit = 10;

    /// <summary>Compares 310.8.3 with 310.8.0, and v8.5.0-rc10 with v8.5.0-rc5 and v8.5.0 (a release beats its pre-releases).</summary>
    internal static int CompareVersions(string a, string b)
    {
        var (coreA, suffixA) = Split(a);
        var (coreB, suffixB) = Split(b);
        for (var i = 0; i < Math.Max(coreA.Count, coreB.Count); i++)
        {
            var c = (i < coreA.Count ? coreA[i] : 0).CompareTo(i < coreB.Count ? coreB[i] : 0);
            if (c != 0) return c;
        }
        if (suffixA.Length == 0 || suffixB.Length == 0) return suffixA.Length == suffixB.Length ? 0 : suffixA.Length == 0 ? 1 : -1;
        var partsA = Tokens().Matches(suffixA).Select(m => m.Value).ToList();
        var partsB = Tokens().Matches(suffixB).Select(m => m.Value).ToList();
        for (var i = 0; i < Math.Min(partsA.Count, partsB.Count); i++)
        {
            var c = long.TryParse(partsA[i], out var x) && long.TryParse(partsB[i], out var y) ? x.CompareTo(y) : string.CompareOrdinal(partsA[i], partsB[i]);
            if (c != 0) return c;
        }
        return partsA.Count.CompareTo(partsB.Count);
    }

    private static (List<long> Core, string Suffix) Split(string version)
    {
        var m = CoreRule().Match(version);
        return (m.Groups[1].Value.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(s => long.TryParse(s, out var n) ? n : 0).ToList(), version[m.Length..]);
    }

    private async Task<List<Candidate>> ReadReleasesAsync(List<string> errors, List<string> warnings, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(await text.GetStringAsync(RenodxDlssGenerator.ReleasesUrl, ct)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            errors.Add($"rhi-repo releases: {ex.Message}; the neural builds already listed are kept");
            return [];
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add("rhi-repo releases: not a list; the neural builds already listed are kept");
                return [];
            }
            var found = new List<Candidate>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Flag(item, "draft") || Flag(item, "prerelease")) continue;
                var tag = Str(item, "tag_name");
                var kind = tag is null ? null : KindOf(tag);
                if (kind is null) continue;

                var zips = item.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
                    ? assets.EnumerateArray().Where(a => Str(a, "name")?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true).ToList()
                    : [];
                if (zips.Count != 1) { warnings.Add($"{tag}: expected one zip asset, found {zips.Count}; skipped"); continue; }
                if (!DateTimeOffset.TryParse(Str(item, "published_at"), out var published)) { warnings.Add($"{tag}: no publish date; skipped"); continue; }
                var name = Str(zips[0], "name");
                var url = Str(zips[0], "browser_download_url");
                if (name is null || url is null) { warnings.Add($"{tag}: the zip asset has no download url; skipped"); continue; }
                found.Add(new Candidate(kind.Value, published, tag!, name, url));
            }
            return found
                .GroupBy(c => c.Kind)
                .SelectMany(g => g.OrderByDescending(c => c.Published).ThenBy(c => c.Tag, StringComparer.Ordinal).Take(keep))
                .ToList();
        }
    }

    private static NeuralKind? KindOf(string tag) =>
        tag.StartsWith("dlssnr-", StringComparison.Ordinal) ? NeuralKind.DlssNr
        : tag.StartsWith("renodx-dlss5-", StringComparison.Ordinal) || tag.StartsWith("dlss5-nr-", StringComparison.Ordinal) ? NeuralKind.Lecram
        : null;

    /// <summary>The zip's only file, extracted next to the download. Anything else (no file, several files) is not a build.</summary>
    private string ExtractSingle(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var files = zip.Entries.Where(e => e.Name.Length > 0).ToList();
        if (files.Count != 1) throw new InvalidDataException($"the zip holds {files.Count} files, expected one");
        Directory.CreateDirectory(tempDir);
        var target = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".bin");
        // Copied by hand: ExtractToFile applies the unix permissions stored in the zip, which can leave an unreadable file.
        using (var source = files[0].Open())
        using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            source.CopyTo(destination);
        return target;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"^v?(\d+(?:\.\d+)*)")]
    private static partial Regex CoreRule();

    [GeneratedRegex(@"^(?:renodx-dlss5|dlss5-nr)-v?(\d+(?:\.\d+)*(?:-[0-9A-Za-z.]+)?)$")]
    private static partial Regex LecramTag();

    [GeneratedRegex(@"^v\d{3,}\.")]
    private static partial Regex SdkLabel();

    [GeneratedRegex(@"^https://github\.com/RankFTW/rhi-repo/releases/download/([^/]+)/")]
    private static partial Regex ReleaseDownload();

    [GeneratedRegex(@"^\d+(\.\d+)+$")]
    private static partial Regex DottedDigits();

    [GeneratedRegex(@"\d+|\D+")]
    private static partial Regex Tokens();
}
