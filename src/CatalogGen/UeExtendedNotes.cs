using System.Text.RegularExpressions;

namespace CatalogGen;

public sealed record UpgradeFormat(string Format, string Mode);

public sealed record IniSetting(string Section, string Key, string Value);

/// <summary>What the wiki's free-text note for an Unreal Extended game says, as data. Everything is optional: the note stays the source of truth.</summary>
public sealed record UeExtendedInfo(
    string? Method, List<UpgradeFormat> Upgrades, bool? CopyDestinations, List<IniSetting> EngineIni, List<string> Warnings);

/// <summary>Reads the Unreal Extended notes ("Upgrade Path: On, B8G8R8A8_TYPELESS Output Size", "Engine.ini", "Native HDR", ...).</summary>
public static partial class UeExtendedNotes
{
    /// <summary>The resource formats the add-on's upgrade list knows; anything else in a note is a typo or a new format and is reported, not passed on.</summary>
    public static readonly IReadOnlySet<string> Formats = new HashSet<string>(StringComparer.Ordinal)
    {
        "B10G10R10A2_UNORM", "B8G8R8A8_TYPELESS", "B8G8R8A8_UNORM", "B8G8R8A8_UNORM_SRGB",
        "R10G10B10A2_TYPELESS", "R10G10B10A2_UNORM", "R11G11B10_FLOAT", "R16G16B16A16_TYPELESS",
        "R8G8B8A8_SNORM", "R8G8B8A8_TYPELESS", "R8G8B8A8_UNORM", "R8G8B8A8_UNORM_SRGB",
    };

    private const string DefaultIniSection = "SystemSettings";

    public static UeExtendedInfo Parse(string? notes)
    {
        var text = notes ?? "";
        var warnings = new List<string>();
        var upgrades = new List<UpgradeFormat>();
        foreach (Match m in UpgradeRule().Matches(text))
        {
            var format = m.Groups["format"].Value;
            if (!Formats.Contains(format))
            {
                warnings.Add($"unknown resource format '{format}' in the Unreal Extended note");
                continue;
            }
            var upgrade = new UpgradeFormat(format, ModeOf(m.Groups["mode"].Value));
            if (!upgrades.Contains(upgrade)) upgrades.Add(upgrade);
        }

        var ini = new List<IniSetting>();
        foreach (Match m in IniRule().Matches(text))
        {
            var setting = new IniSetting(DefaultIniSection, m.Groups["key"].Value, m.Groups["value"].Value.TrimEnd('.'));
            if (!ini.Contains(setting)) ini.Add(setting);
        }

        return new UeExtendedInfo(MethodOf(text), upgrades, CopyOf(text), ini, warnings);
    }

    private static string? MethodOf(string text)
    {
        if (UpgradeOn().IsMatch(text)) return "upgrade";
        if (text.Contains("Upgrade Path: Off", StringComparison.OrdinalIgnoreCase)) return "native";
        if (text.Contains("Native HDR", StringComparison.OrdinalIgnoreCase)) return "native";
        return text.Contains("Engine.ini", StringComparison.OrdinalIgnoreCase) ? "ini" : null;
    }

    private static bool? CopyOf(string text) =>
        CopyRule().Match(text) is { Success: true } m ? m.Groups[1].Value.Equals("Off", StringComparison.OrdinalIgnoreCase) ? false : true : null;

    private static string ModeOf(string mode) => Regex.Replace(mode.ToLowerInvariant(), @"\s+", "-");

    [GeneratedRegex(@"Upgrade Path:\s*On\b", RegexOptions.IgnoreCase)]
    private static partial Regex UpgradeOn();

    [GeneratedRegex(@"Upgrade Copy Destinations\s+(On|Off)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CopyRule();

    // Format names are upper case with an underscore; the mode words are matched case-insensitively.
    [GeneratedRegex(@"\b(?<format>[A-Z][A-Z0-9]*_[A-Z0-9_]+)\s+(?<mode>(?i:Output\s+Size|Output\s+Ratio|Any\s+Size))\b")]
    private static partial Regex UpgradeRule();

    [GeneratedRegex(@"\b(?<key>r\.[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*)=(?<value>[^\s,;]+)")]
    private static partial Regex IniRule();
}
