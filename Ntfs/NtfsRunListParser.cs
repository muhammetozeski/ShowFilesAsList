namespace ShowFilesAsList.Ntfs;

/// <summary>
/// Decodes the data run list of a non-resident NTFS attribute: the variable-length byte encoding that maps
/// an attribute's virtual clusters to the logical clusters holding them on disk.
/// </summary>
static class NtfsRunListParser
{
    /// <summary>
    /// Parses a data run list starting at <paramref name="offset"/> in <paramref name="record"/>, stopping at the
    /// terminating zero byte.
    /// </summary>
    /// <param name="record">The full raw MFT record, with the update sequence fixup already applied.</param>
    /// <param name="offset">Byte offset of the first run header within <paramref name="record"/>.</param>
    /// <returns>
    /// The runs in ascending virtual-cluster order. A sparse run (no physical clusters allocated) is omitted,
    /// since only metadata is read here and never the file content a sparse hole would stand in for.
    /// </returns>
    public static List<NtfsDataRun> Parse(byte[] record, int offset)
    {
        List<NtfsDataRun> runs = [];
        long currentVcn = 0;
        long currentLcn = 0;
        int position = offset;

        while (position < record.Length && record[position] != 0)
        {
            byte header = record[position];
            int lengthByteCount = header & 0x0F;
            int offsetByteCount = (header >> 4) & 0x0F;
            position++;

            long runLength = ReadLittleEndian(record, position, lengthByteCount, signed: false);
            position += lengthByteCount;

            if (offsetByteCount > 0)
            {
                currentLcn += ReadLittleEndian(record, position, offsetByteCount, signed: true);
                position += offsetByteCount;
                runs.Add(new NtfsDataRun(currentVcn, currentLcn, runLength));
            }
            currentVcn += runLength;
        }

        return runs;
    }

    /// <summary>
    /// Reads a little-endian integer of <paramref name="byteCount"/> bytes. When <paramref name="signed"/> is
    /// <see langword="true"/>, the value is sign-extended from the highest bit of its most significant byte,
    /// matching how run offsets (which can be negative, moving earlier on disk) are stored.
    /// </summary>
    static long ReadLittleEndian(byte[] data, int offset, int byteCount, bool signed)
    {
        long value = 0;
        for (int i = 0; i < byteCount; i++)
            value |= (long)data[offset + i] << (8 * i);

        if (signed && byteCount is > 0 and < 8 && (data[offset + byteCount - 1] & 0x80) != 0)
            value |= -1L << (8 * byteCount);

        return value;
    }
}
