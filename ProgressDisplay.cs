using System.Diagnostics;

namespace ShowFilesAsList;

/// <summary>
/// Draws the live progress of a <see cref="ScanProgress"/> on its own background thread: one line, rewritten in place
/// with a carriage return, for the phase now running, and a permanent line left behind for each phase as it finishes.
/// Nothing here runs on the scan's threads, so the display cannot slow the scan; it only reads the counters the scan
/// leaves in memory. When the output is redirected (a file or a pipe, not a console) it prints only the finished-phase
/// lines, so a redirected log stays clean instead of filling with carriage returns.
/// </summary>
sealed class ProgressDisplay : IDisposable
{
    const int RefreshMilliseconds = 100; // 10 frames a second: smooth to watch, and free on a background thread
    const int BarCells = 22;
    const int LabelWidth = 30;
    const int FallbackConsoleWidth = 100;

    readonly ScanProgress progress;
    readonly bool live;
    readonly Thread thread;
    readonly ManualResetEventSlim stopRequested = new(false);

    int liveLineLength;
    bool cursorHidden;

    public ProgressDisplay(ScanProgress progress)
    {
        this.progress = progress;
        live = OutputIsConsole();
        thread = new Thread(Run) { IsBackground = true, Name = "progress-display" };
        thread.Start();
    }

    static bool OutputIsConsole()
    {
        try { return !Console.IsOutputRedirected; }
        catch { return false; }
    }

    /// <summary>Stops the display thread, closes the last live line so later output starts cleanly, and restores the cursor.</summary>
    public void Dispose()
    {
        stopRequested.Set();
        thread.Join();
        if (liveLineLength > 0)
            Console.WriteLine();
        if (cursorHidden)
            TrySetCursorVisible(true);
        stopRequested.Dispose();
    }

    void Run()
    {
        // ManualResetEventSlim.Wait returns true once Dispose sets it; until then it times out every RefreshMilliseconds.
        while (!stopRequested.Wait(RefreshMilliseconds))
            Render();
        Render(); // a final frame so the last phase's finished line and figures are shown even if it ended between ticks
    }

    void Render()
    {
        while (progress.TryDequeueFinishedPhase(out ScanPhaseResult finished))
        {
            ClearLiveLine();
            Console.WriteLine(FormatFinished(finished));
        }

        if (!live || progress.Phase is not { } phase)
            return;

        if (!cursorHidden)
            cursorHidden = TrySetCursorVisible(false);

        TimeSpan elapsed = Stopwatch.GetElapsedTime(phase.StartTimestamp);
        WriteLiveLine(FormatLive(phase, elapsed));
        TrySetTitle($"Scanning - {TitleFor(phase)}");
    }

    // --- formatting ---

    string FormatLive(ScanPhase phase, TimeSpan elapsed) => phase.Kind switch
    {
        ScanPhaseKind.ByteBar => $"{Label(phase.Label)} {BarFor(phase)}  {SizeText(progress.Completed)} / {SizeText(phase.Total)}  {elapsed.ToDurationText()}",
        ScanPhaseKind.EntryBar or ScanPhaseKind.CountBar => $"{Label(phase.Label)} {BarFor(phase)}  {Count(progress.Completed)} / {Count(phase.Total)}  {elapsed.ToDurationText()}",
        ScanPhaseKind.TreeCounts => $"{Label(phase.Label)} {Count(progress.Folders)} folders  {Count(progress.Files)} files  {elapsed.ToDurationText()}",
        ScanPhaseKind.Walk => FormatWalk(phase, elapsed),
        _ => phase.Label,
    };

    string FormatWalk(ScanPhase phase, TimeSpan elapsed)
    {
        long bytes = progress.SizeBytes;
        string rate = elapsed.TotalSeconds > 0 ? $"{SizeText((long)(bytes / elapsed.TotalSeconds))}/s" : "-";
        return $"{Label(phase.Label)} {Count(progress.Folders)} folders  {Count(progress.Files)} files  {SizeText(bytes)}  {rate}  {elapsed.ToDurationText()}";
    }

    static string FormatFinished(ScanPhaseResult phase)
    {
        string body = phase.Kind switch
        {
            ScanPhaseKind.ByteBar => SizeText(phase.Completed),
            ScanPhaseKind.CountBar or ScanPhaseKind.EntryBar => $"{Count(phase.Completed)} entries",
            ScanPhaseKind.TreeCounts or ScanPhaseKind.Walk => $"{Count(phase.Folders)} folders, {Count(phase.Files)} files, {SizeText(phase.SizeBytes)}",
            _ => "",
        };
        return $"  done  {phase.Label,-LabelWidth} {body} in {phase.Duration.ToDurationText()}";
    }

    string BarFor(ScanPhase phase)
    {
        double fraction = phase.Total > 0 ? (double)progress.Completed / phase.Total : 0;
        fraction = Math.Clamp(fraction, 0, 1);
        int filled = (int)Math.Round(fraction * BarCells);
        return $"[{new string('#', filled)}{new string('-', BarCells - filled)}] {fraction * 100,3:F0}%";
    }

    string TitleFor(ScanPhase phase) => phase.Kind switch
    {
        ScanPhaseKind.ByteBar or ScanPhaseKind.CountBar or ScanPhaseKind.EntryBar when phase.Total > 0
            => $"{Math.Clamp((double)progress.Completed / phase.Total, 0, 1) * 100:F0}% - {phase.Label}",
        _ => phase.Label,
    };

    static string Label(string label) => label.Length >= LabelWidth ? label : label.PadRight(LabelWidth);
    static string Count(long value) => value.ToString("N0");
    static string SizeText(long value) => value.ToSizeText();

    // --- single-line console writing, carriage-return style, redirect- and width-safe ---

    void WriteLiveLine(string text)
    {
        int maxLength = SafeConsoleWidth() - 1;
        if (text.Length > maxLength)
            text = text[..maxLength];

        int padding = Math.Max(0, liveLineLength - text.Length);
        Console.Write($"\r{text}{new string(' ', padding)}");
        liveLineLength = text.Length;
    }

    void ClearLiveLine()
    {
        if (liveLineLength == 0)
            return;
        Console.Write($"\r{new string(' ', liveLineLength)}\r");
        liveLineLength = 0;
    }

    static int SafeConsoleWidth()
    {
        try { return Console.WindowWidth > 0 ? Console.WindowWidth : FallbackConsoleWidth; }
        catch { return FallbackConsoleWidth; }
    }

    static bool TrySetCursorVisible(bool visible)
    {
        try { Console.CursorVisible = visible; return true; }
        catch { return false; }
    }

    static void TrySetTitle(string title)
    {
        try { Console.Title = title; }
        catch { /* no console attached, or the host does not support it */ }
    }
}
