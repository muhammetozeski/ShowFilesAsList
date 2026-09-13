namespace ShowFilesAsList;

/// <summary>
/// One folder of the tree built by <see cref="DirectoryScanner"/>.
/// The lists keep the order in which the file system returned the entries; they are not sorted.
/// </summary>
/// <param name="name">The folder name. For the scanned root folder it is the full path.</param>
sealed class ScannedDirectory(string name)
{
    public readonly string Name = name;
    public readonly List<ScannedDirectory> Subdirectories = [];
    public readonly List<(string Name, long Size)> Files = [];

    /// <summary>
    /// Junctions, symbolic links and any other reparse point found in this folder. Their own content is never
    /// scanned or added to <see cref="Size"/>: a reparse point is a redirect, not real content of its own, and
    /// its target is often reachable — and then counted — under its real location elsewhere in the tree too.
    /// </summary>
    public readonly List<(string Name, string Target)> Junctions = [];

    /// <summary>
    /// Total size in bytes of all files in this folder and in every folder below it.
    /// </summary>
    public long Size;

    /// <summary>
    /// Set when reading the folder failed: the exception message and the full path of the folder.
    /// Entries read before the failure stay in the lists.
    /// </summary>
    public (string Message, string Path)? Error;
}
