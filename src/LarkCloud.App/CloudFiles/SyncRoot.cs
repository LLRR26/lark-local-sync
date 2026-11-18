using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using LarkCloud.Core;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using static LarkCloud.App.CloudFiles.Native;

namespace LarkCloud.App.CloudFiles;

/// <summary>One synced folder registered with Windows as a cloud sync root (like a OneDrive folder):
/// it gets cloud status icons, "Always keep on this device" / "Free up space", and Windows asks this
/// app for a file's data when someone opens it.</summary>
public sealed class SyncRoot : IDisposable
{
    static readonly Guid ProviderId = new("6c1c5b5e-3a5e-4f5b-9d3e-1a6f0e2b7c41");

    readonly Pair _pair;
    readonly Func<IRemoteDrive> _remote;
    readonly CfLocal _local;
    long _connectionKey;
    GCHandle _self;
    bool _connected;
    readonly ConcurrentDictionary<long, CancellationTokenSource> _fetches = new();

    /// <summary>Raised when an opened file's size in Lark no longer matches the placeholder (Lark changed it): sync now.</summary>
    public event Action? StaleFileOpened;
    public event Action<string, string>? FetchFailed;          // (relative path, message)
    public int ActiveDownloads => _fetches.Count;

    // Delegates must stay alive for as long as Windows can call them.
    static readonly CF_CALLBACK FetchDataCb = OnFetchData;
    static readonly CF_CALLBACK CancelFetchCb = OnCancelFetch;

    public SyncRoot(Pair pair, CfLocal local, Func<IRemoteDrive> remote)
    {
        _pair = pair;
        _local = local;
        _remote = remote;
    }

    public static string IdFor(string pairId) =>
        $"LarkCloud!{WindowsIdentity.GetCurrent().User?.Value ?? "user"}!{pairId}";

    // ---------------------------------------------------------------- registration
    public static void Register(Pair pair)
    {
        Directory.CreateDirectory(pair.LocalFolder);
        var id = IdFor(pair.Id);
        try
        {
            var name = pair.Label;
            var existing = StorageProviderSyncRootManager.GetCurrentSyncRoots().FirstOrDefault(r => r.Id == id);
            if (existing != null && existing.DisplayNameResource == name) return;
            var info = new StorageProviderSyncRootInfo
            {
                Id = id,
                Path = StorageFolder.GetFolderFromPathAsync(pair.LocalFolder).AsTask().GetAwaiter().GetResult(),
                DisplayNameResource = name,
                IconResource = Environment.ProcessPath + ",0",
                HydrationPolicy = StorageProviderHydrationPolicy.Full,
                HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
                PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
                InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime | StorageProviderInSyncPolicy.PreserveInsyncForSyncEngine,
                HardlinkPolicy = StorageProviderHardlinkPolicy.None,
                Version = AppPaths.Version,
                ShowSiblingsAsGroup = false,
                AllowPinning = true,
                Context = CryptographicBuffer.ConvertStringToBinary(pair.Id, BinaryStringEncoding.Utf8),
            };
            if (existing != null)
            {
                // Registered under an older display name ("Lark – …"): update it. If Windows ignores the update,
                // register it again from scratch (runs before the folder is connected, like "Rename on this PC" does).
                try { StorageProviderSyncRootManager.Register(info); } catch (Exception ex) { Log.Info($"Updating the Explorer name failed: {ex.Message}"); }
                if (StorageProviderSyncRootManager.GetCurrentSyncRoots().FirstOrDefault(r => r.Id == id)?.DisplayNameResource == name)
                {
                    Log.Info($"Renamed “{pair.LocalFolder}” in Explorer to “{name}”");
                    return;
                }
                Unregister(pair);
                Log.Info($"Registering “{pair.LocalFolder}” again to show it in Explorer as “{name}”");
            }
            StorageProviderSyncRootManager.Register(info);
            Log.Info($"Registered “{pair.LocalFolder}” with Windows as a Lark cloud folder");
        }
        catch (Exception ex) when (ex is not UnauthorizedAccessException)
        {
            // Older Windows builds / unusual setups: register directly with the Cloud Files API instead.
            Log.Warn($"Windows sync-root registration failed ({ex.Message}); using the basic registration");
            var reg = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)Marshal.SizeOf<CF_SYNC_REGISTRATION>(),
                ProviderName = AppPaths.AppTitle,
                ProviderVersion = AppPaths.Version,
                ProviderId = ProviderId,
            };
            var pol = new CF_SYNC_POLICIES
            {
                StructSize = (uint)Marshal.SizeOf<CF_SYNC_POLICIES>(),
                Hydration = new CF_HYDRATION_POLICY { Primary = CF_HYDRATION_POLICY_FULL, Modifier = CF_HYDRATION_POLICY_MODIFIER_AUTO_DEHYDRATION_ALLOWED },
                Population = new CF_POPULATION_POLICY { Primary = CF_POPULATION_POLICY_ALWAYS_FULL },
                InSync = CF_INSYNC_POLICY_TRACK_FILE_LAST_WRITE_TIME | CF_INSYNC_POLICY_PRESERVE_INSYNC_FOR_SYNC_ENGINE,
            };
            Check(CfRegisterSyncRoot(pair.LocalFolder, ref reg, ref pol,
                CF_REGISTER_FLAG_UPDATE | CF_REGISTER_FLAG_DISABLE_ON_DEMAND_POPULATION_ON_ROOT | CF_REGISTER_FLAG_MARK_IN_SYNC_ON_ROOT),
                "Registering the folder with Windows");
        }
    }

    public static void Unregister(Pair pair)
    {
        try { StorageProviderSyncRootManager.Unregister(IdFor(pair.Id)); }
        catch (Exception ex) { Log.Info($"Unregister via Windows: {ex.Message}"); }
        try { CfUnregisterSyncRoot(pair.LocalFolder); } catch { }
    }

    // ---------------------------------------------------------------- connection
    public void Connect()
    {
        if (_connected) return;
        _self = GCHandle.Alloc(this);
        var table = new[]
        {
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_FETCH_DATA, Callback = Marshal.GetFunctionPointerForDelegate(FetchDataCb) },
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_CANCEL_FETCH_DATA, Callback = Marshal.GetFunctionPointerForDelegate(CancelFetchCb) },
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_NONE, Callback = IntPtr.Zero },
        };
        var hr = CfConnectSyncRoot(_pair.LocalFolder, table, GCHandle.ToIntPtr(_self),
            CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH | CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION, out _connectionKey);
        if (hr < 0)
        {
            _self.Free();
            Check(hr, "Connecting to the Lark cloud folder");
        }
        _connected = true;
        Log.Info($"Connected “{_pair.LocalFolder}”: files are downloaded from Lark when opened");
    }

    public void Dispose()
    {
        if (!_connected) return;
        foreach (var c in _fetches.Values) c.Cancel();
        CfDisconnectSyncRoot(_connectionKey);
        _connected = false;
        if (_self.IsAllocated) _self.Free();
    }

    // ---------------------------------------------------------------- Windows asks for a file's data
    static void OnFetchData(IntPtr infoPtr, IntPtr paramsPtr)
    {
        CF_CALLBACK_INFO info;
        long offset, length;
        try
        {
            info = Marshal.PtrToStructure<CF_CALLBACK_INFO>(infoPtr);
            if (info.StructSize < 152) info.RequestKey = 0;      // older Windows: no RequestKey field
            offset = Marshal.ReadInt64(paramsPtr, PARAM_FETCH_REQUIRED_OFFSET);
            length = Marshal.ReadInt64(paramsPtr, PARAM_FETCH_REQUIRED_LENGTH);
        }
        catch { return; }
        if (GCHandle.FromIntPtr(info.CallbackContext).Target is not SyncRoot root) return;
        var token = info.FileIdentityLength > 0 && info.FileIdentity != IntPtr.Zero
            ? Encoding.UTF8.GetString(CopyBytes(info.FileIdentity, (int)info.FileIdentityLength)) : "";
        var path = info.NormalizedPath != IntPtr.Zero ? Marshal.PtrToStringUni(info.NormalizedPath) ?? "" : "";
        var cts = new CancellationTokenSource();
        root._fetches[info.TransferKey] = cts;
        var (ck, tk, rk, size) = (info.ConnectionKey, info.TransferKey, info.RequestKey, info.FileSize);
        Task.Run(() => root.Transfer(ck, tk, rk, token, path, offset, length, size, cts.Token));
    }

    static void OnCancelFetch(IntPtr infoPtr, IntPtr paramsPtr)
    {
        try
        {
            var info = Marshal.PtrToStructure<CF_CALLBACK_INFO>(infoPtr);
            if (GCHandle.FromIntPtr(info.CallbackContext).Target is SyncRoot root && root._fetches.TryRemove(info.TransferKey, out var cts))
                cts.Cancel();
        }
        catch { }
    }

    static byte[] CopyBytes(IntPtr p, int n)
    {
        var b = new byte[n];
        Marshal.Copy(p, b, 0, n);
        return b;
    }

    const int Chunk = 4 * 1024 * 1024;               // a multiple of 4 KB, as Windows requires (except the last piece)

    void Transfer(long ck, long tk, long rk, string token, string path, long reqOffset, long reqLength, long fileSize, CancellationToken ct)
    {
        var start = reqOffset & ~4095L;
        var pos = start;
        var rel = RelFromNormalized(path);
        try
        {
            if (token.Length == 0) throw new IOException("this file isn't linked to Lark");
            using var s = _remote().OpenRead(token, start, ct);
            if (s is IKnownLength k && k.TotalLength is long total && total != fileSize)
            {
                StaleFileOpened?.Invoke();
                throw new IOException("the file changed in Lark; refreshing it – open it again in a moment");
            }
            var buf = new byte[Chunk];
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                while (pos < fileSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var want = (int)Math.Min(Chunk, fileSize - pos);
                    var got = 0;
                    while (got < want)
                    {
                        var n = s.Read(buf, got, want - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got < want)
                    {
                        StaleFileOpened?.Invoke();
                        throw new IOException("Lark sent less data than expected (the file changed in Lark)");
                    }
                    Execute(ck, tk, rk, handle.AddrOfPinnedObject(), pos, got, STATUS_SUCCESS);
                    pos += got;
                    CfReportProviderProgress(ck, tk, fileSize, pos);
                }
            }
            finally { handle.Free(); }
            _local.HydratedAt[rel] = DateTime.UtcNow;
            Log.Info($"Downloaded on open: {rel} ({fileSize:N0} bytes)");
        }
        catch (Exception ex)
        {
            var cancelled = ct.IsCancellationRequested;
            if (!cancelled)
            {
                Log.Warn($"Couldn't download {rel} when it was opened: {ex.Message}");
                FetchFailed?.Invoke(rel, ex is NeedLoginException ? "Sign in to Lark again to open files." : ex.Message);
            }
            try
            {
                Execute(ck, tk, rk, IntPtr.Zero, reqOffset, reqLength, STATUS_CLOUD_FILE_UNSUCCESSFUL);
            }
            catch { }
        }
        finally { _fetches.TryRemove(tk, out _); }
    }

    static int _opInfoSize = Marshal.SizeOf<CF_OPERATION_INFO>();

    static void Execute(long ck, long tk, long rk, IntPtr buffer, long offset, long length, int status)
    {
        var op = new CF_OPERATION_INFO
        {
            StructSize = (uint)_opInfoSize, Type = CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = ck, TransferKey = tk, RequestKey = rk,
        };
        var p = new CF_OPERATION_PARAMETERS_TRANSFER_DATA
        {
            ParamSize = 40, CompletionStatus = status, Buffer = buffer, Offset = offset, Length = length,
        };
        var hr = CfExecute(ref op, ref p);
        if (hr == unchecked((int)0x80070057) && _opInfoSize == 48)
        {
            _opInfoSize = 40;                            // older Windows: CF_OPERATION_INFO without RequestKey
            op.StructSize = 40;
            hr = CfExecute(ref op, ref p);
        }
        Check(hr, "Handing the file's data to Windows");
    }

    string RelFromNormalized(string normalized)
    {
        // NormalizedPath is volume-relative ("\Users\me\Lark Cloud\X\a.txt"): match it to our folder.
        var root = _pair.LocalFolder.TrimEnd('\\');
        var noDrive = root.Length > 2 && root[1] == ':' ? root[2..] : root;
        if (normalized.StartsWith(noDrive + "\\", StringComparison.OrdinalIgnoreCase))
            return normalized[(noDrive.Length + 1)..].Replace('\\', '/');
        return normalized;
    }
}
