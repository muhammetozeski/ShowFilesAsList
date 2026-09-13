using System.Runtime.InteropServices;

namespace ShowFilesAsList.Ntfs;

/// <summary>The whole $MFT of one NTFS volume, read into memory, with the geometry needed to parse its records.</summary>
sealed record NtfsMasterFileTable(byte[] Bytes, int BytesPerFileRecordSegment, int BytesPerSector);

/// <summary>
/// Opens an NTFS volume as a raw device and reads its Master File Table in a handful of large sequential
/// reads, instead of the many small per-folder reads an ordinary directory walk needs. Requires administrator
/// rights: reading a volume's raw bytes is only granted to an elevated process.
/// </summary>
static class NtfsVolumeAccessor
{
    const uint NtfsVolumeDataControlCode = 0x00090064; // FSCTL_GET_NTFS_VOLUME_DATA
    const int NtfsVolumeDataBufferSize = 128;
    const int BytesPerSectorOffset = 40;
    const int BytesPerFileRecordSegmentOffset = 48;
    const int MftStartLcnOffset = 64;

    /// <exception cref="UnauthorizedAccessException">The current process cannot open the volume for raw reading (not elevated).</exception>
    /// <exception cref="IOException">The volume could not be queried or read.</exception>
    public static NtfsMasterFileTable ReadMasterFileTable(char driveLetter)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle volumeHandle = NativeStorageApi.CreateFile(
            $@"\\.\{driveLetter}:", NativeStorageApi.GenericRead, NativeStorageApi.FileShareReadWrite, 0, NativeStorageApi.OpenExisting, 0, 0);
        if (volumeHandle.IsInvalid)
            ThrowForLastWin32Error($"volume {driveLetter}: could not be opened for raw reading");

        byte[] volumeDataBuffer = new byte[NtfsVolumeDataBufferSize];
        if (!NativeStorageApi.DeviceIoControl(volumeHandle, NtfsVolumeDataControlCode, null, 0, volumeDataBuffer, (uint)volumeDataBuffer.Length, out _, 0))
            ThrowForLastWin32Error($"volume {driveLetter}: could not read the NTFS volume data (is it actually NTFS?)");

        int bytesPerSector = BitConverter.ToInt32(volumeDataBuffer, BytesPerSectorOffset);
        int bytesPerFileRecordSegment = BitConverter.ToInt32(volumeDataBuffer, BytesPerFileRecordSegmentOffset);
        long mftStartLcn = BitConverter.ToInt64(volumeDataBuffer, MftStartLcnOffset);
        int bytesPerCluster = (int)(BitConverter.ToUInt32(volumeDataBuffer, 44));

        using FileStream volumeStream = new(volumeHandle, FileAccess.Read, bufferSize: 1, isAsync: false); // no benefit from FileStream's own buffer; every read below is already a large explicit block

        byte[] mftRecordZero = new byte[bytesPerFileRecordSegment];
        volumeStream.Seek(mftStartLcn * bytesPerCluster, SeekOrigin.Begin);
        volumeStream.ReadExactly(mftRecordZero);
        if (!MftRecordParser.ApplyFixup(mftRecordZero, 0, bytesPerFileRecordSegment, bytesPerSector))
            throw new IOException($"volume {driveLetter}: the $MFT's own record is corrupt (update sequence check failed)");

        List<NtfsDataRun> mftDataRuns = MftRecordParser.FindUnnamedDataRuns(mftRecordZero, 0, bytesPerFileRecordSegment);
        if (mftDataRuns.Count == 0)
            throw new IOException($"volume {driveLetter}: could not find the $MFT's own data runs");

        long totalMftBytes = (mftDataRuns[^1].StartVcn + mftDataRuns[^1].ClusterCount) * bytesPerCluster;
        byte[] mft = new byte[totalMftBytes];
        foreach (NtfsDataRun run in mftDataRuns)
        {
            volumeStream.Seek(run.StartLcn * bytesPerCluster, SeekOrigin.Begin);
            volumeStream.ReadExactly(mft, (int)(run.StartVcn * bytesPerCluster), (int)(run.ClusterCount * bytesPerCluster));
        }

        return new NtfsMasterFileTable(mft, bytesPerFileRecordSegment, bytesPerSector);
    }

    const int AccessDeniedWin32Error = 5;

    static void ThrowForLastWin32Error(string what)
    {
        int errorCode = Marshal.GetLastWin32Error();
        if (errorCode == AccessDeniedWin32Error)
            throw new UnauthorizedAccessException(what);
        throw new IOException(what, Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
    }
}
