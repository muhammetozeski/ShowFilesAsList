using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ShowFilesAsList.Ntfs;

using ShowFilesAsList; // ScannedDirectory: the result type this and DirectoryScanner both build

/// <summary>Everything a whole scan's <see cref="MftVolumeScanner.BuildTree"/> calls share, bundled so passing it down through the recursion is one parameter, not four.</summary>
sealed record MftScanContext(
    ConcurrentDictionary<long, MftFileRecord> RecordsByNumber,
    Dictionary<long, List<(MftFileRecord Record, string Name)>> ChildrenByParent,
    ConcurrentDictionary<long, long> ExtensionRecordDataSizes,
    ScanProgress? Progress);

/// <summary>
/// Scans a folder tree by reading the whole NTFS Master File Table once instead of opening every folder in it:
/// a handful of large sequential disk reads and one in-memory pass, versus one small disk operation per folder.
/// Requires administrator rights and a local NTFS volume; <see cref="Program"/> falls back to
/// <see cref="DirectoryScanner"/> when either is not the case.
/// </summary>
/// <param name="progress">Reports the phases (reading, parsing, indexing, tree building) as they run; may be <see langword="null"/>.</param>
sealed class MftVolumeScanner(ScanProgress? progress = null)
{
    const int FileIdInfoClass = 18;
    const int FileIdInfoBufferSize = 24; // VolumeSerialNumber (8 bytes) + FILE_ID_128 (16 bytes)
    const long RecordNumberMask = 0x0000FFFFFFFFFFFF;

    /// <exception cref="UnauthorizedAccessException">The process is not elevated enough to read the volume's raw bytes.</exception>
    /// <exception cref="IOException"><paramref name="rootPath"/> or the volume could not be read.</exception>
    public ScannedDirectory Scan(string rootPath)
    {
        char driveLetter = char.ToUpperInvariant(Path.GetPathRoot(rootPath) is { Length: > 0 } rootPrefix ? rootPrefix[0] : throw new IOException($"'{rootPath}' has no drive letter"));
        NtfsMasterFileTable masterFileTable = NtfsVolumeAccessor.ReadMasterFileTable(driveLetter, progress);

        (ConcurrentDictionary<long, MftFileRecord> recordsByNumber, ConcurrentDictionary<long, long> extensionRecordDataSizes) = ParseAllRecords(masterFileTable, progress);
        Dictionary<long, List<(MftFileRecord Record, string Name)>> childrenByParent = BuildChildIndex(recordsByNumber, progress);

        long rootRecordNumber = ResolveRecordNumber(rootPath);
        progress?.BeginPhase(ScanPhaseKind.TreeCounts, "Building the folder tree");
        MftScanContext context = new(recordsByNumber, childrenByParent, extensionRecordDataSizes, progress);
        return BuildTree(rootRecordNumber, rootPath, rootPath, context, [rootRecordNumber]);
    }

    /// <summary>
    /// Parses every record of the table, using all available processors since each record's own bytes are
    /// independent of every other record's. Returns the in-use, non-extension records that represent a file or
    /// folder, plus a lookup of the unnamed $DATA size found in every extension record — the overflow records a
    /// large or fragmented file's $ATTRIBUTE_LIST can point its real $DATA attribute at. Both stay as the
    /// concurrent collections they were built as: the record count runs into the millions on a large drive, and
    /// nothing downstream writes to them again, so copying them into plain Dictionaries first would only add an
    /// extra full pass over every entry for no benefit.
    /// </summary>
    static (ConcurrentDictionary<long, MftFileRecord> RecordsByNumber, ConcurrentDictionary<long, long> ExtensionRecordDataSizes) ParseAllRecords(NtfsMasterFileTable masterFileTable, ScanProgress? progress)
    {
        int recordLength = masterFileTable.BytesPerFileRecordSegment;
        int recordCount = masterFileTable.Bytes.Length / recordLength;
        ConcurrentDictionary<long, MftFileRecord> records = new();
        ConcurrentDictionary<long, long> extensionRecordDataSizes = new();

        // Partitioning into contiguous ranges (rather than Parallel.For over each index) means one plain inner loop
        // per range and a single progress update when the range finishes — so the shared progress counter is touched
        // a few dozen times across the whole parse instead of over a million, never becoming a point of contention.
        progress?.BeginPhase(ScanPhaseKind.CountBar, "Parsing file records", recordCount);
        Parallel.ForEach(Partitioner.Create(0, recordCount), range =>
        {
            for (int recordNumber = range.Item1; recordNumber < range.Item2; recordNumber++)
            {
                int recordOffset = recordNumber * recordLength;
                if (!MftRecordParser.HasFileSignature(masterFileTable.Bytes, recordOffset))
                    continue;
                if (!MftRecordParser.ApplyFixup(masterFileTable.Bytes, recordOffset, recordLength, masterFileTable.BytesPerSector))
                    continue; // the update sequence check failed; treat as unreadable rather than trust a half-fixed-up record

                if (MftRecordParser.Parse(masterFileTable.Bytes, recordOffset, recordLength, recordNumber) is { } record)
                    records[recordNumber] = record;
                else if (MftRecordParser.TryGetExtensionRecordDataSize(masterFileTable.Bytes, recordOffset, recordLength) is { } extensionDataSize)
                    extensionRecordDataSizes[recordNumber] = extensionDataSize;
            }
            progress?.AdvanceBar(range.Item2 - range.Item1);
        });

        return (records, extensionRecordDataSizes);
    }

    const int IndexProgressBatch = 8192;

    /// <summary>Inverts the record set into a parent record number to (child, name) lookup, ready for a top-down tree walk.</summary>
    static Dictionary<long, List<(MftFileRecord Record, string Name)>> BuildChildIndex(ConcurrentDictionary<long, MftFileRecord> recordsByNumber, ScanProgress? progress)
    {
        progress?.BeginPhase(ScanPhaseKind.CountBar, "Building the record index", recordsByNumber.Count);
        Dictionary<long, List<(MftFileRecord Record, string Name)>> childrenByParent = new(recordsByNumber.Count);
        long sinceReport = 0;
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

            if (++sinceReport >= IndexProgressBatch)
            {
                progress?.AdvanceBar(sinceReport);
                sinceReport = 0;
            }
        }
        progress?.AdvanceBar(sinceReport);
        return childrenByParent;
    }

    /// <summary>
    /// Builds one folder of the result tree from the parsed record set, recursing into subfolders. A folder that
    /// carries a reparse point (a junction, a symbolic link, or any other kind of redirect) is listed under
    /// <see cref="ScannedDirectory.Junctions"/> instead: it has no content of its own — a reparse point is a
    /// redirect, and Windows resolves what it points to on demand, not something this reader recurses into — and
    /// its target is often reachable under its own, real location elsewhere in the tree too, so counting it again
    /// here would double it.
    /// </summary>
    /// <param name="recordNumber">MFT record number of the folder to build.</param>
    /// <param name="fullPath">Full path of the folder, used only if it turns out not to be in <paramref name="context"/>.</param>
    /// <param name="displayName">The name stored in the result's <see cref="ScannedDirectory.Name"/>.</param>
    /// <param name="context">The parsed record set and the lookups built from it.</param>
    /// <param name="ancestorRecordNumbers">
    /// Every record already being built higher up this same branch, <paramref name="recordNumber"/> included.
    /// <see cref="BuildChildIndex"/> already removes the one cycle NTFS itself creates (the root naming itself as
    /// its own parent); this is the safety net against a cycle from anywhere else — corruption, or a case this
    /// reader has not seen — that would otherwise recurse until the stack overflows.
    /// </param>
    static ScannedDirectory BuildTree(long recordNumber, string fullPath, string displayName, MftScanContext context, HashSet<long> ancestorRecordNumbers)
    {
        if (!context.RecordsByNumber.TryGetValue(recordNumber, out MftFileRecord? record))
            return new ScannedDirectory(displayName) { Error = ("This path's record was not found while reading the Master File Table.", fullPath) };

        ScannedDirectory directory = new(displayName);
        foreach ((MftFileRecord childRecord, string childName) in context.ChildrenByParent.GetValueOrDefault(recordNumber, []))
        {
            string childPath = Path.Join(fullPath, childName);
            if (childRecord.HasReparsePoint)
            {
                directory.Junctions.Add((childName, childRecord.ReparseTargetPath ?? ""));
            }
            else if (childRecord.IsDirectory)
            {
                if (!ancestorRecordNumbers.Add(childRecord.RecordNumber))
                {
                    directory.Subdirectories.Add(new ScannedDirectory(childName) { Error = ("This folder is its own ancestor in the Master File Table; skipped to avoid scanning it forever.", childPath) });
                    continue;
                }

                ScannedDirectory subdirectory = BuildTree(childRecord.RecordNumber, childPath, childName, context, ancestorRecordNumbers);
                ancestorRecordNumbers.Remove(childRecord.RecordNumber);

                directory.Subdirectories.Add(subdirectory);
                directory.Size += subdirectory.Size;
            }
            else
            {
                long fileSize = ResolveFileSize(childRecord, childPath, context.ExtensionRecordDataSizes);
                directory.Files.Add((childName, fileSize));
                directory.Size += fileSize;
            }
        }

        context.Progress?.AddTreeCounts(directory.Files.Count, directory.Junctions.Count);
        return directory;
    }

    /// <summary>
    /// Most files carry their own $DATA size; a file with either very many attributes or very many fragments can
    /// have NTFS move $DATA to a separate extension record instead, found through the base record's
    /// $ATTRIBUTE_LIST. The rare file whose own $ATTRIBUTE_LIST does not fit its record either falls back to an
    /// ordinary file-size query, since following that list would mean reading its own, possibly scattered, data runs.
    /// </summary>
    static long ResolveFileSize(MftFileRecord record, string fullPath, ConcurrentDictionary<long, long> extensionRecordDataSizes)
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
