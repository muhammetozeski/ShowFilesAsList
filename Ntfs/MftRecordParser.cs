namespace ShowFilesAsList.Ntfs;

/// <summary>
/// Parses one 1024-byte (typically) NTFS MFT record out of a buffer: applies the update sequence fixup,
/// walks its attributes and builds an <see cref="MftFileRecord"/>. Works equally on a lone record (used to
/// read the $MFT's own record 0) and on a record embedded at some offset inside the whole MFT buffer.
/// </summary>
static class MftRecordParser
{
    const uint FileNameAttributeType = 0x30;
    const uint AttributeListAttributeType = 0x20;
    const uint DataAttributeType = 0x80;
    const uint ReparsePointAttributeType = 0xC0;
    const uint EndOfAttributesMarker = 0xFFFFFFFF;
    const ushort InUseFlag = 0x0001;
    const ushort DirectoryFlag = 0x0002;
    const long RecordNumberMask = 0x0000FFFFFFFFFFFF;

    // The two reparse tags with a known PathBuffer layout; everything else (cloud placeholders, deduplication,
    // WIM backing, AppExecLink, ...) is left with no target rather than risk misreading a different layout.
    const uint MountPointReparseTag = 0xA0000003;
    const uint SymbolicLinkReparseTag = 0xA000000C;
    const string NtNamespacePrefix = @"\??\";

    /// <returns><see langword="true"/> when <paramref name="buffer"/> at <paramref name="recordOffset"/> starts with the "FILE" signature.</returns>
    public static bool HasFileSignature(byte[] buffer, int recordOffset) =>
        buffer[recordOffset] == (byte)'F' && buffer[recordOffset + 1] == (byte)'I' && buffer[recordOffset + 2] == (byte)'L' && buffer[recordOffset + 3] == (byte)'E';

    /// <summary>
    /// Applies the NTFS update sequence fixup in place: every sector of the record has its real last two bytes
    /// swapped out for a check value when the volume is written, with the real bytes stashed in the update
    /// sequence array; this restores them.
    /// </summary>
    /// <returns><see langword="false"/> when a sector's check value does not match, meaning the record is corrupt.</returns>
    public static bool ApplyFixup(byte[] buffer, int recordOffset, int recordLength, int bytesPerSector)
    {
        ushort usaOffset = BitConverter.ToUInt16(buffer, recordOffset + 4);
        ushort usaCount = BitConverter.ToUInt16(buffer, recordOffset + 6);
        if (usaCount == 0)
            return true;

        ushort updateSequenceNumber = BitConverter.ToUInt16(buffer, recordOffset + usaOffset);
        for (int sector = 0; sector < usaCount - 1; sector++)
        {
            int sectorEndOffset = recordOffset + (sector + 1) * bytesPerSector - 2;
            if (sectorEndOffset + 2 > recordOffset + recordLength)
                break;
            if (BitConverter.ToUInt16(buffer, sectorEndOffset) != updateSequenceNumber)
                return false;

            int originalBytesOffset = recordOffset + usaOffset + 2 + sector * 2;
            buffer[sectorEndOffset] = buffer[originalBytesOffset];
            buffer[sectorEndOffset + 1] = buffer[originalBytesOffset + 1];
        }
        return true;
    }

    /// <summary>
    /// Parses <paramref name="buffer"/> at <paramref name="recordOffset"/> into an <see cref="MftFileRecord"/>.
    /// Call <see cref="ApplyFixup"/> first; this does not apply it itself, so a caller who already validated
    /// and fixed up a batch of records does not pay for it twice.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the record is not in use, is an extension record holding overflow attributes
    /// for another record (identified by its own <c>BaseFileRecordSegment</c> being non-zero), or does not carry
    /// the "FILE" signature.
    /// </returns>
    public static MftFileRecord? Parse(byte[] buffer, int recordOffset, int recordLength, long recordNumber)
    {
        if (!HasFileSignature(buffer, recordOffset))
            return null;

        ushort flags = BitConverter.ToUInt16(buffer, recordOffset + 22);
        if ((flags & InUseFlag) == 0)
            return null;

        ulong baseFileRecordSegment = BitConverter.ToUInt64(buffer, recordOffset + 32);
        if (baseFileRecordSegment != 0)
            return null; // an extension record holding overflow attributes for another record, not a file or folder of its own

        bool isDirectory = (flags & DirectoryFlag) != 0;
        bool hasReparsePoint = false;
        string? reparseTargetPath = null;
        long dataSize = 0;
        bool dataSizeFoundLocally = false;
        long? externalDataRecordNumber = null;
        bool hasUnresolvedNonResidentAttributeList = false;
        List<MftFileNameAttribute> names = [];

        foreach ((uint type, int attributeOffset, _) in EnumerateAttributes(buffer, recordOffset, recordLength))
        {
            if (type == ReparsePointAttributeType)
            {
                hasReparsePoint = true;
                reparseTargetPath = TryReadReparseTargetPath(buffer, attributeOffset);
            }
            else if (type == FileNameAttributeType)
            {
                if (buffer[attributeOffset + 8] != 0) // non-resident $FILE_NAME never happens; skip defensively rather than misread
                    continue;

                ushort valueOffset = BitConverter.ToUInt16(buffer, attributeOffset + 20);
                int valueStart = attributeOffset + valueOffset;
                ulong parentReference = BitConverter.ToUInt64(buffer, valueStart);
                long parentRecordNumber = (long)(parentReference & RecordNumberMask);
                byte nameLengthChars = buffer[valueStart + 64];
                byte nameNamespace = buffer[valueStart + 65];
                string name = System.Text.Encoding.Unicode.GetString(buffer, valueStart + 66, nameLengthChars * 2);
                names.Add(new MftFileNameAttribute(parentRecordNumber, name, nameNamespace));
            }
            else if (type == DataAttributeType && !isDirectory && IsUnnamedDataAttribute(buffer, attributeOffset))
            {
                dataSize = ReadDataAttributeSize(buffer, attributeOffset);
                dataSizeFoundLocally = true;
            }
            else if (type == AttributeListAttributeType && !isDirectory)
            {
                if (buffer[attributeOffset + 8] != 0)
                {
                    // The list itself does not fit in this record and needs its own, possibly scattered, data runs to
                    // read — rare enough (an extreme number of attributes or fragments) that MftVolumeScanner falls
                    // back to a plain file-size query for this one file instead of following it.
                    hasUnresolvedNonResidentAttributeList = true;
                    continue;
                }

                foreach ((uint entryType, long entryStartingVcn, long entryRecordNumber) in EnumerateResidentAttributeListEntries(buffer, attributeOffset))
                {
                    // A heavily fragmented $DATA can itself be split across several extension records, one per run of
                    // virtual clusters; only the first of those (StartingVCN 0) carries the real, allocated and
                    // initialized sizes, the same way only the first record of any attribute does.
                    if (entryType == DataAttributeType && entryStartingVcn == 0 && entryRecordNumber != recordNumber)
                        externalDataRecordNumber = entryRecordNumber;
                }
            }
        }

        return new MftFileRecord
        {
            RecordNumber = recordNumber,
            IsDirectory = isDirectory,
            HasReparsePoint = hasReparsePoint,
            ReparseTargetPath = reparseTargetPath,
            DataSize = dataSize,
            DataSizeFoundLocally = dataSizeFoundLocally,
            ExternalDataRecordNumber = dataSizeFoundLocally ? null : externalDataRecordNumber,
            HasUnresolvedNonResidentAttributeList = hasUnresolvedNonResidentAttributeList,
            Names = names,
        };
    }

    /// <summary>
    /// Reads the target path out of a resident $REPARSE_POINT attribute's <c>REPARSE_DATA_BUFFER</c> value —
    /// the substitute name, which is always the full, authoritative target, unlike the print name, which is
    /// sometimes only a shorter display form.
    /// </summary>
    /// <returns>The target path, or <see langword="null"/> for a non-resident attribute or an unrecognized reparse tag.</returns>
    static string? TryReadReparseTargetPath(byte[] buffer, int attributeOffset)
    {
        if (buffer[attributeOffset + 8] != 0) // non-resident: not expected for a directory junction or symlink's small target path
            return null;

        ushort valueOffset = BitConverter.ToUInt16(buffer, attributeOffset + 20);
        int valueStart = attributeOffset + valueOffset;
        uint reparseTag = BitConverter.ToUInt32(buffer, valueStart);

        // Both layouts share the same first four fields; a symbolic link's buffer has one extra 4-byte Flags
        // field before its path buffer that a mount point's does not.
        int pathBufferStart = reparseTag switch
        {
            MountPointReparseTag => valueStart + 16,
            SymbolicLinkReparseTag => valueStart + 20,
            _ => -1,
        };
        if (pathBufferStart < 0)
            return null;

        ushort substituteNameOffset = BitConverter.ToUInt16(buffer, valueStart + 8);
        ushort substituteNameLength = BitConverter.ToUInt16(buffer, valueStart + 10);
        string target = System.Text.Encoding.Unicode.GetString(buffer, pathBufferStart + substituteNameOffset, substituteNameLength);
        return target.StartsWith(NtNamespacePrefix, StringComparison.Ordinal) ? target[NtNamespacePrefix.Length..] : target;
    }

    /// <summary>
    /// Reads the unnamed $DATA attribute of an extension record — a record that itself holds no name of its own,
    /// only overflow attributes for whichever base record's $ATTRIBUTE_LIST points at it.
    /// </summary>
    /// <returns>The size in bytes, or <see langword="null"/> when this record carries no such attribute.</returns>
    public static long? TryGetExtensionRecordDataSize(byte[] buffer, int recordOffset, int recordLength)
    {
        foreach ((uint type, int attributeOffset, _) in EnumerateAttributes(buffer, recordOffset, recordLength))
        {
            if (type == DataAttributeType && IsUnnamedDataAttribute(buffer, attributeOffset))
                return ReadDataAttributeSize(buffer, attributeOffset);
        }
        return null;
    }

    /// <summary>
    /// Finds the data runs of the unnamed, non-resident $DATA attribute of the record at <paramref name="recordOffset"/>.
    /// Used only to locate the $MFT file's own content (record 0) before the rest of the MFT can be read.
    /// </summary>
    public static List<NtfsDataRun> FindUnnamedDataRuns(byte[] buffer, int recordOffset, int recordLength)
    {
        foreach ((uint type, int attributeOffset, _) in EnumerateAttributes(buffer, recordOffset, recordLength))
        {
            if (type != DataAttributeType || !IsUnnamedDataAttribute(buffer, attributeOffset))
                continue;
            if (buffer[attributeOffset + 8] == 0) // resident: the whole $MFT can never be resident
                return [];

            ushort dataRunsOffset = BitConverter.ToUInt16(buffer, attributeOffset + 32);
            return NtfsRunListParser.Parse(buffer, attributeOffset + dataRunsOffset);
        }
        return [];
    }

    static bool IsUnnamedDataAttribute(byte[] buffer, int attributeOffset) => buffer[attributeOffset + 9] == 0; // NameLength == 0; a named one is an alternate data stream, not the file's main content

    static long ReadDataAttributeSize(byte[] buffer, int attributeOffset) => buffer[attributeOffset + 8] == 0 // NonResident flag
        ? BitConverter.ToUInt32(buffer, attributeOffset + 16) // resident: ValueLength
        : BitConverter.ToInt64(buffer, attributeOffset + 48); // non-resident: RealSize

    /// <summary>Walks the attribute records of one MFT record, stopping at the end-of-attributes marker or a malformed length.</summary>
    /// <returns>Each attribute's type, its absolute offset in <paramref name="buffer"/>, and its length.</returns>
    static IEnumerable<(uint Type, int Offset, int Length)> EnumerateAttributes(byte[] buffer, int recordOffset, int recordLength)
    {
        ushort firstAttributeOffset = BitConverter.ToUInt16(buffer, recordOffset + 20);
        int position = recordOffset + firstAttributeOffset;
        int recordEnd = recordOffset + recordLength;

        while (position + 4 <= recordEnd)
        {
            uint type = BitConverter.ToUInt32(buffer, position);
            if (type == EndOfAttributesMarker)
                yield break;

            int length = BitConverter.ToInt32(buffer, position + 4);
            if (length <= 0 || position + length > recordEnd)
                yield break;

            yield return (type, position, length);
            position += length;
        }
    }

    /// <summary>Walks the entries of a resident $ATTRIBUTE_LIST value, each pointing at the record that actually holds one attribute (or one fragment of one attribute's run list).</summary>
    static IEnumerable<(uint Type, long StartingVcn, long RecordNumber)> EnumerateResidentAttributeListEntries(byte[] buffer, int attributeOffset)
    {
        ushort valueOffset = BitConverter.ToUInt16(buffer, attributeOffset + 20);
        int valueLength = (int)BitConverter.ToUInt32(buffer, attributeOffset + 16);
        int position = attributeOffset + valueOffset;
        int end = position + valueLength;

        while (position + 24 <= end)
        {
            uint entryType = BitConverter.ToUInt32(buffer, position);
            ushort entryLength = BitConverter.ToUInt16(buffer, position + 4);
            if (entryLength == 0)
                yield break;

            long entryStartingVcn = BitConverter.ToInt64(buffer, position + 8);
            ulong entryFileReference = BitConverter.ToUInt64(buffer, position + 16);
            yield return (entryType, entryStartingVcn, (long)(entryFileReference & RecordNumberMask));

            position += entryLength;
        }
    }
}
