using System.Text;
using System.Text.RegularExpressions;

namespace CatalogGen;

public enum NeuralKind { DlssNr, Lecram, RenodxDlss }

/// <param name="FileName">The name the mirrored file carries.</param>
/// <param name="Generation">Lecram only: "v10" (v10 and older, renodx-dlss5.addon64) or "v11" (dlss5-nr.addon64).</param>
public sealed record Identified(NeuralKind Kind, string FileName, string? Version, string? Generation);

/// <summary>Tells which of the mirrored binaries a PE file is, from what is inside it rather than from how it was named.</summary>
public static partial class NeuralIdentify
{
    public const string DlssNrFile = "nvngx_dlssnr.dll";
    public const string Lecram10File = "renodx-dlss5.addon64";
    public const string Lecram11File = "dlss5-nr.addon64";
    public const string RenodxFile = "renodx-dlss.addon64";

    /// <summary>Null when the file is none of them. <paramref name="name"/> is the file's name as it was dropped or listed.</summary>
    public static Identified? Identify(string path, string name)
    {
        var head = new byte[2];
        using (var fs = File.OpenRead(path))
            if (fs.Read(head) < 2 || head[0] != 'M' || head[1] != 'Z') return null;

        var pe = PeInfo.ReadVersion(path);
        var original = pe?.Get("OriginalFilename");
        if (pe?.Get("InternalName")?.Equals("DLSSNR", StringComparison.OrdinalIgnoreCase) == true
            || Is(original, DlssNrFile) || Is(name, DlssNrFile))
            return new Identified(NeuralKind.DlssNr, DlssNrFile, pe?.FileVersion ?? Digits(pe?.Get("FileVersion")), null);

        if (Is(original, RenodxFile) || (original is null && name.StartsWith("renodx-dlss", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("renodx-dlss5", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase)))
            return new Identified(NeuralKind.RenodxDlss, RenodxFile, null, null);

        if (Is(original, Lecram10File)) return Lecram(path, Lecram10File, "v10", pe);
        if ((original is null || Is(original, Lecram11File)) && LooksLikeLecramV11(File.ReadAllBytes(path)))
            return Lecram(path, Lecram11File, "v11", pe);
        return null;
    }

    private static Identified Lecram(string path, string file, string generation, PeVersion? pe) =>
        new(NeuralKind.Lecram, file, FindBuildLabel(File.ReadAllBytes(path)) ?? pe?.FileVersion ?? (generation == "v11" ? "v11" : null), generation);

    private static bool Is(string? value, string expected) => value?.Equals(expected, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>"310,8,3,0" -> "310.8.3".</summary>
    private static string? Digits(string? raw)
    {
        if (raw is null) return null;
        var parts = DigitRun().Matches(raw).Select(m => m.Value).ToList();
        if (parts.Count == 4 && parts[3] == "0") parts.RemoveAt(3);
        return parts.Count == 0 ? null : string.Join('.', parts);
    }

    /// <summary>
    /// True for a PE file with the strings only Lecram's v11 binary has: the ini section it reads, and its NR-SETTINGS and
    /// NR-COMPAT log lines. v10 builds have the section but neither log line. (Same check as the app's LecramSource.)
    /// </summary>
    internal static bool LooksLikeLecramV11(byte[] data) =>
        data.Length > 0x40 && data[0] == 'M' && data[1] == 'Z'
        && data.AsSpan().IndexOf("NR-SETTINGS"u8) >= 0
        && data.AsSpan().IndexOf("NR-COMPAT"u8) >= 0
        && data.AsSpan().IndexOf("RenoDX.DLSS5"u8) >= 0;

    // ---------- build label (same rule as the app's AddonLibrary.FindBuildLabel) ----------

    /// <summary>
    /// The build label of a Lecram add-on: the highest <c>v1.2.3</c> / <c>v1.2.3-pre</c> string in the binary. The RenoDX
    /// framework in it carries v6.1.0 / v6.1.2 strings, which lose by being lower; on a tie the one with a pre-release suffix wins,
    /// then the most frequent, then the shorter. Null when the binary has none.
    /// </summary>
    internal static string? FindBuildLabel(ReadOnlySpan<byte> data)
    {
        var found = new Dictionary<string, (long Rank, bool Pre, int Count)>();
        for (var offset = 0; offset < data.Length;)
        {
            var at = data[offset..].IndexOf((byte)'v');
            if (at < 0) break;
            var i = offset + at;
            offset = i + 1;
            if (SemverAt(data, i) is not { } s) continue;
            var rank = s.Major * 1_000_000_000_000L + s.Minor * 1_000_000L + s.Patch;
            var pre = s.Text.Contains('-');
            found[s.Text] = (rank, pre, found.TryGetValue(s.Text, out var old) ? old.Count + 1 : 1);
        }
        return found.Count == 0 ? null
            : found.OrderByDescending(f => f.Value.Rank).ThenByDescending(f => f.Value.Pre)
                .ThenByDescending(f => f.Value.Count).ThenBy(f => f.Key.Length).First().Key;
    }

    private static (string Text, long Major, long Minor, long Patch)? SemverAt(ReadOnlySpan<byte> data, int i)
    {
        var end = i + 1;
        Span<long> numbers = stackalloc long[3];
        for (var part = 0; part < 3; part++)
        {
            if (part > 0)
            {
                if (end >= data.Length || data[end] != (byte)'.') return null;
                end++;
            }
            var from = end;
            long n = 0;
            while (end < data.Length && data[end] is >= (byte)'0' and <= (byte)'9' && end - from < 7) n = n * 10 + (data[end++] - (byte)'0');
            if (end == from || (end < data.Length && data[end] is >= (byte)'0' and <= (byte)'9')) return null;
            numbers[part] = n;
        }
        var core = end;
        if (end < data.Length && data[end] == (byte)'-')
        {
            var from = ++end;
            while (end < data.Length && IsSuffixChar(data[end])) end++;
            var suffix = end;
            while (suffix > from && data[suffix - 1] is (byte)'.' or (byte)'-') suffix--;
            if (suffix > from) return (Encoding.ASCII.GetString(data[i..suffix]), numbers[0], numbers[1], numbers[2]);
        }
        return (Encoding.ASCII.GetString(data[i..core]), numbers[0], numbers[1], numbers[2]);
    }


    private static bool IsSuffixChar(byte b) =>
        b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9') or (byte)'.' or (byte)'-';

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitRun();
}
