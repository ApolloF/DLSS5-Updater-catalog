using System.Text.RegularExpressions;

namespace CatalogGen;

/// <summary>One numbered section of crosire's Addons.ini: a package ReShade's own setup offers.</summary>
public sealed record AddonSource(string Number, string PackageName, string Description, string? Url, string? Url32, string? Url64,
    string? Repository, string? EffectInstallPath);

/// <summary>
/// Reads crosire's list (https://raw.githubusercontent.com/crosire/reshade-shaders/list/Addons.ini): numbered sections
/// <c>[00]</c> with PackageName, PackageDescription, DownloadUrl / DownloadUrl32 / DownloadUrl64, RepositoryUrl and
/// EffectInstallPath. A commented-out section (every line starts with '#') is not part of the list.
/// </summary>
public static partial class AddonsIni
{
    public const string Url = "https://raw.githubusercontent.com/crosire/reshade-shaders/list/Addons.ini";

    public static List<AddonSource> Parse(string text)
    {
        var result = new List<AddonSource>();
        string? number = null;
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (number is not null && keys.TryGetValue("PackageName", out var name) && name.Length > 0)
                result.Add(new AddonSource(number, name, keys.GetValueOrDefault("PackageDescription", ""),
                    Get("DownloadUrl"), Get("DownloadUrl32"), Get("DownloadUrl64"), Get("RepositoryUrl"), Get("EffectInstallPath")));
            number = null;
            keys.Clear();
        }

        string? Get(string key) => keys.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (SectionHeader().Match(line) is { Success: true } m)
            {
                Flush();
                number = m.Groups[1].Value;
                continue;
            }
            var eq = line.IndexOf('=');
            if (number is null || eq <= 0) continue;
            keys[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        Flush();
        return result;
    }

    [GeneratedRegex(@"^\[(\d+)\]$")]
    private static partial Regex SectionHeader();
}
