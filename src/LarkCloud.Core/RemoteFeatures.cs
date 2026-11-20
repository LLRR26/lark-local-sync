using System.Collections.Concurrent;
using System.IO.Compression;

namespace LarkCloud.Core;

/// <summary>Lark features beyond plain Drive files (implemented by LarkApi; the tests use a fake).</summary>
public interface ILarkExtras
{
    /// <summary>Export a Lark doc/sheet/... to a file (docx, pdf, xlsx, pptx); returns the export's file token and size.</summary>
    (string FileToken, long Size) Export(string docToken, string docType, string ext, CancellationToken ct);
    Stream OpenExport(string exportFileToken, CancellationToken ct);
}

/// <summary>Special kinds of items, recognised by their token.</summary>
public static class SpecialTokens
{
    /// <summary>A file Lark's security filter blocks: stored in Lark as "name (zipped).zip".</summary>
    public const string ZipPrefix = "zip:";
    public const string ZipSuffix = " (zipped).zip";

    /// <summary>A read-only offline copy of a Lark doc: "x:&lt;type&gt;:&lt;ext&gt;:&lt;doc token&gt;".</summary>
    public const string CopyPrefix = "x:";
    public const string CopyLabel = " (Lark copy)";

    public static bool IsZip(string? t) => t != null && t.StartsWith(ZipPrefix, StringComparison.Ordinal);
    public static bool IsCopy(string? t) => t != null && t.StartsWith(CopyPrefix, StringComparison.Ordinal);
    public static string Raw(string t) => IsZip(t) ? t[ZipPrefix.Length..] : t;

    public static string CopyToken(string type, string ext, string docToken) => $"{CopyPrefix}{type}:{ext}:{docToken}";

    public static (string Type, string Ext, string Doc) ParseCopy(string t)
    {
        var p = t[CopyPrefix.Length..].Split(':', 3);
        return (p[0], p[1], p[2]);
    }

    /// <summary>The file type an offline copy of this kind of Lark doc gets, or null if Lark can't export it.</summary>
    public static string? CopyExt(string docType, string format)
    {
        var pdf = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase);
        return docType switch
        {
            "doc" or "docx" => pdf ? "pdf" : "docx",
            "sheet" or "bitable" => "xlsx",
            "slides" => pdf ? "pdf" : "pptx",
            _ => null,
        };
    }
}

/// <summary>Wraps the real Lark connection and handles the special tokens, so the sync engine can treat
/// zipped files and offline doc copies like any other file.</summary>
public sealed class RemoteFeatures : IRemoteDrive
{
    readonly IRemoteDrive _inner;
    readonly ILarkExtras? _extras;
    readonly Func<string, string?> _getMeta;
    readonly Action<string, string> _setMeta;
    readonly ConcurrentDictionary<string, (string FileToken, long Size)> _exports = new();

    /// <summary>The last UploadZipped had to use the password-protected kind.</summary>
    [ThreadStatic] public static bool LastZipProtected;

    /// <summary>Copies whose size turned out different when opened (exported again): the engine updates them.</summary>
    public readonly ConcurrentDictionary<string, long> FreshSizes = new();

    public RemoteFeatures(IRemoteDrive inner, ILarkExtras? extras, Func<string, string?> getMeta, Action<string, string> setMeta)
    {
        _inner = inner;
        _extras = extras;
        _getMeta = getMeta;
        _setMeta = setMeta;
    }

    public IRemoteDrive Inner => _inner;
    public string RootToken => _inner.RootToken;
    public List<RemoteEntry> ListFolder(string folderToken) => _inner.ListFolder(folderToken);
    public string CreateFolder(string parentToken, string name) => _inner.CreateFolder(parentToken, name);
    public bool? CanEdit(string token, string type) => _inner.CanEdit(token, type);
    public string MyFoldersRoot() => _inner.MyFoldersRoot();

    static LarkApiException ReadOnlyCopy() =>
        new(-2, "this is a read-only copy of a Lark doc (no permission to change it here)");

    // ---------------------------------------------------------------- reading
    public long ProbeSize(string token)
    {
        if (SpecialTokens.IsCopy(token)) return ExportNow(token, CancellationToken.None).Size;
        if (SpecialTokens.IsZip(token))
        {
            // Our zips hold one file and its size is in the first header: read 30 bytes, not the whole zip.
            var head = new byte[30];
            using (var s = _inner.OpenRead(SpecialTokens.Raw(token), 0, CancellationToken.None))
                if (s.ReadAtLeast(head, 30, throwOnEndOfStream: false) == 30 && BitConverter.ToUInt32(head, 0) == 0x04034b50
                    && (BitConverter.ToUInt16(head, 6) & 8) == 0 && BitConverter.ToUInt32(head, 22) is var size and not 0xFFFFFFFF)
                    return size;
            using var z = OpenZip(token, CancellationToken.None, out _);             // sizes after the data, or zip64
            return z.Length;
        }
        return _inner.ProbeSize(token);
    }

    public Stream OpenRead(string token, long offset, CancellationToken ct)
    {
        if (SpecialTokens.IsCopy(token)) return OpenCopy(token, offset, ct);
        if (SpecialTokens.IsZip(token))
        {
            var s = OpenZip(token, ct, out var total);
            if (offset > 0) Skip(s, offset);
            return s;
        }
        return _inner.OpenRead(token, offset, ct);
    }

    static void Skip(Stream s, long n)
    {
        var buf = new byte[81920];
        while (n > 0)
        {
            var k = s.Read(buf, 0, (int)Math.Min(buf.Length, n));
            if (k <= 0) break;
            n -= k;
        }
    }

    /// <summary>Download the zip and hand back the single file inside it (as a stream that knows its length).</summary>
    KnownLengthStream OpenZip(string token, CancellationToken ct, out long total)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $".lcs-{Guid.NewGuid():N}.zip");
        using (var src = _inner.OpenRead(SpecialTokens.Raw(token), 0, ct))
        using (var dst = File.Create(tmp))
            src.CopyTo(dst, 1 << 20);
        var head = new byte[30];
        using (var f = File.OpenRead(tmp)) f.ReadAtLeast(head, 30, throwOnEndOfStream: false);
        if (ProtectedZip.IsProtected(head))
        {
            var data = ProtectedZip.Read(File.ReadAllBytes(tmp));
            try { File.Delete(tmp); } catch { }
            total = data.Length;
            return new KnownLengthStream(new MemoryStream(data), data.Length, null);
        }
        var zip = ZipFile.OpenRead(tmp);
        var entry = zip.Entries.FirstOrDefault(e => e.Length > 0 || e.Name.Length > 0)
                    ?? throw new IOException("the zipped copy in Lark is empty");
        total = entry.Length;
        return new KnownLengthStream(entry.Open(), entry.Length, () => { zip.Dispose(); try { File.Delete(tmp); } catch { } });
    }

    (string FileToken, long Size) ExportNow(string token, CancellationToken ct)
    {
        if (_extras == null) throw new LarkApiException(-3, "offline copies of Lark docs aren't available");
        var (type, ext, doc) = SpecialTokens.ParseCopy(token);
        var r = _extras.Export(doc, type, ext, ct);
        _exports[token] = r;
        _setMeta("export:" + token, $"{r.FileToken}|{r.Size}");
        return r;
    }

    Stream OpenCopy(string token, long offset, CancellationToken ct)
    {
        if (_extras == null) throw new LarkApiException(-3, "offline copies of Lark docs aren't available");
        if (!_exports.TryGetValue(token, out var known) && _getMeta("export:" + token) is { } saved && saved.Split('|') is [var ft, var sz])
            known = (ft, long.Parse(sz));
        Stream s;
        long size;
        try
        {
            if (known.FileToken == null) throw new IOException("not exported yet");
            s = _extras.OpenExport(known.FileToken, ct);
            size = known.Size;
        }
        catch (Exception ex) when (ex is IOException or LarkApiException)
        {
            // The exported file has expired in Lark: export again. If its size changed, the placeholder is
            // updated on the next check (FreshSizes) and opening it again works.
            var fresh = ExportNow(token, ct);
            if (fresh.Size != known.Size) FreshSizes[token] = fresh.Size;
            s = _extras.OpenExport(fresh.FileToken, ct);
            size = fresh.Size;
        }
        if (offset > 0) Skip(s, offset);
        return new KnownLengthStream(s, size, null);
    }

    // ---------------------------------------------------------------- writing
    public string Upload(string srcPath, string parentToken, string name, string? existingToken)
    {
        if (SpecialTokens.IsCopy(existingToken)) throw ReadOnlyCopy();
        if (SpecialTokens.IsZip(existingToken)) return UploadZipped(srcPath, parentToken, name, existingToken);
        return _inner.Upload(srcPath, parentToken, name, existingToken);
    }

    /// <summary>Store a file Lark's filter blocks as "name (zipped).zip" (the app shows it as the original file).</summary>
    public string UploadZipped(string srcPath, string parentToken, string name, string? existingToken)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $".lcs-{Guid.NewGuid():N}.zip");
        try
        {
            // A normal zip first (opens without a password). Lark's filter can look inside those, so if it's
            // blocked too, a password-protected zip (password "lark"), which the filter can't read.
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(srcPath, name, CompressionLevel.Optimal);
            var raw = existingToken == null ? null : SpecialTokens.Raw(existingToken);
            var keep = SpecialTokens.IsZip(existingToken) ? raw : null;
            string tok;
            try { tok = _inner.Upload(tmp, parentToken, name + SpecialTokens.ZipSuffix, keep); }
            catch (LarkApiException ex) when (ex.IsBlocked)
            {
                ProtectedZip.Write(srcPath, tmp, name);
                tok = _inner.Upload(tmp, parentToken, name + SpecialTokens.ZipSuffix, keep);
                LastZipProtected = true;
                Log.Info($"{name}: stored as a password-protected zip (password \"{ProtectedZip.Password}\")");
            }
            if (raw != null && !SpecialTokens.IsZip(existingToken))
            {
                try { _inner.Delete(raw, Kinds.File); } catch (Exception ex) { Log.Warn($"Couldn't trash the old copy of {name}: {ex.Message}"); }
            }
            return SpecialTokens.ZipPrefix + tok;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public void Delete(string token, string type)
    {
        if (SpecialTokens.IsCopy(token)) return;                  // removing an offline copy never touches the doc
        _inner.Delete(SpecialTokens.Raw(token), type);
    }

    public void Move(string token, string type, string destFolderToken)
    {
        if (SpecialTokens.IsCopy(token)) throw ReadOnlyCopy();
        _inner.Move(SpecialTokens.Raw(token), type, destFolderToken);
    }

    public void Rename(string token, string type, string newName)
    {
        if (SpecialTokens.IsCopy(token)) throw ReadOnlyCopy();
        _inner.Rename(SpecialTokens.Raw(token), type, SpecialTokens.IsZip(token) ? newName + SpecialTokens.ZipSuffix : newName);
    }
}

/// <summary>A stream that knows its total length (so a size mismatch is noticed before handing data to Windows).</summary>
public sealed class KnownLengthStream : Stream, IKnownLength
{
    readonly Stream _inner;
    readonly Action? _onDispose;
    public long? TotalLength { get; }

    public KnownLengthStream(Stream inner, long length, Action? onDispose)
    {
        _inner = inner;
        TotalLength = length;
        _onDispose = onDispose;
    }

    public override long Length => TotalLength ?? 0;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _inner.Dispose(); _onDispose?.Invoke(); }
        base.Dispose(disposing);
    }
}
