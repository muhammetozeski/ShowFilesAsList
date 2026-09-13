using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using ShowFilesAsList.Ntfs;

namespace ShowFilesAsList;

static class Program
{
    const string ResultFileName = "result.json";
    const string FolderPrompt = "Please enter a folder to scan:";
    const int ProgressLineWidth = 24;

    // Parallelism used for the classic, per-folder scan when the fast NTFS path is not available. A solid-state
    // drive has no seek cost and services many requests at once; a spinning drive is fastest read one folder at a
    // time, since concurrent reads from scattered folders just add seeks; a path whose media could not be
    // identified (a network share, a virtual drive) gets a modest value that hides some round-trip latency
    // without risking the thrashing a spinning drive would suffer from it.
    const int SolidStateParallelism = 64;
    const int SpinningParallelism = 1;
    const int UnknownMediaParallelism = 4;

    /// <summary>
    /// Asks for a folder (unless one was already supplied in <paramref name="args"/>, which happens only when this
    /// process restarted itself elevated to use the fast NTFS scan), scans it, saves the result as result.json next
    /// to the executable and opens that file with the program Windows uses for .json files.
    /// </summary>
    static void Main(string[] args)
    {
        string resultFilePath = Path.Join(AppContext.BaseDirectory, ResultFileName);

        string? rootPath = args is [string suppliedPath, ..] ? suppliedPath : PromptForDirectoryPath();
        if (rootPath is null)
            return;

        if (args.Length == 0)
        {
            Console.WriteLine($"This path will be scanned: {rootPath}");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey(intercept: true);
        }
        else if (!Directory.Exists(rootPath))
        {
            // A path arrives here only from the elevated restart in TryRestartElevatedForPath, so this should
            // never actually be missing; check anyway rather than let a bad path fail confusingly deep in the scan.
            Console.WriteLine($"'{rootPath}' is not a folder that exists. Press any key to exit...");
            Console.ReadKey(intercept: true);
            return;
        }

        Console.Title = "Scanning";
        Stopwatch scanStopwatch = Stopwatch.StartNew();
        ScannedDirectory root = Scan(rootPath);
        TimeSpan scanDuration = scanStopwatch.Elapsed;
        Console.WriteLine();

        try
        {
            ResultJsonWriter.Write(resultFilePath, rootPath, root, scanDuration);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Could not save {resultFilePath}: {exception.Message}");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey(intercept: true);
            return;
        }

        Console.WriteLine($"Saved {resultFilePath}");
        Process.Start("explorer.exe", $"\"{resultFilePath}\"").Dispose();
    }

    /// <summary>
    /// Reads console lines until one of them is an existing folder. Double quotes and surrounding spaces are removed first,
    /// so a path copied with Explorer's "Copy as path" can be pasted as it is.
    /// </summary>
    /// <returns>The full path of the folder, or <see langword="null"/> when the input ends before a folder is entered.</returns>
    static string? PromptForDirectoryPath()
    {
        Console.WriteLine(FolderPrompt);
        while (Console.ReadLine() is string line)
        {
            string path = line.Replace("\"", "").Trim();

            if (Directory.Exists(path))
                return Path.GetFullPath(path);

            Console.WriteLine($"{(path.Length == 0 ? "The input is empty." : "This folder does not exist.")} {FolderPrompt}");
        }

        return null;
    }

    /// <summary>
    /// Scans <paramref name="rootPath"/>, using the direct NTFS Master File Table reader when the path is on a
    /// local NTFS drive and this process is elevated enough to read it — normally many times faster than opening
    /// every folder one by one — and otherwise the ordinary per-folder walk. On a local NTFS drive without
    /// elevation, offers to restart elevated so the fast reader can be used.
    /// </summary>
    static ScannedDirectory Scan(string rootPath)
    {
        void ReportProgress(long scannedBytes) => Console.Write($"\r{scannedBytes.ToSizeText()} read".PadRight(ProgressLineWidth));

        if (!IsLocalNtfsDrive(rootPath))
            return new DirectoryScanner(ParallelismFor(rootPath), ReportProgress).Scan(rootPath);

        if (!IsRunningElevated())
        {
            if (TryRestartElevatedForPath(rootPath))
                Environment.Exit(0);
            Console.WriteLine("Continuing without administrator rights: this scan will use the ordinary, slower folder walk.");
            return new DirectoryScanner(ParallelismFor(rootPath), ReportProgress).Scan(rootPath);
        }

        try
        {
            return new MftVolumeScanner(ReportProgress).Scan(rootPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"The fast NTFS scan could not be used ({exception.Message}); falling back to the ordinary folder walk.");
            return new DirectoryScanner(ParallelismFor(rootPath), ReportProgress).Scan(rootPath);
        }
    }

    static bool IsLocalNtfsDrive(string path)
    {
        try
        {
            DriveInfo drive = new(path);
            return drive.DriveType == DriveType.Fixed && drive.DriveFormat == "NTFS";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return false;
        }
    }

    static bool IsRunningElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static int ParallelismFor(string path) => StorageMediaDetector.DetectForPath(path) switch
    {
        StorageMediaKind.SolidState => SolidStateParallelism,
        StorageMediaKind.Spinning => SpinningParallelism,
        _ => UnknownMediaParallelism,
    };

    /// <summary>
    /// Asks whether to restart this program elevated, with <paramref name="rootPath"/> passed on the command
    /// line so the elevated instance scans it right away instead of asking again.
    /// </summary>
    /// <returns><see langword="true"/> when the elevated instance was started and this one should now exit.</returns>
    static bool TryRestartElevatedForPath(string rootPath)
    {
        Console.WriteLine("Scanning this drive much faster needs administrator rights.");
        Console.Write("Restart elevated? (y/n): ");
        if (Console.ReadKey().KeyChar is not ('y' or 'Y'))
        {
            Console.WriteLine();
            return false;
        }
        Console.WriteLine();

        try
        {
            ProcessStartInfo startInfo = new(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            startInfo.ArgumentList.Add(rootPath); // ArgumentList quotes correctly on its own — a hand-built "\"{path}\"" breaks for a path ending in \, which every drive root does
            using Process? elevated = Process.Start(startInfo);
            return elevated is not null;
        }
        catch (Win32Exception)
        {
            Console.WriteLine("The administrator prompt was declined or could not be shown.");
            return false;
        }
    }
}
