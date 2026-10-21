using System.Text;

namespace CatalogGen;

/// <summary>What a PE file's version resource says. Read by hand because FileVersionInfo only works on Windows and the Action runs on Linux.</summary>
public sealed record PeVersion(IReadOnlyDictionary<string, string> Strings, int[] Fixed)
{
    public string? Get(string key) => Strings.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    /// <summary>The numeric file version as 310.8.3 (the last part only when it isn't zero); null when the resource has none.</summary>
    public string? FileVersion => Fixed.All(n => n == 0) ? null : string.Join('.', Fixed[3] > 0 ? Fixed : Fixed[..3]);
}

public static class PeInfo
{
    private const uint ResourceTable = 2;
    private const int RtVersion = 16;

    /// <summary>The version resource, or null when the file is not a PE file or has none.</summary>
    public static PeVersion? ReadVersion(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var dos = Read(fs, 0, 0x40);
            if (dos[0] != 'M' || dos[1] != 'Z') return null;
            var pe = BitConverter.ToInt32(dos, 0x3C);
            var coff = Read(fs, pe, 24);
            if (BitConverter.ToUInt32(coff) != 0x4550) return null;
            var sections = BitConverter.ToUInt16(coff, 6);
            var optSize = BitConverter.ToUInt16(coff, 20);
            var opt = Read(fs, pe + 24, optSize);
            var dirs = BitConverter.ToUInt16(opt) == 0x20b ? 112 : 96;
            if (opt.Length < dirs + (ResourceTable + 1) * 8) return null;
            var rsrcRva = BitConverter.ToUInt32(opt, dirs + (int)ResourceTable * 8);
            if (rsrcRva == 0) return null;

            var table = Read(fs, pe + 24 + optSize, sections * 40);
            long ToFile(uint rva)
            {
                for (var i = 0; i < sections; i++)
                {
                    var virtualSize = BitConverter.ToUInt32(table, i * 40 + 8);
                    var start = BitConverter.ToUInt32(table, i * 40 + 12);
                    var raw = BitConverter.ToUInt32(table, i * 40 + 20);
                    if (rva >= start && rva < start + Math.Max(virtualSize, 1)) return raw + (rva - start);
                }
                throw new InvalidDataException("rva outside every section");
            }

            var root = ToFile(rsrcRva);
            // Three levels: resource type (version), resource id, language; the last one points at the data entry.
            var idDir = Child(fs, root, root, RtVersion);
            var langDir = idDir is null ? null : Child(fs, root, root + idDir.Value, null);
            var data = langDir is null ? null : Child(fs, root, root + langDir.Value, null);
            if (data is null) return null;
            var leaf = Read(fs, root + data.Value, 16);
            var blob = Read(fs, ToFile(BitConverter.ToUInt32(leaf)), Math.Min(BitConverter.ToInt32(leaf, 4), 1 << 16));
            return ParseBlob(blob);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>Offset, relative to the resource section, of the entry with <paramref name="id"/> (the first one when null) in the directory at <paramref name="dir"/>.</summary>
    private static long? Child(Stream fs, long root, long dir, int? id)
    {
        var head = Read(fs, dir, 16);
        var count = BitConverter.ToUInt16(head, 12) + BitConverter.ToUInt16(head, 14);
        var entries = Read(fs, dir + 16, count * 8);
        for (var i = 0; i < count; i++)
        {
            if (id is not null && BitConverter.ToUInt32(entries, i * 8) != id) continue;
            return BitConverter.ToUInt32(entries, i * 8 + 4) & 0x7FFFFFFF;
        }
        return null;
    }

    private static PeVersion ParseBlob(byte[] blob)
    {
        // VS_VERSIONINFO: length, value length, type, key "VS_VERSION_INFO", padding, VS_FIXEDFILEINFO, then StringFileInfo / VarFileInfo.
        var fixedParts = new int[4];
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var rootKeyEnd = KeyEnd(blob, 6);
        var valueAt = Align(rootKeyEnd);
        var valueLength = BitConverter.ToUInt16(blob, 2);
        if (valueLength >= 16 && BitConverter.ToUInt32(blob, valueAt) == 0xFEEF04BD)
        {
            var ms = BitConverter.ToUInt32(blob, valueAt + 8);
            var ls = BitConverter.ToUInt32(blob, valueAt + 12);
            fixedParts = [(int)(ms >> 16), (int)(ms & 0xFFFF), (int)(ls >> 16), (int)(ls & 0xFFFF)];
        }

        // Children are nested blocks (length, value length, type, key); StringFileInfo and its tables hold no value, the String blocks do.
        var end = Math.Min(BitConverter.ToUInt16(blob, 0), blob.Length);
        Walk(blob, Align(valueAt + valueLength), end, strings);
        return new PeVersion(strings, fixedParts);
    }

    private static void Walk(byte[] blob, int from, int to, Dictionary<string, string> strings)
    {
        for (var at = from; at + 6 <= to;)
        {
            var length = BitConverter.ToUInt16(blob, at);
            if (length < 6 || at + length > blob.Length) return;
            var valueLength = BitConverter.ToUInt16(blob, at + 2);
            var type = BitConverter.ToUInt16(blob, at + 4);
            var keyEnd = KeyEnd(blob, at + 6);
            var key = Encoding.Unicode.GetString(blob, at + 6, keyEnd - 2 - (at + 6));
            var valueAt = Align(keyEnd);
            if (type == 1 && valueLength > 0 && valueAt + valueLength * 2 <= blob.Length)
                strings.TryAdd(key, Encoding.Unicode.GetString(blob, valueAt, valueLength * 2).TrimEnd('\0'));
            else if (valueLength == 0) Walk(blob, valueAt, at + length, strings);
            at += Align(length);
        }
    }

    private static int KeyEnd(byte[] blob, int from)
    {
        var at = from;
        while (at + 1 < blob.Length && (blob[at] != 0 || blob[at + 1] != 0)) at += 2;
        return at + 2;
    }

    private static int Align(int offset) => (offset + 3) & ~3;

    private static byte[] Read(Stream fs, long offset, int count)
    {
        var buffer = new byte[count];
        fs.Seek(offset, SeekOrigin.Begin);
        fs.ReadExactly(buffer);
        return buffer;
    }
}
