using System.Text;

namespace LarkCloud.Core;

/// <summary>A one-file zip protected with the classic zip password ("ZipCrypto"). Lark's security filter can
/// look inside normal zips, but not inside a password-protected one. Windows File Explorer, 7-Zip and WinRAR
/// open it with the password <see cref="Password"/>. This is only to get past the filter, not to keep the file
/// secret: the password is written in the README.</summary>
public static class ProtectedZip
{
    public const string Password = "lark";

    static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint CrcByte(uint crc, byte b) => (crc >> 8) ^ Crc[(crc ^ b) & 0xFF];

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcByte(c, b);
        return ~c;
    }

    sealed class Keys
    {
        uint _k0 = 0x12345678, _k1 = 0x23456789, _k2 = 0x34567890;

        public Keys(string password) { foreach (var b in Encoding.ASCII.GetBytes(password)) Update(b); }

        void Update(byte b)
        {
            _k0 = CrcByte(_k0, b);
            _k1 = (_k1 + (_k0 & 0xFF)) * 134775813 + 1;
            _k2 = CrcByte(_k2, (byte)(_k1 >> 24));
        }

        byte Stream() { var t = (ushort)(_k2 | 2); return (byte)((t * (t ^ 1)) >> 8); }

        public byte Encrypt(byte plain) { var c = (byte)(plain ^ Stream()); Update(plain); return c; }
        public byte Decrypt(byte cipher) { var p = (byte)(cipher ^ Stream()); Update(p); return p; }
    }

    /// <summary>Is this zip the password-protected kind?</summary>
    public static bool IsProtected(ReadOnlySpan<byte> zip) =>
        zip.Length >= 30 && BitConverter.ToUInt32(zip[..4]) == 0x04034b50 && (BitConverter.ToUInt16(zip[6..8]) & 1) != 0;

    public static void Write(string srcPath, string zipPath, string? entryName = null)
    {
        var data = File.ReadAllBytes(srcPath);
        var crc = Crc32(data);
        var (time, date) = DosTime(File.GetLastWriteTime(srcPath));
        var keys = new Keys(Password);
        var header = new byte[12];
        Random.Shared.NextBytes(header);
        header[11] = (byte)(crc >> 24);                     // lets unzip tools check the password
        var enc = new byte[12 + data.Length];
        for (var i = 0; i < 12; i++) enc[i] = keys.Encrypt(header[i]);
        for (var i = 0; i < data.Length; i++) enc[12 + i] = keys.Encrypt(data[i]);
        var name = Encoding.UTF8.GetBytes(entryName ?? Path.GetFileName(srcPath));

        using var fs = File.Create(zipPath);
        using var w = new BinaryWriter(fs);
        // local file header
        const ushort flags = 1 | 0x800;                      // encrypted, UTF-8 name
        w.Write(0x04034b50u); w.Write((ushort)20); w.Write(flags); w.Write((ushort)0);
        w.Write(time); w.Write(date); w.Write(crc); w.Write((uint)enc.Length); w.Write((uint)data.Length);
        w.Write((ushort)name.Length); w.Write((ushort)0); w.Write(name);
        w.Write(enc);
        // central directory
        var cdStart = (uint)fs.Position;
        w.Write(0x02014b50u); w.Write((ushort)20); w.Write((ushort)20); w.Write(flags); w.Write((ushort)0);
        w.Write(time); w.Write(date); w.Write(crc); w.Write((uint)enc.Length); w.Write((uint)data.Length);
        w.Write((ushort)name.Length); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
        w.Write(0u); w.Write(0u); w.Write(name);
        var cdSize = (uint)fs.Position - cdStart;
        // end of central directory
        w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);
        w.Write(cdSize); w.Write(cdStart); w.Write((ushort)0);
    }

    /// <summary>The file inside a zip written by <see cref="Write"/>.</summary>
    public static byte[] Read(byte[] zip)
    {
        if (!IsProtected(zip)) throw new InvalidDataException("not a password-protected zip");
        var method = BitConverter.ToUInt16(zip, 8);
        var crc = BitConverter.ToUInt32(zip, 14);
        var csize = (int)BitConverter.ToUInt32(zip, 18);
        var size = (int)BitConverter.ToUInt32(zip, 22);
        var start = 30 + BitConverter.ToUInt16(zip, 26) + BitConverter.ToUInt16(zip, 28);
        if (method != 0) throw new InvalidDataException("unsupported zip");
        var keys = new Keys(Password);
        for (var i = 0; i < 12; i++) keys.Decrypt(zip[start + i]);
        var data = new byte[size];
        for (var i = 0; i < size && i < csize - 12; i++) data[i] = keys.Decrypt(zip[start + 12 + i]);
        if (Crc32(data) != crc) throw new InvalidDataException("the zipped copy is damaged (checksum mismatch)");
        return data;
    }

    static (ushort Time, ushort Date) DosTime(DateTime t)
    {
        if (t.Year < 1980) t = new DateTime(1980, 1, 1);
        return ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)), (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day));
    }
}
