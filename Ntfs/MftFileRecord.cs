namespace ShowFilesAsList.Ntfs;

/// <summary>
/// One $FILE_NAME attribute of an <see cref="MftFileRecord"/>: the name it has under one parent folder.
/// A record carries more than one of these when it is hard-linked under several parents, and often carries
/// two for the same parent because NTFS keeps a short 8.3 alias (<see cref="Namespace"/> <see cref="DosNamespace"/>)
/// alongside the real name.
/// </summary>
/// <param name="ParentRecordNumber">MFT record number of the parent folder.</param>
/// <param name="Name">The name in this namespace.</param>
/// <param name="Namespace">0 = POSIX, 1 = Win32, 2 = DOS 8.3 alias, 3 = Win32 name that is also a valid DOS name.</param>
readonly record struct MftFileNameAttribute(long ParentRecordNumber, string Name, byte Namespace)
{
    public const byte DosNamespace = 2;
}

/// <summary>
/// One parsed, in-use NTFS MFT record: a file or folder with its names, its size and the flags
/// <see cref="MftVolumeScanner"/> needs to decide how to place it in the scanned tree.
/// </summary>
sealed class MftFileRecord
{
    public required long RecordNumber { get; init; }
    public required bool IsDirectory { get; init; }

    /// <summary>
    /// Set when the record carries a $REPARSE_POINT attribute (a junction, a symlink, or a similar redirect).
    /// A reparse point directory has no children of its own in the MFT: whatever it points to is a separate,
    /// unrelated part of the tree that <see cref="MftVolumeScanner"/> has to scan the ordinary way instead.
    /// </summary>
    public required bool HasReparsePoint { get; init; }

    /// <summary>
    /// Real (logical) size in bytes of the unnamed $DATA attribute, when <see cref="DataSizeFoundLocally"/> is
    /// <see langword="true"/>; otherwise meaningless (0 for a directory, or a placeholder for a file whose $DATA
    /// attribute a $ATTRIBUTE_LIST points at a different record — <see cref="ExternalDataRecordNumber"/> resolves that case).
    /// </summary>
    public required long DataSize { get; init; }

    /// <summary>
    /// <see langword="true"/> when this record's own attributes included an unnamed $DATA — including a
    /// genuinely empty file, whose $DATA is 0 bytes but still "found". A large or heavily fragmented file
    /// sometimes keeps its $DATA in a different record instead, which this being <see langword="false"/> signals.
    /// </summary>
    public required bool DataSizeFoundLocally { get; init; }

    /// <summary>
    /// The record actually holding this file's $DATA attribute, read from a resident $ATTRIBUTE_LIST when
    /// <see cref="DataSizeFoundLocally"/> is <see langword="false"/>. NTFS moves $DATA to its own record like this
    /// for a file with either so many attributes or so many data runs (fragments) that they no longer fit the 1024
    /// bytes of one record — a multi-gigabyte or badly fragmented file, typically.
    /// </summary>
    public required long? ExternalDataRecordNumber { get; init; }

    /// <summary>
    /// <see langword="true"/> for the rare file whose own $ATTRIBUTE_LIST is itself too large to stay resident,
    /// so its entries cannot be read from this record's bytes alone. <see cref="MftVolumeScanner"/> falls back
    /// to <see cref="FileInfo"/> for this file's size rather than read the list's own, possibly scattered, data runs.
    /// </summary>
    public required bool HasUnresolvedNonResidentAttributeList { get; init; }

    public required List<MftFileNameAttribute> Names { get; init; }

    /// <summary>
    /// The distinct parent folders this record appears under, each with the name to show there: the Win32 name
    /// when the record has one for that parent, the DOS 8.3 alias otherwise. A record with both a long and a
    /// short name under the same parent — the common case — yields one entry here, not two.
    /// </summary>
    public IEnumerable<(long ParentRecordNumber, string Name)> DistinctParentNames()
    {
        foreach (IGrouping<long, MftFileNameAttribute> group in Names.GroupBy(name => name.ParentRecordNumber))
        {
            MftFileNameAttribute preferred = group.FirstOrDefault(name => name.Namespace != MftFileNameAttribute.DosNamespace, group.First());
            yield return (group.Key, preferred.Name);
        }
    }
}
