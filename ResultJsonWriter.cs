using System.Text.Encodings.Web;
using System.Text.Json;

namespace ShowFilesAsList;

/// <summary>
/// Saves a <see cref="ScannedDirectory"/> tree as indented JSON. The root object starts with the "> notes" text;
/// after it every folder is an object keyed "name : size", every file is a "name": "size" pair, and every
/// junction is a "name : junction": "target" pair. Inside each object folders come before files before
/// junctions; folders and files are both ordered from the largest to the smallest.
/// </summary>
static class ResultJsonWriter
{
    const string NotesKey = "> notes";
    const string FolderKeySeparator = " : ";
    const string JunctionKeySuffix = " : junction";
    const string ErrorKeyPrefix = "Error: ";
    const int FlushThresholdBytes = 1024 * 1024;

    // UnsafeRelaxedJsonEscaping writes names such as "résumé.pdf" and the ">" of the notes key as they are instead of \u escapes.
    // It is unsafe only for JSON placed inside HTML pages, which a local result file never is.
    static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Creates or overwrites <paramref name="filePath"/> with the notes and the content of <paramref name="root"/>.
    /// </summary>
    /// <param name="filePath">Path of the JSON file to write.</param>
    /// <param name="rootPath">Full path of the scanned folder, used to look up the disk space of its drive.</param>
    /// <param name="root">The scanned folder whose content becomes the root JSON object.</param>
    /// <param name="scanDuration">How long the scan itself took, from start to the finished tree — not reading the folder path, not writing this file.</param>
    /// <param name="progress">Advances the write-phase bar as folders are written; may be <see langword="null"/>.</param>
    /// <exception cref="IOException">The file could not be created or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Writing to <paramref name="filePath"/> is not permitted.</exception>
    public static void Write(string filePath, string rootPath, ScannedDirectory root, TimeSpan scanDuration, ScanProgress? progress = null)
    {
        using FileStream file = File.Create(filePath);
        using Utf8JsonWriter writer = new(file, WriterOptions);

        writer.WriteStartObject();
        writer.WriteString(NotesKey, CreateNotes(rootPath, root.Size, scanDuration));
        WriteContent(writer, root, progress);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Builds the "> notes" text: the scan time and the scanned size, then the free space and the total size of the drive holding the folder.
    /// </summary>
    /// <param name="rootPath">Full path of the scanned folder.</param>
    /// <param name="scannedBytes">Total size of the scanned files in bytes.</param>
    /// <param name="scanDuration">How long the scan took.</param>
    /// <returns>
    /// The notes, one fact per line. When the drive cannot be queried, for example because the path is a network share,
    /// the reason is written in place of the drive lines.
    /// </returns>
    static string CreateNotes(string rootPath, long scannedBytes, TimeSpan scanDuration)
    {
        string scanTimeLine = $"Scan time: {scanDuration.ToDurationText()}";
        string scannedSizeLine = $"Scanned size: {scannedBytes.ToSizeText()}";

        try
        {
            DriveInfo drive = new(rootPath);
            return $"{scanTimeLine}\n{scannedSizeLine}\nFree space on disk: {drive.TotalFreeSpace.ToSizeText()}\nDisk size: {drive.TotalSize.ToSizeText()}";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return $"{scanTimeLine}\n{scannedSizeLine}\nDisk space is unavailable: {exception.Message}";
        }
    }

    /// <summary>
    /// Writes the subfolders, the files and the read error of <paramref name="directory"/> into the JSON object that is open in <paramref name="writer"/>.
    /// </summary>
    /// <param name="writer">A writer whose innermost open object belongs to <paramref name="directory"/>.</param>
    /// <param name="directory">The folder whose content is written.</param>
    /// <param name="progress">Advanced once per folder written (by this folder's own entry count), so the write-phase bar fills without a per-entry update.</param>
    static void WriteContent(Utf8JsonWriter writer, ScannedDirectory directory, ScanProgress? progress)
    {
        foreach (ScannedDirectory subdirectory in directory.Subdirectories.OrderByDescending(static subdirectory => subdirectory.Size))
        {
            writer.WriteStartObject($"{subdirectory.Name}{FolderKeySeparator}{subdirectory.Size.ToSizeText()}");
            WriteContent(writer, subdirectory, progress);
            writer.WriteEndObject();
        }

        foreach ((string fileName, long fileSize) in directory.Files.OrderByDescending(static file => file.Size))
            writer.WriteString(fileName, fileSize.ToSizeText());

        foreach ((string junctionName, string target) in directory.Junctions)
            writer.WriteString($"{junctionName}{JunctionKeySuffix}", target);

        if (directory.Error is (string message, string path))
            writer.WriteString($"{ErrorKeyPrefix}{message}", path);

        progress?.AdvanceBar(1 + directory.Files.Count + directory.Junctions.Count);

        // Utf8JsonWriter keeps the written JSON in memory until Flush, which would hold the whole file for a large tree.
        if (writer.BytesPending >= FlushThresholdBytes)
            writer.Flush();
    }
}
