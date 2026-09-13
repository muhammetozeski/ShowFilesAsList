using System.IO.Enumeration;

namespace ShowFilesAsList;

/// <summary>
/// Reads a folder tree from disk into <see cref="ScannedDirectory"/> objects and adds up the file sizes of every folder.
/// </summary>
/// <param name="maxDegreeOfParallelism">
/// How many folders may be read at once. 1 keeps the walk fully sequential, which is the better choice on a
/// spinning hard drive, where concurrent reads from scattered folders add seeks instead of hiding them. A solid-state
/// drive has no seek cost and benefits from a much higher value, since it can service many requests at once.
/// </param>
/// <param name="reportProgress">
/// Receives the total size in bytes of the files counted so far: at most once every
/// <see cref="ProgressIntervalMilliseconds"/> milliseconds during a scan, and once more with the final total.
/// </param>
sealed class DirectoryScanner(int maxDegreeOfParallelism = 1, Action<long>? reportProgress = null)
{
    const int ProgressIntervalMilliseconds = 100;

    // The same entries DirectoryInfo.GetFiles() and GetDirectories() return: hidden and system entries are included,
    // and a folder that cannot be opened throws instead of being skipped without a trace.
    static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    // One slot is this thread itself, so a parallelism of 1 hands out no extra slots and the walk stays sequential.
    readonly SemaphoreSlim? extraWorkerSlots = maxDegreeOfParallelism > 1 ? new SemaphoreSlim(maxDegreeOfParallelism - 1) : null;
    readonly Lock progressLock = new();

    long scannedBytes;
    long nextProgressTime;

    /// <summary>
    /// Scans <paramref name="rootPath"/> and every folder below it, reading each folder once.
    /// A folder that cannot be read gets <see cref="ScannedDirectory.Error"/> set and the scan goes on with the next folder.
    /// </summary>
    /// <param name="rootPath">Full path of an existing folder.</param>
    /// <returns>The scanned root folder.</returns>
    public ScannedDirectory Scan(string rootPath)
    {
        scannedBytes = 0;
        nextProgressTime = 0;

        ScannedDirectory root = ScanDirectory(rootPath, rootPath);
        reportProgress?.Invoke(Interlocked.Read(ref scannedBytes));
        return root;
    }

    /// <summary>
    /// Reads the entries of one folder. Subfolders beyond what <see cref="extraWorkerSlots"/> allows are scanned
    /// right here, depth-first; the rest are handed to their own tasks so several folders are read at once.
    /// </summary>
    /// <param name="path">Full path of the folder.</param>
    /// <param name="name">The name stored in <see cref="ScannedDirectory.Name"/>.</param>
    /// <returns>The folder with its files, its subfolders and its total size.</returns>
    ScannedDirectory ScanDirectory(string path, string name)
    {
        ScannedDirectory directory = new(name);

        try
        {
            FileSystemEnumerable<(string Name, bool IsDirectory, long Length, FileAttributes Attributes)> entries =
                new(path, static (ref entry) => (entry.FileName.ToString(), entry.IsDirectory, entry.Length, entry.Attributes), AllEntries);

            List<Task<ScannedDirectory>>? parallelSubdirectories = null;
            foreach ((string entryName, bool isDirectory, long length, FileAttributes attributes) in entries)
            {
                if (isDirectory && (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // A reparse point (a junction, a symbolic link, or any other kind of redirect) has no content
                    // of its own; its target is often reachable under its own, real location elsewhere in the
                    // tree too, so recursing into it here would double whatever it points to.
                    string target;
                    try { target = Directory.ResolveLinkTarget(Path.Join(path, entryName), returnFinalTarget: false)?.FullName ?? ""; }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { target = ""; }
                    directory.Junctions.Add((entryName, target));
                }
                else if (isDirectory)
                {
                    string subdirectoryPath = Path.Join(path, entryName);
                    if (extraWorkerSlots is not null && extraWorkerSlots.Wait(0))
                    {
                        (parallelSubdirectories ??= []).Add(Task.Run(() =>
                        {
                            try { return ScanDirectory(subdirectoryPath, entryName); }
                            finally { extraWorkerSlots.Release(); }
                        }));
                    }
                    else
                    {
                        ScannedDirectory subdirectory = ScanDirectory(subdirectoryPath, entryName);
                        directory.Subdirectories.Add(subdirectory);
                        directory.Size += subdirectory.Size;
                    }
                }
                else
                {
                    directory.Files.Add((entryName, length));
                    directory.Size += length;
                    Interlocked.Add(ref scannedBytes, length);
                }
            }

            if (parallelSubdirectories is not null)
            {
                foreach (Task<ScannedDirectory> task in parallelSubdirectories)
                {
                    ScannedDirectory subdirectory = task.GetAwaiter().GetResult();
                    directory.Subdirectories.Add(subdirectory);
                    directory.Size += subdirectory.Size;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            directory.Error = (exception.Message, path);
        }

        ReportProgressIfDue();
        return directory;
    }

    void ReportProgressIfDue()
    {
        if (reportProgress is null)
            return;

        lock (progressLock)
        {
            if (Environment.TickCount64 < nextProgressTime)
                return;
            reportProgress(Interlocked.Read(ref scannedBytes));
            nextProgressTime = Environment.TickCount64 + ProgressIntervalMilliseconds;
        }
    }
}
