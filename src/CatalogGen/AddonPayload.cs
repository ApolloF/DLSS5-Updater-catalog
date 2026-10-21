using System.IO.Compression;

namespace CatalogGen;

/// <summary>
/// What an add-on download holds that the app installs. An add-on binary (.addon32 / .addon64 / .addon) goes to the add-on
/// folder; effect files (.fx / .fxh) go below the game's Shaders folder. Documentation files are ignored. Anything else
/// (shader dumps, a launcher, data the add-on reads) is something the app doesn't place, so the package is then marked
/// manual instead of being installed half way.
/// </summary>
public static class AddonPayload
{
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase) { ".md", ".txt", ".rtf", ".pdf", ".html", ".url" };
    private const string ShaderRoot = "reshade-shaders/Shaders";

    public sealed record Result(List<PayloadItem> Items, List<string> Unplaced);

    /// <summary>The payload of a plain binary download (not a zip).</summary>
    public static PayloadItem? ForBinary(string fileName, string declaredArch) => AddonArch(fileName, declaredArch) is { } arch
        ? new PayloadItem(fileName, "addon", arch, null)
        : null;

    /// <param name="entries">Zip entry names. Directories end with '/'.</param>
    /// <param name="effectInstallPath">The list's EffectInstallPath, e.g. <c>.\reshade-shaders\Shaders\REST</c>.</param>
    /// <param name="declaredArch">"x64", "x86" or "both": which build the list says this download is (decides a bare .addon).</param>
    public static Result Analyse(IEnumerable<string> entries, string? effectInstallPath, string declaredArch)
    {
        var items = new List<PayloadItem>();
        var unplaced = new List<string>();
        var installBase = BaseBelowShaders(effectInstallPath);

        foreach (var raw in entries)
        {
            var name = raw.Replace('\\', '/').TrimStart('/');
            if (name.Length == 0 || name.EndsWith('/')) continue;
            if (name.Split('/').Any(p => p is ".." or "." || p.Contains(':')))
            {
                unplaced.Add(name);
                continue;
            }
            var file = name[(name.LastIndexOf('/') + 1)..];
            var ext = Path.GetExtension(file);

            if (AddonArch(file, declaredArch) is { } arch) items.Add(new PayloadItem(name, "addon", arch, null));
            else if (ext.Equals(".fx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".fxh", StringComparison.OrdinalIgnoreCase))
            {
                if (EffectDest(name, file, installBase) is { } dest) items.Add(new PayloadItem(name, "effect", null, dest));
                else unplaced.Add(name);
            }
            else if (!IsDocument(file, ext)) unplaced.Add(name);
        }
        return new Result(items, unplaced);
    }

    public static Result AnalyseZip(string path, string? effectInstallPath, string declaredArch)
    {
        using var zip = ZipFile.OpenRead(path);
        return Analyse(zip.Entries.Select(e => e.FullName), effectInstallPath, declaredArch);
    }

    private static string? AddonArch(string file, string declaredArch)
    {
        var ext = Path.GetExtension(file);
        if (ext.Equals(".addon32", StringComparison.OrdinalIgnoreCase)) return "x86";
        if (ext.Equals(".addon64", StringComparison.OrdinalIgnoreCase)) return "x64";
        if (ext.Equals(".addon", StringComparison.OrdinalIgnoreCase)) return declaredArch;
        return null;
    }

    private static bool IsDocument(string file, string ext) =>
        Documents.Contains(ext) || file.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
        || file.StartsWith("README", StringComparison.OrdinalIgnoreCase) || file.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder below reshade-shaders\Shaders the list puts effects in ("REST"), "" for the Shaders folder itself, null when it is elsewhere.</summary>
    private static string? BaseBelowShaders(string? effectInstallPath)
    {
        if (string.IsNullOrWhiteSpace(effectInstallPath)) return null;
        var p = effectInstallPath.Replace('\\', '/').Trim();
        while (p.StartsWith("./")) p = p[2..];
        p = p.Trim('/');
        if (p.Equals(ShaderRoot, StringComparison.OrdinalIgnoreCase)) return "";
        return p.StartsWith(ShaderRoot + "/", StringComparison.OrdinalIgnoreCase) ? p[(ShaderRoot.Length + 1)..] : null;
    }

    /// <summary>Where an effect file goes below the Shaders folder: after a "Shaders" folder in the zip if there is one, else flat in the install path.</summary>
    private static string? EffectDest(string name, string file, string? installBase)
    {
        var parts = name.Split('/');
        var shaders = Array.FindIndex(parts, p => p.Equals("Shaders", StringComparison.OrdinalIgnoreCase));
        if (shaders >= 0 && shaders < parts.Length - 1)
        {
            var below = string.Join('/', parts[(shaders + 1)..]);
            return string.IsNullOrEmpty(installBase) ? below : installBase + "/" + below;
        }
        if (installBase is null) return null;
        return string.IsNullOrEmpty(installBase) ? file : installBase + "/" + file;
    }
}
