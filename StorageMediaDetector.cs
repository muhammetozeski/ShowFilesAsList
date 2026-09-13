namespace ShowFilesAsList;

enum StorageMediaKind { Unknown, SolidState, Spinning }

/// <summary>
/// Finds out whether the physical disk behind a path is solid-state or a spinning hard drive, so the classic
/// folder walk in <see cref="DirectoryScanner"/> knows whether reading many folders at once helps (solid-state,
/// where the drive can service many requests at once) or hurts (a spinning drive, where concurrent reads from
/// scattered folders just add seeks). Neither query needs administrator rights.
/// </summary>
static class StorageMediaDetector
{
    const uint VolumeDiskExtentsControlCode = 0x00560000; // IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS
    const uint StorageQueryPropertyControlCode = 0x002D1400; // IOCTL_STORAGE_QUERY_PROPERTY
    const uint SeekPenaltyPropertyId = 7; // StorageDeviceSeekPenaltyProperty
    const int DiskNumberOffset = 8;
    const int IncursSeekPenaltyOffset = 8;

    /// <returns>
    /// <see cref="StorageMediaKind.SolidState"/> or <see cref="StorageMediaKind.Spinning"/> for a path on a local
    /// fixed disk; <see cref="StorageMediaKind.Unknown"/> for anything the two IOCTLs cannot resolve (a network
    /// path, a virtual drive, or a permissions issue) so the caller can fall back to a conservative default.
    /// </returns>
    public static StorageMediaKind DetectForPath(string path)
    {
        if (Path.GetPathRoot(path) is not { Length: > 0 } root)
            return StorageMediaKind.Unknown;

        return TryGetDiskNumber(root[0], out int diskNumber) && TryGetIncursSeekPenalty(diskNumber, out bool incursSeekPenalty)
            ? incursSeekPenalty ? StorageMediaKind.Spinning : StorageMediaKind.SolidState
            : StorageMediaKind.Unknown;
    }

    static bool TryGetDiskNumber(char driveLetter, out int diskNumber)
    {
        diskNumber = 0;
        using Microsoft.Win32.SafeHandles.SafeFileHandle volumeHandle = NativeStorageApi.CreateFile(
            $@"\\.\{driveLetter}:", 0, NativeStorageApi.FileShareReadWrite, 0, NativeStorageApi.OpenExisting, 0, 0);
        if (volumeHandle.IsInvalid)
            return false;

        byte[] outBuffer = new byte[32];
        if (!NativeStorageApi.DeviceIoControl(volumeHandle, VolumeDiskExtentsControlCode, null, 0, outBuffer, (uint)outBuffer.Length, out _, 0))
            return false;

        diskNumber = BitConverter.ToInt32(outBuffer, DiskNumberOffset);
        return true;
    }

    static bool TryGetIncursSeekPenalty(int diskNumber, out bool incursSeekPenalty)
    {
        incursSeekPenalty = false;
        using Microsoft.Win32.SafeHandles.SafeFileHandle diskHandle = NativeStorageApi.CreateFile(
            $@"\\.\PhysicalDrive{diskNumber}", 0, NativeStorageApi.FileShareReadWrite, 0, NativeStorageApi.OpenExisting, 0, 0);
        if (diskHandle.IsInvalid)
            return false;

        byte[] query = new byte[12]; // STORAGE_PROPERTY_QUERY: PropertyId, QueryType(=0, PropertyStandardQuery), AdditionalParameters[1]
        BitConverter.GetBytes(SeekPenaltyPropertyId).CopyTo(query, 0);
        byte[] outBuffer = new byte[16];
        if (!NativeStorageApi.DeviceIoControl(diskHandle, StorageQueryPropertyControlCode, query, (uint)query.Length, outBuffer, (uint)outBuffer.Length, out _, 0))
            return false;

        incursSeekPenalty = outBuffer[IncursSeekPenaltyOffset] != 0;
        return true;
    }
}
