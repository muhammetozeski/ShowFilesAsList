using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ShowFilesAsList.Ntfs;

using ShowFilesAsList; // DirectoryScanner and ScannedDirectory: the fallback path for reparse points, and the shared result type

/// <summary>
/// Scans a folder tree by reading the whole NTFS Master File Table once instead of opening every folder in it:
/// a handful of large sequential disk reads and one in-memory pass, versus one small disk operation per folder.
/// Requires administrator rights and a local NTFS volume; <see cref="Program"/> falls back to
/// <see cref="DirectoryScanner"/> when either is not the case.
/// </summary>
/// <param name="reportProgress">Receives the total size in bytes of the scanned files once the tree is built.</param>
sealed class MftVolumeScanner(Action<long>? reportProgress = null)
{
    const int FileIdInfoClass = 18;
    const int FileIdInfoBufferSize = 24; // VolumeSerialNumber (8 bytes) + FILE_ID_128 (16 bytes)
    const long RecordNumberMask = 0x0000FFFFFFFFFFFF;

    /// <exception cref="UnauthorizedAccessException">The process is not elevated enough to read the volume's raw bytes.</exception>
    /// <exception cref="IOException"><paramref name="rootPath"/> or the volume could not be read.</exception>
    public ScannedDirectory Scan(string rootPath)
    {
        char driveLetter = char.ToUpperInvariant(Path.GetPathRoot(rootPath) is { Length: > 0 } rootPrefix ? rootPrefix[0] : throw new IOException($"'{rootPath}' has no drive letter"));
        NtfsMasterFileTable masterFileTable = NtfsVolumeAccessor.ReadMasterFileTable(driveLetter);

        (Dictionary<long, MftFileRecord> recordsByNumber, Dictionary<long, long> extensionRecordDataSizes) = ParseAllRecords(masterFileTable);
        Dictionary<long, List<(MftFileRecord Record, string Name)>> childrenByParent = BuildChildIndex(recordsByNumber);

        long rootRecordNumber = ResolveRecordNumber(rootPath);
        ScannedDirectory rootDirectory = BuildTree(rootRecordNumber, rootPath, rootPath, recordsByNumber, childrenByParent, extensionRecordDataSizes, [rootRecordNumber]);

        reportProgress?.Invoke(rootDirectory.Size);
        return rootDirectory;
    }

    /// <summary>
    /// Parses every record of the table, using all available processors since each record's own bytes are
    /// independent of every other record's. Returns the in-use, non-extension records that represent a file or
    /// folder, plus a lookup of the unnamed $DATA size found in every extension record — the overflow records a
    /// large or fragmented file's $ATTRIBUTE_LIST can point its real $DATA attribute at.
    /// </summary>
    static (Dictionary<long, MftFileRecord> RecordsByNumber, Dictionary<long, long> ExtensionRecordDataSizes) ParseAllRecords(NtfsMasterFileTable masterFileTable)
    {
        int recordLength = masterFileTable.BytesPerFileRecordSegment;
        int recordCount = masterFileTable.Bytes.Length / recordLength;
        ConcurrentDictionary<long, MftFileRecord> records = new();
        ConcurrentDictionary<long, long> extensionRecordDataSizes = new();

        Parallel.For(0, recordCount, recordNumber =>
        {
            int recordOffset = recordNumber * recordLength;
            if (!MftRecordParser.HasFileSignature(masterFileTable.Bytes, recordOffset))
                return;
            if (!MftRecordParser.ApplyFixup(masterFileTable.Bytes, recordOffset, recordLength, masterFileTable.BytesPerSector))
                return; // the update sequence check failed; treat as unreadable rather than trust a half-fixed-up record

            if (MftRecordParser.Parse(masterFileTable.Bytes, recordOffset, recordLength, recordNumber) is { } record)
            {
                records[recordNumber] = record;
                return;
            }

            if (MftRecordParser.TryGetExtensionRecordDataSize(masterFileTable.Bytes, recordOffset, recordLength) is { } extensionDataSize)
                extensionRecordDataSizes[recordNumber] = extensionDataSize;
        });

        return (new Dictionary<long, MftFileRecord>(records), new Dictionary<long, long>(extensionRecordDataSizes));
    }

    /// <summary>Inverts the record set into a parent record number to (child, name) lookup, ready for a top-down tree walk.</summary>
    static Dictionary<long, List<(MftFileRecord Record, string Name)>> BuildChildIndex(Dictionary<long, MftFileRecord> recordsByNumber)
    {
        Dictionary<long, List<(MftFileRecord Record, string Name)>> childrenByParent = [];
        foreach (MftFileRecord record in recordsByNumber.Values)
        {
            foreach ((long parentRecordNumber, string name) in record.DistinctParentNames())
            {
                // The volume root names itself as its own parent — NTFS convention, not a mistake — which would
                // otherwise make BuildTree recurse into the root forever as "its own child".
                if (parentRecordNumber == record.RecordNumber)
                    continue;

                if (!childrenByParent.TryGetValue(parentRecordNumber, out List<(MftFileRecord Record, string Name)>? children))
                    childrenByParent[parentRecordNumber] = children = [];
                children.Add((record, name));
            }
        }
        return childrenByParent;
    }

    /// <summary>
    /// Builds one folder of the result tree from the parsed record set, recursing into subfolders. A folder that
    /// carries a reparse point (a junction or similar redirect) has no children of its own in the MFT — whatever
    /// it points to is scanned the ordinary way, via <see cref="DirectoryScanner"/>, so the result matches what
    /// Explorer and the classic scanner both show there.
    /// </summary>
    /// <param name="recordNumber">MFT record number of the folder to build.</param>
    /// <param name="fullPath">Full path of the folder, used to open it directly should <paramref name="recordsByNumber"/> turn out not to have it, or a reparse point need the fallback scanner.</param>
    /// <param name="displayName">The name stored in the result's <see cref="ScannedDirectory.Name"/>.</param>
    /// <param name="recordsByNumber">Every parsed record, keyed by its MFT record number.</param>
    /// <param name="childrenByParent">The lookup <see cref="BuildChildIndex"/> built from <paramref name="recordsByNumber"/>.</param>
    /// <param name="extensionRecordDataSizes">The lookup <see cref="ParseAllRecords"/> built for file sizes a $ATTRIBUTE_LIST moved out of their base record.</param>
    /// <param name="ancestorRecordNumbers">
    /// Every record already being built higher up this same branch, <paramref name="recordNumber"/> included.
    /// <see cref="BuildChildIndex"/> already removes the one cycle NTFS itself creates (the root naming itself as
    /// its own parent); this is the safety net against a cycle from anywhere else — corruption, or a case this
    /// reader has not seen — that would otherwise recurse until the stack overflows.
    /// </param>
    static ScannedDirectory BuildTree(long recordNumber, string fullPath, string displayName, Dictionary<long, MftFileRecord> recordsByNumber,
        Dictionary<long, List<(MftFileRecord Record, string Name)>> childrenByParent, Dictionary<long, long> extensionRecordDataSizes, HashSet<long> ancestorRecordNumbers)
    {
        if (!recordsByNumber.TryGetValue(recordNumber, out MftFileRecord? record))
            return new ScannedDirectory(displayName) { Error = ("This path's record was not found while reading the Master File Table.", fullPath) };

        if (record.HasReparsePoint)
        {
            // DirectoryScanner.Scan names its result after the path it was given, which is right for an actual scan
            // root but wrong here: this folder is a child of another one, so it needs the plain child name that the
            // rest of the tree uses, not the full path this fallback scan happened to start from.
            ScannedDirectory scanned = new DirectoryScanner().Scan(fullPath);
            ScannedDirectory renamed = new(displayName) { Size = scanned.Size, Error = scanned.Error };
            renamed.Subdirectories.AddRange(scanned.Subdirectories);
            renamed.Files.AddRange(scanned.Files);
            return renamed;
        }

        ScannedDirectory directory = new(displayName);
        foreach ((MftFileRecord childRecord, string childName) in childrenByParent.GetValueOrDefault(recordNumber, []))
        {
            string childPath = Path.Join(fullPath, childName);
            if (childRecord.IsDirectory)
            {
                if (!ancestorRecordNumbers.Add(childRecord.RecordNumber))
                {
                    directory.Subdirectories.Add(new ScannedDirectory(childName) { Error = ("This folder is its own ancestor in the Master File Table; skipped to avoid scanning it forever.", childPath) });
                    continue;
                }

                ScannedDirectory subdirectory = BuildTree(childRecord.RecordNumber, childPath, childName, recordsByNumber, childrenByParent, extensionRecordDataSizes, ancestorRecordNumbers);
                ancestorRecordNumbers.Remove(childRecord.RecordNumber);

                directory.Subdirectories.Add(subdirectory);
                directory.Size += subdirectory.Size;
            }
            else
            {
                long fileSize = ResolveFileSize(childRecord, childPath, extensionRecordDataSizes);
                directory.Files.Add((childName, fileSize));
                directory.Size += fileSize;
            }
        }
        return directory;
    }

    /// <summary>
    /// Most files carry their own $DATA size; a file with either very many attributes or very many fragments can
    /// have NTFS move $DATA to a separate extension record instead, found through the base record's
    /// $ATTRIBUTE_LIST. The rare file whose own $ATTRIBUTE_LIST does not fit its record either falls back to an
    /// ordinary file-size query, since following that list would mean reading its own, possibly scattered, data runs.
    /// </summary>
    static long ResolveFileSize(MftFileRecord record, string fullPath, Dictionary<long, long> extensionRecordDataSizes)
    {
        if (record.DataSizeFoundLocally)
            return record.DataSize;

        if (record.ExternalDataRecordNumber is long externalRecordNumber && extensionRecordDataSizes.TryGetValue(externalRecordNumber, out long externalSize))
            return externalSize;

        if (record.HasUnresolvedNonResidentAttributeList)
        {
            try { return new FileInfo(fullPath).Length; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return 0; }
        }

        return 0; // a genuinely empty file with no $DATA attribute at all, which NTFS allows
    }

    /// <summary>Resolves the MFT record number backing an existing file or folder path.</summary>
    static long ResolveRecordNumber(string path)
    {
        using SafeFileHandle handle = NativeStorageApi.CreateFile(path, NativeStorageApi.GenericRead, NativeStorageApi.FileShareReadWrite, 0, NativeStorageApi.OpenExisting, NativeStorageApi.FileFlagBackupSemantics, 0);
        if (handle.IsInvalid)
            throw new IOException($"could not open '{path}'", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

        byte[] fileIdInfo = new byte[FileIdInfoBufferSize];
        if (!NativeStorageApi.GetFileInformationByHandleEx(handle, FileIdInfoClass, fileIdInfo, (uint)fileIdInfo.Length))
            throw new IOException($"could not read the file ID of '{path}'", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

        return BitConverter.ToInt64(fileIdInfo, 8) & RecordNumberMask;
    }
}
