using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LarkCloud.App.CloudFiles;

/// <summary>Windows Cloud Files API (cfapi.h / cldapi.dll), only the parts this app uses.
/// Layouts follow the Windows SDK headers for 64-bit Windows.</summary>
internal static class Native
{
    // ---------------------------------------------------------------- enums / flags
    public const ushort CF_HYDRATION_POLICY_FULL = 2;
    public const ushort CF_HYDRATION_POLICY_MODIFIER_AUTO_DEHYDRATION_ALLOWED = 4;
    public const ushort CF_POPULATION_POLICY_ALWAYS_FULL = 3;
    public const uint CF_INSYNC_POLICY_TRACK_FILE_LAST_WRITE_TIME = 0x100;
    public const uint CF_INSYNC_POLICY_PRESERVE_INSYNC_FOR_SYNC_ENGINE = 0x80000000;

    public const uint CF_REGISTER_FLAG_UPDATE = 1;
    public const uint CF_REGISTER_FLAG_DISABLE_ON_DEMAND_POPULATION_ON_ROOT = 2;
    public const uint CF_REGISTER_FLAG_MARK_IN_SYNC_ON_ROOT = 4;

    public const uint CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH = 4;
    public const uint CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION = 8;

    public const int CF_CALLBACK_TYPE_FETCH_DATA = 0;
    public const int CF_CALLBACK_TYPE_CANCEL_FETCH_DATA = 2;
    public const int CF_CALLBACK_TYPE_NONE = unchecked((int)0xffffffff);

    public const uint CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION = 1;
    public const uint CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC = 2;

    public const uint CF_CONVERT_FLAG_MARK_IN_SYNC = 1;

    public const uint CF_UPDATE_FLAG_MARK_IN_SYNC = 2;
    public const uint CF_UPDATE_FLAG_DEHYDRATE = 4;
    public const uint CF_UPDATE_FLAG_DISABLE_ON_DEMAND_POPULATION = 0x10;

    public const uint CF_OPEN_FILE_FLAG_EXCLUSIVE = 1;
    public const uint CF_OPEN_FILE_FLAG_WRITE_ACCESS = 2;

    public const int CF_OPERATION_TYPE_TRANSFER_DATA = 0;
    public const int CF_PLACEHOLDER_INFO_BASIC = 0;
    public const int CF_PIN_STATE_PINNED = 1;
    public const int CF_PIN_STATE_UNPINNED = 2;
    public const int CF_IN_SYNC_STATE_IN_SYNC = 1;
    public const uint CF_REVERT_FLAG_NONE = 0;

    public const int STATUS_SUCCESS = 0;
    public const int STATUS_UNSUCCESSFUL = unchecked((int)0xC0000001);
    public const int STATUS_CLOUD_FILE_UNSUCCESSFUL = unchecked((int)0xC000CF17);

    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    public const uint FILE_ATTRIBUTE_OFFLINE = 0x1000;
    public const uint FILE_ATTRIBUTE_PINNED = 0x80000;
    public const uint FILE_ATTRIBUTE_UNPINNED = 0x100000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    public const int HR_NOT_A_CLOUD_FILE = unchecked((int)0x80070178);

    // ---------------------------------------------------------------- structs
    [StructLayout(LayoutKind.Sequential)]
    public struct CF_HYDRATION_POLICY { public ushort Primary; public ushort Modifier; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_POPULATION_POLICY { public ushort Primary; public ushort Modifier; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_SYNC_POLICIES
    {
        public uint StructSize;
        public CF_HYDRATION_POLICY Hydration;
        public CF_POPULATION_POLICY Population;
        public uint InSync;
        public uint HardLink;
        public uint PlaceholderManagement;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CF_SYNC_REGISTRATION
    {
        public uint StructSize;
        public string ProviderName;
        public string ProviderVersion;
        public IntPtr SyncRootIdentity;
        public uint SyncRootIdentityLength;
        public IntPtr FileIdentity;
        public uint FileIdentityLength;
        public Guid ProviderId;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void CF_CALLBACK(IntPtr callbackInfo, IntPtr callbackParameters);

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_CALLBACK_REGISTRATION
    {
        public int Type;
        public IntPtr Callback;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_CALLBACK_INFO
    {
        public uint StructSize;
        public long ConnectionKey;
        public IntPtr CallbackContext;
        public IntPtr VolumeGuidName;
        public IntPtr VolumeDosName;
        public uint VolumeSerialNumber;
        public long SyncRootFileId;
        public IntPtr SyncRootIdentity;
        public uint SyncRootIdentityLength;
        public long FileId;
        public long FileSize;
        public IntPtr FileIdentity;
        public uint FileIdentityLength;
        public IntPtr NormalizedPath;
        public long TransferKey;
        public byte PriorityHint;
        public IntPtr CorrelationVector;
        public IntPtr ProcessInfo;
        public long RequestKey;
    }

    // CF_CALLBACK_PARAMETERS: ULONG ParamSize, then an 8-byte aligned union.
    public const int PARAM_FETCH_REQUIRED_OFFSET = 16;   // FetchData.RequiredFileOffset
    public const int PARAM_FETCH_REQUIRED_LENGTH = 24;   // FetchData.RequiredLength

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_OPERATION_INFO
    {
        public uint StructSize;
        public int Type;
        public long ConnectionKey;
        public long TransferKey;
        public IntPtr CorrelationVector;
        public IntPtr SyncStatus;
        public long RequestKey;
    }

    /// <summary>CF_OPERATION_PARAMETERS with the TransferData member of the union.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CF_OPERATION_PARAMETERS_TRANSFER_DATA
    {
        public uint ParamSize;
        public uint _pad;
        public uint Flags;
        public int CompletionStatus;
        public IntPtr Buffer;
        public long Offset;
        public long Length;
        public long _unionTail;            // the union's largest member is 40 bytes
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_FS_METADATA
    {
        public FILE_BASIC_INFO BasicInfo;
        public long FileSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_PLACEHOLDER_CREATE_INFO
    {
        public IntPtr RelativeFileName;
        public CF_FS_METADATA FsMetadata;
        public IntPtr FileIdentity;
        public uint FileIdentityLength;
        public uint Flags;
        public int Result;
        public long CreateUsn;
    }

    // ---------------------------------------------------------------- functions
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CfRegisterSyncRoot(string syncRootPath, ref CF_SYNC_REGISTRATION registration, ref CF_SYNC_POLICIES policies, uint flags);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CfUnregisterSyncRoot(string syncRootPath);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CfConnectSyncRoot(string syncRootPath, [In] CF_CALLBACK_REGISTRATION[] callbackTable, IntPtr callbackContext, uint connectFlags, out long connectionKey);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    public static extern int CfDisconnectSyncRoot(long connectionKey);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CfCreatePlaceholders(string baseDirectoryPath, [In, Out] CF_PLACEHOLDER_CREATE_INFO[] placeholderArray, uint placeholderCount, uint createFlags, out uint entriesProcessed);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    public static extern int CfExecute(ref CF_OPERATION_INFO opInfo, ref CF_OPERATION_PARAMETERS_TRANSFER_DATA opParams);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    public static extern int CfGetPlaceholderInfo(SafeFileHandle fileHandle, int infoClass, IntPtr infoBuffer, uint infoBufferLength, out uint returnedLength);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    public static extern int CfConvertToPlaceholder(IntPtr fileHandle, byte[] fileIdentity, uint fileIdentityLength, uint convertFlags, IntPtr convertUsn, IntPtr overlapped);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    public static extern int CfUpdatePlaceholder(IntPtr fileHandle, IntPtr fsMetadata, byte[]? fileIdentity, uint fileIdentityLength, IntPtr dehydrateRangeArray, uint dehydrateRangeCount, uint updateFlags, IntPtr updateUsn, IntPtr overlapped);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    public static extern int CfDehydratePlaceholder(IntPtr fileHandle, long startingOffset, long length, uint flags, IntPtr overlapped);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    public static extern int CfHydratePlaceholder(SafeFileHandle fileHandle, long startingOffset, long length, uint flags, IntPtr overlapped);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    public static extern int CfRevertPlaceholder(IntPtr fileHandle, uint revertFlags, IntPtr overlapped);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CfOpenFileWithOplock(string filePath, uint flags, out IntPtr protectedHandle);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    public static extern IntPtr CfGetWin32HandleFromProtectedHandle(IntPtr protectedHandle);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    public static extern void CfCloseHandle(IntPtr fileHandle);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    public static extern int CfReportProviderProgress(long connectionKey, long transferKey, long total, long completed);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    // Shell delete to the Recycle Bin with every prompt / error box off (this runs on background threads).
    public const uint FO_DELETE = 3;
    public const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_NOCONFIRMMKDIR = 0x200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;                  // double-NUL terminated
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

    public const uint WRITE_DAC = 0x40000;                // enough for the Cloud Files calls, and not subject to other apps' share modes
    public const uint FSCTL_READ_FILE_USN_DATA = 0x900EB;

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? inBuf, int inLen, byte[] outBuf, int outLen, out int returned, IntPtr overlapped);

    /// <summary>The file's current USN (changes whenever the file is modified), or 0 if the volume can't tell.</summary>
    public static long ReadUsn(SafeFileHandle h)
    {
        var input = new byte[] { 2, 0, 2, 0 };            // USN_RECORD_V2 only: MinMajorVersion = MaxMajorVersion = 2
        var buf = new byte[128];
        return DeviceIoControl(h, FSCTL_READ_FILE_USN_DATA, input, input.Length, buf, buf.Length, out var n, IntPtr.Zero) && n >= 32
            ? BitConverter.ToInt64(buf, 24) : 0;
    }

    public const uint FILE_READ_ATTRIBUTES = 0x80;
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_ALL = 7;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    /// <summary>"\\?\" form so long paths work with the native calls.</summary>
    public static string Long(string path) =>
        path.Length < 240 || path.StartsWith(@"\\?\") ? path : path.StartsWith(@"\\") ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    public static void Check(int hr, string what)
    {
        if (hr >= 0) return;
        var ex = Marshal.GetExceptionForHR(hr) ?? new IOException($"{what} failed (0x{hr:X8})");
        if (ex is System.Runtime.InteropServices.COMException)
            ex = new IOException($"{what} failed: {ex.Message} (0x{hr:X8})", ex);
        throw ex;
    }

    /// <summary>A handle from CfOpenFileWithOplock: exclusive, so it fails while another app has the file open.</summary>
    public sealed class OplockHandle : IDisposable
    {
        readonly IntPtr _protected;
        public IntPtr Win32 { get; }

        public OplockHandle(string path, bool exclusive)
        {
            var flags = CF_OPEN_FILE_FLAG_WRITE_ACCESS | (exclusive ? CF_OPEN_FILE_FLAG_EXCLUSIVE : 0);
            Check(CfOpenFileWithOplock(Long(path), flags, out _protected), "Opening the file");
            Win32 = CfGetWin32HandleFromProtectedHandle(_protected);
        }

        public void Dispose() => CfCloseHandle(_protected);
    }
}
