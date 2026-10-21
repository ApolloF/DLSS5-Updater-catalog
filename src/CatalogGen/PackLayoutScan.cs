using System.IO.Compression;

namespace CatalogGen;

/// <summary>
/// Finds the Shaders and Textures folders of a shader pack's zip. GitHub archives wrap everything in a "repo-sha/" folder,
/// release zips may not; the folder names are matched without case and without stray spaces. The folder with the most
/// effect files wins; Textures is the sibling of that Shaders folder.
/// </summary>
public static class PackLayoutScan
{
    private const int MaxDepth = 3;

    public static PackLayout AnalyseZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return Analyse(zip.Entries.Select(e => e.FullName));
    }

    /// <exception cref="InvalidDataException">No Shaders folder with an effect file in it.</exception>
    public static PackLayout Analyse(IEnumerable<string> entries)
    {
        var files = entries.Select(e => e.Replace('\\', '/').TrimStart('/')).Where(e => e.Length > 0 && !e.EndsWith('/')).ToList();
        if (files.Any(f => f.Split('/').Any(p => p is ".." or ".")))
            throw new InvalidDataException("the zip has entries that climb out of its folder");

        var candidates = files
            .Select(f => DirsOf(f).Where(d => IsNamed(d, "Shaders")))
            .SelectMany(d => d)
            .Distinct(StringComparer.Ordinal)
            .Where(d => d.Split('/').Length <= MaxDepth)
            .Select(d => (Dir: d, Fx: files.Count(f => f.StartsWith(d + "/", StringComparison.Ordinal) && IsEffect(f))))
            .Where(c => c.Fx > 0)
            .OrderByDescending(c => c.Fx).ThenBy(c => c.Dir.Split('/').Length).ThenBy(c => c.Dir, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0) throw new InvalidDataException("no Shaders folder with effect files (.fx) in the zip");

        var shaders = candidates[0].Dir;
        var parent = shaders.Contains('/') ? shaders[..shaders.LastIndexOf('/')] : "";
        var textures = files.SelectMany(f => DirsOf(f)).Distinct(StringComparer.Ordinal)
            .Where(d => IsNamed(d, "Textures") && ParentOf(d) == parent)
            .OrderBy(d => d, StringComparer.Ordinal).FirstOrDefault();

        return new PackLayout
        {
            ShadersDir = shaders + "/",
            TexturesDir = textures is null ? null : textures + "/",
            ShaderFiles = files.Count(f => f.StartsWith(shaders + "/", StringComparison.Ordinal)),
            TextureFiles = textures is null ? 0 : files.Count(f => f.StartsWith(textures + "/", StringComparison.Ordinal)),
        };
    }

    private static IEnumerable<string> DirsOf(string file)
    {
        var parts = file.Split('/');
        for (var i = 1; i < parts.Length; i++) yield return string.Join('/', parts[..i]);
    }

    private static string ParentOf(string dir) => dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : "";

    private static bool IsNamed(string dir, string name) =>
        dir[(dir.LastIndexOf('/') + 1)..].Trim().Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool IsEffect(string file) => file.EndsWith(".fx", StringComparison.OrdinalIgnoreCase);
}
