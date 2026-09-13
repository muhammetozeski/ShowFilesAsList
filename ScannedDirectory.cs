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
    /// Total size in bytes of all files in this folder and in every folder below it.
    /// </summary>
    public long Size;

    /// <summary>
    /// Set when reading the folder failed: the exception message and the full path of the folder.
    /// Entries read before the failure stay in the lists.
    /// </summary>
    public (string Message, string Path)? Error;
}
