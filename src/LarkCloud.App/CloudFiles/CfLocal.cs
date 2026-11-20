using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using LarkCloud.Core;
using static LarkCloud.App.CloudFiles.Native;

namespace LarkCloud.App.CloudFiles;

/// <summary>The synced folder on the PC, as Windows cloud placeholders (ICloudLocal for the engine).</summary>
public sealed class CfLocal : ICloudLocal
{
    public string Root { get; }

    // Reading a placeholder's Lark identity needs a file handle, so it's cached per path and only
    // re-read when the item's times/size/attributes change.
    readonly ConcurrentDictionary<string, (long Key, string? Identity, bool IsPlaceholder, bool InSync)> _cache = new(StringComparer.OrdinalIgnoreCase);
    public readonly ConcurrentDictionary<string, DateTime> HydratedAt = new(StringComparer.OrdinalIgnoreCase);

    public CfLocal(string root) { Root = Path.GetFullPath(root).TrimEnd('\\'); }

    public string FullPath(string rel) => rel.Length == 0 ? Root : Path.Combine(Root, rel.Replace('/', '\\'));
    string Rel(string full) => full[(Root.Length + 1)..].Replace('\\', '/');

    // ---------------------------------------------------------------- reading
    List<string> _unreadable = new();
    public IReadOnlyCollection<string> UnreadablePaths => _unreadable;

    public List<LocalItem> Scan()
    {
        // The synced folder itself missing (drive not ready, folder moved) must stop the check – otherwise
        // everything would look deleted on this PC.
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException($"The synced folder “{Root}” can't be found");
        var outList = new List<LocalItem>();
        var unreadable = new List<string>();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(Root));
        var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> items;
            try { items = dir.EnumerateFileSystemInfos("*", opts).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (dir.FullName.TrimEnd('\\').Length <= Root.Length) throw;     // the synced folder itself
                unreadable.Add(Rel(dir.FullName));
                continue;
            }
            foreach (var fi in items)
            {
                var item = Describe(fi);
                if (item == null) { unreadable.Add(Rel(fi.FullName)); continue; }
                outList.Add(item);
                // .NET doesn't recurse into reparse points; cloud placeholder folders are reparse points,
                // so walk them ourselves (but never into real symbolic links / junctions).
                if (fi is DirectoryInfo di && fi.LinkTarget == null) stack.Push(di);
            }
        }
        _unreadable = unreadable;
        if (unreadable.Count > 0) Log.Warn($"Couldn't read {unreadable.Count} item(s) on this PC this time (e.g. {unreadable[0]}); they're left alone");
        return outList;
    }

    public LocalPresence CheckPresence(string rel)
    {
        try
        {
            File.GetAttributes(FullPath(rel));
            return LocalPresence.Present;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A disconnected drive or a moved sync root also makes its children appear missing.
            return Directory.Exists(Root) ? LocalPresence.Missing : LocalPresence.Unreadable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LocalPresence.Unreadable;
        }
    }

    public LocalItem? Stat(string rel)
    {
        var full = FullPath(rel);
        FileSystemInfo fi = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        if (!fi.Exists) return null;
        _cache.TryRemove(Rel(fi.FullName), out _);
        return Describe(fi);
    }

    LocalItem? Describe(FileSystemInfo fi)
    {
        try
        {
            var attrs = (uint)fi.Attributes;
            var isDir = (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;
            var rel = Rel(fi.FullName);
            var size = isDir ? 0 : ((FileInfo)fi).Length;
            var mtime = fi.LastWriteTimeUtc.Ticks;
            var item = new LocalItem
            {
                Path = rel, IsDir = isDir, Size = size, MTime = mtime,
                OnDisk = isDir || (attrs & (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_OFFLINE)) == 0,
                Pinned = (attrs & FILE_ATTRIBUTE_PINNED) != 0,
                ReadOnly = (attrs & 1) != 0,
                Unpinned = (attrs & FILE_ATTRIBUTE_UNPINNED) != 0,
            };
            var used = Math.Max(fi.LastAccessTimeUtc.Ticks, mtime);
            if (HydratedAt.TryGetValue(rel, out var h)) used = Math.Max(used, h.Ticks);
            item.LastUse = used;
            if ((attrs & FILE_ATTRIBUTE_REPARSE_POINT) == 0)
            {
                item.IsPlaceholder = false;                    // a plain file/folder: not linked to Lark yet
                return item;
            }
            var key = HashCode.Combine(fi.CreationTimeUtc.Ticks, mtime, size, attrs & ~(FILE_ATTRIBUTE_PINNED | FILE_ATTRIBUTE_UNPINNED | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_OFFLINE));
            if (!_cache.TryGetValue(rel, out var c) || c.Key != key)
            {
                var (identity, isPh, inSync) = ReadPlaceholderInfo(fi.FullName);
                c = (key, identity, isPh, inSync);
                _cache[rel] = c;
            }
            item.IsPlaceholder = c.IsPlaceholder;
            item.Identity = c.Identity;
            item.InSync = c.InSync;
            return item;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    static (string? Identity, bool IsPlaceholder, bool InSync) ReadPlaceholderInfo(string full)
    {
        using var h = CreateFile(Long(full), FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return (null, false, true);
        return ReadPlaceholderInfo(h);
    }

    static (string? Identity, bool IsPlaceholder, bool InSync) ReadPlaceholderInfo(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        const int size = 4096 + 64;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            var hr = CfGetPlaceholderInfo(h, CF_PLACEHOLDER_INFO_BASIC, buf, size, out _);
            if (hr < 0) return (null, false, true);           // not a cloud file (another kind of reparse point)
            var inSync = Marshal.ReadInt32(buf, 4) == CF_IN_SYNC_STATE_IN_SYNC;
            var len = Marshal.ReadInt32(buf, 24);
            string? identity = null;
            if (len > 0 && len <= 4096)
            {
                var bytes = new byte[len];
                Marshal.Copy(buf + 28, bytes, 0, len);
                identity = Encoding.UTF8.GetString(bytes);
            }
            return (identity, true, inSync);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---------------------------------------------------------------- placeholders
    static FILE_BASIC_INFO Times(DateTime utc, uint attrs)
    {
        var ft = utc.ToFileTimeUtc();
        return new FILE_BASIC_INFO { CreationTime = ft, LastAccessTime = ft, LastWriteTime = ft, ChangeTime = ft, FileAttributes = attrs };
    }

    void Create(string rel, string token, long size, DateTime mtimeUtc, bool isDir)
    {
        var parent = FullPath(PathUtil.Parent(rel));
        var name = Marshal.StringToHGlobalUni(PathUtil.Name(rel));
        var id = Encoding.UTF8.GetBytes(token);
        var idPtr = Marshal.AllocHGlobal(id.Length);
        try
        {
            Marshal.Copy(id, 0, idPtr, id.Length);
            var info = new[]
            {
                new CF_PLACEHOLDER_CREATE_INFO
                {
                    RelativeFileName = name,
                    FsMetadata = new CF_FS_METADATA
                    {
                        BasicInfo = Times(mtimeUtc, isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL),
                        FileSize = isDir ? 0 : size,
                    },
                    FileIdentity = idPtr, FileIdentityLength = (uint)id.Length,
                    Flags = CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC | (isDir ? CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION : 0),
                },
            };
            Check(CfCreatePlaceholders(Long(parent), info, 1, 0, out _), "Creating the placeholder");
            Check(info[0].Result, "Creating the placeholder");
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(idPtr);
        }
    }

    public void CreateFolderPlaceholder(string rel, string token) => Create(rel, token, 0, DateTime.UtcNow, true);

    public void CreateFilePlaceholder(string rel, string token, long size, DateTime mtimeUtc)
    {
        if (size == 0)
        {
            // An empty file has no data to fetch: create it for real and link it to Lark.
            var full = FullPath(rel);
            using (new FileStream(full, FileMode.CreateNew, FileAccess.Write)) { }
            File.SetLastWriteTimeUtc(full, mtimeUtc);
            File.SetCreationTimeUtc(full, mtimeUtc);
            MarkSynced(rel, token);
            return;
        }
        Create(rel, token, size, mtimeUtc, false);
    }

    /// <summary>Run `act` with the read-only flag lifted (the app's own updates must work on read-only files).</summary>
    static void Writable(string full, Action act)
    {
        var attrs = File.Exists(full) ? File.GetAttributes(full) : 0;
        var ro = (attrs & FileAttributes.ReadOnly) != 0;
        if (ro) File.SetAttributes(full, attrs & ~FileAttributes.ReadOnly);
        try { act(); }
        finally { if (ro && File.Exists(full)) File.SetAttributes(full, File.GetAttributes(full) | FileAttributes.ReadOnly); }
    }

    public void SetReadOnly(string rel, bool readOnly)
    {
        var full = FullPath(rel);
        var a = File.GetAttributes(full);
        File.SetAttributes(full, readOnly ? a | FileAttributes.ReadOnly : a & ~FileAttributes.ReadOnly);
        _cache.TryRemove(rel, out _);
    }

    public void UpdateFilePlaceholder(string rel, string token, long size, DateTime mtimeUtc) =>
        Writable(FullPath(rel), () => UpdateFilePlaceholderCore(rel, token, size, mtimeUtc));

    void UpdateFilePlaceholderCore(string rel, string token, long size, DateTime mtimeUtc)
    {
        var full = FullPath(rel);
        using var h = new OplockHandle(full, exclusive: true);        // fails while the file is open in an app
        var meta = new CF_FS_METADATA { BasicInfo = Times(mtimeUtc, 0), FileSize = size };
        var mp = Marshal.AllocHGlobal(Marshal.SizeOf<CF_FS_METADATA>());
        try
        {
            Marshal.StructureToPtr(meta, mp, false);
            var id = Encoding.UTF8.GetBytes(token);
            Check(CfUpdatePlaceholder(h.Win32, mp, id, (uint)id.Length, IntPtr.Zero, 0,
                CF_UPDATE_FLAG_MARK_IN_SYNC | CF_UPDATE_FLAG_DEHYDRATE, IntPtr.Zero, IntPtr.Zero), "Updating the file from Lark");
        }
        finally { Marshal.FreeHGlobal(mp); }
        _cache.TryRemove(rel, out _);
        HydratedAt.TryRemove(rel, out _);
    }

    public LocalItem MarkSynced(string rel, string token)
    {
        var full = FullPath(rel);
        var current = Stat(rel) ?? throw new FileNotFoundException(rel);
        Writable(full, () =>
        {
            using var h = new OplockHandle(full, exclusive: false);
            MarkSynced(h.Win32, current, token);
        });
        _cache.TryRemove(rel, out _);
        return Stat(rel) ?? current;
    }

    /// <summary>Upload copies live in a hidden ".lcs-*" folder inside the synced folder: same drive (a big upload can't fill C:),
    /// and the ignore rules keep it out of every scan and every sync.</summary>
    public string SnapshotDir => Path.Combine(Root, ".lcs-upload");

    public void CleanSnapshots()
    {
        var dir = SnapshotDir;
        Directory.CreateDirectory(dir);
        File.SetAttributes(dir, FileAttributes.Directory | FileAttributes.Hidden);
        foreach (var f in Directory.EnumerateFiles(dir, ".lcs-*"))
            try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public LocalItem? MarkUploaded(string rel, string token, long size, string hash)
    {
        var full = FullPath(rel);
        LocalItem? uploaded = null;
        Writable(full, () =>
        {
            try { uploaded = MarkUploadedExclusive(full, rel, token, size, hash); }
            // Open in another app (Word, Excel…) that allows sharing: verify without locking it out.
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { uploaded = MarkUploadedShared(full, rel, token, size, hash); }
        });
        _cache.TryRemove(rel, out _);
        return uploaded;
    }

    static bool SameAsUploaded(FileStream file, long size, string hash) =>
        file.Length == size && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)) == hash;

    static LocalItem? MarkUploadedExclusive(string full, string rel, string token, long size, string hash)
    {
        // Read and mark through the same exclusive handle so a save cannot replace the verified bytes.
        using var file = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var handle = file.SafeFileHandle;
        var attrs = (uint)File.GetAttributes(handle);
        if ((attrs & (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_OFFLINE)) != 0) return null;
        if (!SameAsUploaded(file, size, hash)) return null;
        var current = new LocalItem
        {
            Path = rel, Size = file.Length, MTime = File.GetLastWriteTimeUtc(handle).Ticks,
            IsPlaceholder = (attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0 && ReadPlaceholderInfo(handle).IsPlaceholder,
        };
        MarkSynced(handle.DangerousGetHandle(), current, token);
        return current;
    }

    /// <summary>The file is open in another app, so it can't be locked. The marking handle asks only for WRITE_DAC (which share
    /// modes don't govern) and the bytes are read through a second, sharing handle. Three guards keep us from marking bytes we
    /// didn't upload: size/time are noted before and compared after hashing, the baseline gets the time noted BEFORE hashing (a
    /// later write then shows as a change at the next scan), and Windows only applies the mark if the file's USN is still the
    /// one read before hashing. Anything doubtful returns null / throws and the mark is retried on a later pass.</summary>
    static LocalItem? MarkUploadedShared(string full, string rel, string token, long size0, string hash)
    {
        using var mark = CreateFile(Long(full), WRITE_DAC | FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (mark.IsInvalid) throw new IOException($"Couldn't open {rel} to mark it as synced (error {Marshal.GetLastWin32Error()})");
        var usn = ReadUsn(mark);
        if (usn == 0) return null;                                  // no change journal: nothing to guard with
        using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var handle = file.SafeFileHandle;
        var attrs = (uint)File.GetAttributes(handle);
        if ((attrs & (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_OFFLINE)) != 0) return null;
        var (size, mtime) = (file.Length, File.GetLastWriteTimeUtc(handle).Ticks);
        if (!SameAsUploaded(file, size0, hash)) return null;
        if (file.Length != size || File.GetLastWriteTimeUtc(handle).Ticks != mtime) return null;     // written while hashing
        var current = new LocalItem
        {
            Path = rel, Size = size, MTime = mtime,
            IsPlaceholder = (attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0 && ReadPlaceholderInfo(handle).IsPlaceholder,
        };
        var usnPtr = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(usnPtr, usn);
            MarkSynced(mark.DangerousGetHandle(), current, token, usnPtr);     // refused if the USN moved on
        }
        finally { Marshal.FreeHGlobal(usnPtr); }
        return current;
    }

    static void MarkSynced(IntPtr handle, LocalItem current, string token, IntPtr usn = default)
    {
        var id = Encoding.UTF8.GetBytes(token);
        if (!current.IsPlaceholder)
            Check(CfConvertToPlaceholder(handle, id, (uint)id.Length, CF_CONVERT_FLAG_MARK_IN_SYNC, usn, IntPtr.Zero), "Linking the file to Lark");
        else
            Check(CfUpdatePlaceholder(handle, IntPtr.Zero, id, (uint)id.Length, IntPtr.Zero, 0,
                CF_UPDATE_FLAG_MARK_IN_SYNC | (current.IsDir ? CF_UPDATE_FLAG_DISABLE_ON_DEMAND_POPULATION : 0), usn, IntPtr.Zero),
                "Marking the file as synced");
    }

    public void Move(string from, string to)
    {
        var a = FullPath(from);
        var b = FullPath(to);
        if (Directory.Exists(a))
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                var tmp = a + ".lcs-move";                            // case-only rename of a folder
                Directory.Move(a, tmp);
                Directory.Move(tmp, b);
            }
            else Directory.Move(a, b);
        }
        else File.Move(a, b);
        foreach (var k in _cache.Keys.Where(k => k.Equals(from, StringComparison.OrdinalIgnoreCase) || k.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)).ToList())
            _cache.TryRemove(k, out _);
    }

    public void Delete(string rel, bool recycle)
    {
        var full = FullPath(rel);
        if (Directory.Exists(full))
        {
            if (recycle) Recycle(full, true, true);
            else Directory.Delete(full, recursive: false);
        }
        else if (File.Exists(full))
        {
            // Only a copy Lark still has (in sync, or never downloaded) may skip the Recycle Bin: edits made here must stay recoverable.
            var st = recycle ? Stat(rel) : null;
            var inLarkTrash = st != null && (st.InSync || !st.OnDisk);
            var attrs = File.GetAttributes(full);
            File.SetAttributes(full, attrs & ~FileAttributes.ReadOnly);
            try
            {
                if (recycle) Recycle(full, false, inLarkTrash);
                else File.Delete(full);
            }
            catch
            {
                if (File.Exists(full)) File.SetAttributes(full, attrs);
                throw;
            }
        }
        _cache.TryRemove(rel, out _);
    }

    static readonly object RecycleLock = new();

    static void Recycle(string full, bool isDir, bool canDeleteForGood)
    {
        // Direct shell call with no UI at all (the VB helper can still show error boxes, and this runs on a background thread).
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE, pFrom = full + "\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_NOCONFIRMMKDIR),
        };
        int rc;
        // (deletes run several at a time; the shell's file operation is taken one at a time)
        try { lock (RecycleLock) rc = SHFileOperationW(ref op); }
        catch (Exception ex) { rc = -1; Log.Warn($"Recycle Bin call failed for {Path.GetFileName(full)}: {ex.Message}"); }
        var exists = isDir ? Directory.Exists(full) : File.Exists(full);
        if (!exists) return;
        if (rc == 0 && !op.fAnyOperationsAborted) return;
        // The Recycle Bin couldn't take it (turned off, full, file too big, path too long). Unless something else is using the
        // item – then it's left alone and retried later – it's deleted for good, because it was deleted in Lark.
        if (!canDeleteForGood)
            throw new IOException("The Recycle Bin couldn't take this file and it has changes made on this PC, so it was left here.");
        if (!isDir)
        {
            try { using var probe = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.None); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("Couldn't move this item to the Recycle Bin because it's in use. It was left on this PC.", ex);
            }
        }
        Log.Warn($"The Recycle Bin couldn't take {Path.GetFileName(full)} (code {rc}); deleting it permanently");
        if (isDir) Directory.Delete(full, recursive: false);        // never a populated folder: that stays for the next check
        else File.Delete(full);
    }

    /// <summary>"Free up space": drop the local copy; the file stays visible and comes back when opened.</summary>
    public void Dehydrate(string rel)
    {
        Writable(FullPath(rel), () =>
        {
            using var h = new OplockHandle(FullPath(rel), exclusive: true);
            Check(CfDehydratePlaceholder(h.Win32, 0, -1, 0, IntPtr.Zero), "Freeing up space");
        });
        HydratedAt.TryRemove(rel, out _);
    }

    /// <summary>Download the file now (for "Always keep on this device").</summary>
    public void Hydrate(string rel, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var h = CreateFile(Long(FullPath(rel)), GENERIC_READ, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"Couldn't open {rel} (error {Marshal.GetLastWin32Error()})");
        Check(CfHydratePlaceholder(h, 0, -1, 0, IntPtr.Zero), "Downloading the file");
    }

    /// <summary>Turn a placeholder back into a normal file (used when a folder stops syncing).</summary>
    public void Revert(string rel) => Writable(FullPath(rel), () =>
    {
        using var h = new OplockHandle(FullPath(rel), exclusive: false);
        Check(CfRevertPlaceholder(h.Win32, CF_REVERT_FLAG_NONE, IntPtr.Zero), "Keeping the file");
    });

    public void WriteShortcut(string rel, string url)
    {
        var full = FullPath(rel);
        File.WriteAllText(full, $"[InternetShortcut]\r\nURL={url}\r\n", Encoding.ASCII);
        _cache.TryRemove(rel, out _);
    }

    public Stream OpenForUpload(string rel) =>
        new FileStream(FullPath(rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
}
