using System.IO.Compression;
using System.Text;

namespace CatalogGen.Tests;

/// <summary>Builds a tiny but valid PE file (one .rsrc section) whose version resource says what the test wants.</summary>
public static class TestPe
{
    public static byte[] Make(string? internalName = null, string? originalName = null, int[]? fileVersion = null, string? fileVersionText = null, string? extra = null)
    {
        var strings = new List<byte[]>();
        void Add(string key, string? value) { if (value is not null) strings.Add(Block(key, 1, Text(value), (ushort)(value.Length + 1))); }
        Add("InternalName", internalName);
        Add("OriginalFilename", originalName);
        Add("FileVersion", fileVersionText);
        var table = Block("040904b0", 1, [], 0, strings.ToArray());
        var info = Block("StringFileInfo", 1, [], 0, table);
        var fixedInfo = new byte[52];
        BitConverter.GetBytes(0xFEEF04BDu).CopyTo(fixedInfo, 0);
        var v = fileVersion ?? [0, 0, 0, 0];
        BitConverter.GetBytes((uint)((v[0] << 16) | v[1])).CopyTo(fixedInfo, 8);
        BitConverter.GetBytes((uint)((v[2] << 16) | v[3])).CopyTo(fixedInfo, 12);
        var blob = Block("VS_VERSION_INFO", 0, fixedInfo, 52, info);

        const int rsrcRva = 0x1000, rawAt = 0x200;
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("MZ"u8.ToArray());
        w.Seek(0x3C, SeekOrigin.Begin); w.Write(0x40);
        w.Seek(0x40, SeekOrigin.Begin); w.Write("PE\0\0"u8.ToArray());
        w.Write((ushort)0x8664); w.Write((ushort)1); w.Write(0); w.Write(0); w.Write(0); w.Write((ushort)240); w.Write((ushort)0x22);
        var optional = new byte[240];
        BitConverter.GetBytes((ushort)0x20b).CopyTo(optional, 0);
        BitConverter.GetBytes(rsrcRva).CopyTo(optional, 112 + 16);
        BitConverter.GetBytes(blob.Length + 0x58).CopyTo(optional, 112 + 20);
        w.Write(optional);
        w.Write(".rsrc\0\0\0"u8.ToArray()); w.Write(blob.Length + 0x58); w.Write(rsrcRva); w.Write(blob.Length + 0x58); w.Write(rawAt); w.Write(new byte[16]);

        w.Seek(rawAt, SeekOrigin.Begin);
        w.Write(new byte[12]); w.Write((ushort)0); w.Write((ushort)1); w.Write(16); w.Write(0x80000000u | 0x18);
        w.Write(new byte[12]); w.Write((ushort)0); w.Write((ushort)1); w.Write(1); w.Write(0x80000000u | 0x30);
        w.Write(new byte[12]); w.Write((ushort)0); w.Write((ushort)1); w.Write(0x409); w.Write(0x48);
        w.Write(rsrcRva + 0x58); w.Write(blob.Length); w.Write(0); w.Write(0);
        w.Write(blob);
        if (extra is not null) w.Write(Encoding.ASCII.GetBytes(extra));
        return ms.ToArray();
    }

    public static byte[] Zip(string entryName, byte[] content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var s = zip.CreateEntry(entryName).Open();
            s.Write(content);
        }
        return ms.ToArray();
    }

    private static byte[] Text(string s) => Encoding.Unicode.GetBytes(s + "\0");

    private static byte[] Block(string key, ushort type, byte[] value, ushort valueLength, params byte[][] children)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write(valueLength); w.Write(type);
        w.Write(Text(key));
        Pad(ms);
        w.Write(value);
        Pad(ms);
        foreach (var c in children) { w.Write(c); Pad(ms); }
        var bytes = ms.ToArray();
        BitConverter.GetBytes((ushort)bytes.Length).CopyTo(bytes, 0);
        return bytes;
    }

    private static void Pad(MemoryStream ms) { while (ms.Length % 4 != 0) ms.WriteByte(0); }
}
