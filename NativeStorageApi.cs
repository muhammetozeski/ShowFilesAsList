using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ShowFilesAsList;

/// <summary>
/// The raw Win32 calls <see cref="StorageMediaDetector"/> and the NTFS reader in <see cref="Ntfs"/> both need
/// to open a volume or a physical disk and query or read it directly, bypassing the ordinary file APIs.
/// </summary>
static partial class NativeStorageApi
{
    public const uint GenericRead = 0x80000000;
    public const uint FileShareReadWrite = 0x00000003;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, byte[]? inBuffer, uint inBufferSize, byte[] outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass, byte[] outBuffer, uint outBufferSize);
}
