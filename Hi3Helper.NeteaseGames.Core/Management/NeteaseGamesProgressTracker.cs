using System;
using System.Threading;
using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.NeteaseGames.Core.Management;

/// <summary>
///     Turns the progress of the individual phases of a run into the single cumulative counter the host expects.
///     <para>
///         <see cref="InstallProgress.DownloadedBytes" /> is documented as "how many bytes <b>already</b> downloaded"
///         and the host derives both the percentage and the speed from it
///         (<c>PluginGameInstallWrapper.UpdateProgressCallback</c>: <c>readDownload = DownloadedBytes - LastDownloaded</c>).
///         Reporting anything other than a monotonically increasing total therefore produces exactly the symptoms the
///         live run showed on 2026-10-26: the bar oscillating between 5 MB and 9 MB out of 66 GB, and going backwards.
///     </para>
///     <para>
///         A run has three byte-producing phases that report in different shapes:
///     </para>
///     <list type="number">
///         <item>the base tier assembly - one callback per finished chunk, carrying that chunk's size;</item>
///         <item>the shader tier assembly - the same;</item>
///         <item>the delta download - one callback per write, carrying the running total of that phase.</item>
///     </list>
///     <para>
///         This type absorbs all three: deltas are added, absolute phase values are converted to deltas, and the result
///         is always a single total that never decreases.
///     </para>
/// </summary>
public sealed class NeteaseGamesProgressTracker
{
    private readonly object _gate = new();
    private readonly InstallProgressDelegate? _delegate;
    private readonly long _totalBytes;
    private readonly int _totalCount;

    private long _bytes;
    private int _count;
    private long _lastAbsolute;

    /// <summary>
    ///     Creates a tracker.
    /// </summary>
    /// <param name="progressDelegate">The host's callback (may be <c>null</c>).</param>
    /// <param name="totalBytes">The total the percentage is computed against.</param>
    /// <param name="totalCount">The total number of objects the run reports.</param>
    public NeteaseGamesProgressTracker(InstallProgressDelegate? progressDelegate, long totalBytes, int totalCount)
    {
        _delegate = progressDelegate;
        _totalBytes = totalBytes;
        _totalCount = totalCount;
    }

    /// <summary>The number of bytes accounted for so far.</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>
    ///     Records bytes that were already on disk before this run (a resumed installation). They count towards the
    ///     total, otherwise the bar would start at zero on a resumed run and jump when the first new byte arrives.
    /// </summary>
    /// <param name="bytes">The number of bytes already present.</param>
    /// <param name="count">The number of objects those bytes represent.</param>
    public void AddExisting(long bytes, int count = 0)
    {
        if (bytes <= 0 && count <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _bytes += Math.Max(0, bytes);
            _count += Math.Max(0, count);
        }

        Report();
    }

    /// <summary>
    ///     Adds the bytes of one finished object (the assemblers report this way).
    ///     <para>
    ///         The object count is deliberately left alone: the assemblers finish ~11,000 chunks while the plan has
    ///         ~340 items, so counting chunks would make the "N / M" badge jump to its maximum in the first seconds.
    ///         The count is set by <see cref="Complete" /> instead, which is what the previous code effectively did
    ///         (it reported 0 during the download and the total at the end).
    ///     </para>
    /// </summary>
    /// <param name="bytes">The size of the object that just finished.</param>
    public void AddDelta(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _bytes += bytes;
        }

        Report();
    }

    /// <summary>Marks the run as finished: the byte counter and the object count both reach their maximum.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _bytes = _totalBytes;
            _count = _totalCount;
        }

        Report();
    }

    /// <summary>
    ///     Records an absolute value from a phase that reports a running total (the delta downloader). The difference
    ///     against the previous absolute value is what gets added, so a phase that starts again at zero cannot make the
    ///     overall counter go backwards.
    /// </summary>
    /// <param name="absolute">The phase's running total.</param>
    public void SetAbsolute(long absolute)
    {
        long delta;
        lock (_gate)
        {
            delta = absolute - _lastAbsolute;
            if (delta <= 0)
            {
                return;
            }

            _lastAbsolute = absolute;
            _bytes += delta;
        }

        Report();
    }

    /// <summary>Reports the current total to the host.</summary>
    public void Report()
    {
        if (_delegate is null)
        {
            return;
        }

        long bytes;
        int count;
        lock (_gate)
        {
            bytes = _bytes;
            count = _count;
        }

        // A tracker can overshoot when a resumed phase re-reports bytes it had already counted; clamping keeps the
        // percentage the host computes inside 0..100.
        if (_totalBytes > 0 && bytes > _totalBytes)
        {
            bytes = _totalBytes;
        }

        if (_totalCount > 0 && count > _totalCount)
        {
            count = _totalCount;
        }

        InstallProgress progress = new()
        {
            DownloadedBytes = bytes,
            TotalBytesToDownload = _totalBytes,
            DownloadedCount = count,
            TotalCountToDownload = _totalCount,
            StateCount = count,
            TotalStateToComplete = _totalCount
        };

        _delegate(in progress);
    }
}
