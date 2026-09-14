using System.Collections.Concurrent;
using System.Diagnostics;

namespace ShowFilesAsList;

/// <summary>How <see cref="ProgressDisplay"/> renders a phase: which numbers it has and whether it can show a percentage.</summary>
enum ScanPhaseKind
{
    /// <summary>A bar whose numerator and denominator are byte counts (reading the Master File Table).</summary>
    ByteBar,

    /// <summary>A bar whose numerator and denominator are plain item counts (parsing records, building the index).</summary>
    CountBar,

    /// <summary>No total is knowable up front, so no bar: the running folder and file counts are shown instead (building the tree from the parsed records).</summary>
    TreeCounts,

    /// <summary>Like <see cref="TreeCounts"/> but also shows the read rate and the folder currently being read (the ordinary per-folder walk).</summary>
    Walk,

    /// <summary>A bar counting entries written against the total the finished scan produced (writing result.json).</summary>
    EntryBar,
}

/// <summary>One phase of a scan: what it is called, how to render it, its total (0 when indeterminate) and when it began.</summary>
sealed record ScanPhase(string Label, ScanPhaseKind Kind, long Total, long StartTimestamp);

/// <summary>A phase that has ended, captured the moment the next one begins, so its final line and duration stay correct.</summary>
sealed record ScanPhaseResult(string Label, ScanPhaseKind Kind, long Completed, long Total, long Folders, long Files, long Junctions, long SizeBytes, TimeSpan Duration);

/// <summary>
/// The live state of a running scan, written by the scan threads and read by <see cref="ProgressDisplay"/> on its own
/// thread. The scan side only ever touches plain 64-bit counters — with <see cref="Interlocked"/> where more than one
/// thread writes the same one, and never more than once per folder or per parsed range so no counter is contended at
/// item frequency — and never does any console work itself. That keeps the whole progress feature off the scan's hot
/// path: the display thread does all the formatting and drawing.
/// </summary>
sealed class ScanProgress
{
    long completed, folders, files, junctions, sizeBytes;
    volatile string currentPath = "";
    volatile ScanPhase? phase;
    readonly ConcurrentQueue<ScanPhaseResult> finishedPhases = new();

    public ScanPhase? Phase => phase;

    /// <summary>Takes the next phase that has finished since the last call, for the display to print as a permanent line.</summary>
    public bool TryDequeueFinishedPhase(out ScanPhaseResult result) => finishedPhases.TryDequeue(out result!);

    public long Completed => Volatile.Read(ref completed);
    public long Folders => Volatile.Read(ref folders);
    public long Files => Volatile.Read(ref files);
    public long Junctions => Volatile.Read(ref junctions);
    public long SizeBytes => Volatile.Read(ref sizeBytes);
    public string CurrentPath => currentPath;

    /// <summary>Ends the phase now in progress (if any), capturing its final figures for the display, and starts a new one.</summary>
    /// <param name="kind">How the display should render the new phase.</param>
    /// <param name="label">The phase's name, shown on its live line and its finished line.</param>
    /// <param name="total">Item or byte total for a bar phase; 0 for the indeterminate <see cref="ScanPhaseKind.TreeCounts"/> and <see cref="ScanPhaseKind.Walk"/>.</param>
    public void BeginPhase(ScanPhaseKind kind, string label, long total = 0)
    {
        Complete();
        Volatile.Write(ref completed, 0);
        Volatile.Write(ref folders, 0);
        Volatile.Write(ref files, 0);
        Volatile.Write(ref junctions, 0);
        Volatile.Write(ref sizeBytes, 0);
        currentPath = "";
        phase = new ScanPhase(label, kind, total, Stopwatch.GetTimestamp());
    }

    /// <summary>Ends the phase now in progress, if any, without starting another — call once when the whole scan and write are done.</summary>
    public void Complete()
    {
        if (phase is not { } endingPhase)
            return;
        finishedPhases.Enqueue(new ScanPhaseResult(
            endingPhase.Label, endingPhase.Kind, Volatile.Read(ref completed), endingPhase.Total,
            Volatile.Read(ref folders), Volatile.Read(ref files), Volatile.Read(ref junctions), Volatile.Read(ref sizeBytes),
            Stopwatch.GetElapsedTime(endingPhase.StartTimestamp)));
        phase = null;
    }

    /// <summary>Adds to the bar numerator. Call once per parsed range or per written folder, never once per item, so the counter is never contended at item frequency.</summary>
    public void AdvanceBar(long amount) => Interlocked.Add(ref completed, amount);

    /// <summary>Adds one folder's own tallies during a tree build. Called from the single tree-building thread, once per folder.</summary>
    public void AddTreeCounts(long addFiles, long addJunctions)
    {
        Interlocked.Increment(ref folders);
        Interlocked.Add(ref files, addFiles);
        Interlocked.Add(ref junctions, addJunctions);
    }

    /// <summary>Publishes one folder's tallies during the ordinary walk. Called once per folder from possibly several walk threads.</summary>
    public void PublishWalk(long addFiles, long addJunctions, long addBytes, string path)
    {
        Interlocked.Increment(ref folders);
        Interlocked.Add(ref files, addFiles);
        Interlocked.Add(ref junctions, addJunctions);
        Interlocked.Add(ref sizeBytes, addBytes);
        currentPath = path;
    }
}
