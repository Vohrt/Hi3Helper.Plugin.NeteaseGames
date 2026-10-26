using System;
using System.Collections.Generic;
using Hi3Helper.NeteaseGames.Core.Management;
using Hi3Helper.Plugin.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The progress the plugin reports is the only thing the host uses for the bar, the percentage and the speed
///     (<c>PluginGameInstallWrapper.UpdateProgressCallback</c> derives the speed from
///     <c>DownloadedBytes - LastDownloaded</c>). A live run on 2026-10-26 oscillated between 5 MB and 9 MB out of
///     66 GB and jumped backwards, because the assemblers reported one chunk's size at a time as if it were the
///     running total. These tests pin the counter to "monotonically increasing, phase agnostic".
/// </summary>
public class ProgressTrackerTests
{
    /// <summary>Collects every value the tracker hands to the host.</summary>
    private sealed class Recorder
    {
        public List<long> Bytes { get; } = [];
        public List<int> Counts { get; } = [];

        public InstallProgressDelegate Callback => (in InstallProgress progress) =>
        {
            Bytes.Add(progress.DownloadedBytes);
            Counts.Add(progress.DownloadedCount);
        };
    }

    /// <summary>
    ///     The bug: the base assembler calls back once per chunk with <b>that chunk's size</b>. Feeding it straight to
    ///     the host made the counter restart from ~5 MB every time.
    /// </summary>
    [Fact]
    public void AddDelta_AccumulatesChunkSizes()
    {
        Recorder recorder = new();
        NeteaseGamesProgressTracker tracker = new(recorder.Callback, totalBytes: 1000, totalCount: 3);

        tracker.AddDelta(200);
        tracker.AddDelta(300);
        tracker.AddDelta(100);

        Assert.Equal([200L, 500L, 600L], recorder.Bytes);
    }

    /// <summary>
    ///     A second assembly phase starts its own chunk list from zero. Because deltas are added, the counter keeps
    ///     climbing across phases instead of dropping back to the size of the first chunk of the new phase.
    /// </summary>
    [Fact]
    public void AddDelta_KeepsClimbingAcrossPhases()
    {
        Recorder recorder = new();
        NeteaseGamesProgressTracker tracker = new(recorder.Callback, totalBytes: 1000, totalCount: 3);

        // base tier
        tracker.AddDelta(400);
        tracker.AddDelta(400);
        // shader tier: a fresh sequence of per-chunk sizes
        tracker.AddDelta(150);

        Assert.Equal([400L, 800L, 950L], recorder.Bytes);
        for (int i = 1; i < recorder.Bytes.Count; i++)
        {
            Assert.True(recorder.Bytes[i] >= recorder.Bytes[i - 1], "the counter must never decrease");
        }
    }

    /// <summary>
    ///     The delta downloader reports the running total of its own phase. A value below the previous maximum means
    ///     the phase started over (a retry re-reads the staged files from zero), and re-adding those bytes would
    ///     count them twice - so everything at or below the previous maximum is ignored, and the counter only moves
    ///     forward again once the phase passes where it had reached.
    /// </summary>
    [Fact]
    public void SetAbsolute_ConvertsToDeltasAndIgnoresRegression()
    {
        Recorder recorder = new();
        NeteaseGamesProgressTracker tracker = new(recorder.Callback, totalBytes: 1000, totalCount: 3);

        tracker.SetAbsolute(100);
        tracker.SetAbsolute(250);
        // a retry restarts the phase's own counter: these are bytes that were already counted
        tracker.SetAbsolute(50);
        tracker.SetAbsolute(120);
        tracker.SetAbsolute(300);

        Assert.Equal([100L, 250L, 300L], recorder.Bytes);
    }

    /// <summary>
    ///     Bytes left on disk by an interrupted run count from the start, so a resumed installation does not begin
    ///     at 0 % and then leap forward.
    /// </summary>
    [Fact]
    public void AddExisting_CountsTowardsTheTotal()
    {
        Recorder recorder = new();
        NeteaseGamesProgressTracker tracker = new(recorder.Callback, totalBytes: 1000, totalCount: 3);

        tracker.AddExisting(600);
        tracker.AddDelta(100);

        Assert.Equal([600L, 700L], recorder.Bytes);
    }

    /// <summary>
    ///     Every finished object advances the count, including the assembled chunks: a fresh installation downloads
    ///     ~11,000 chunks and 342 plan items, and the badge has to move during the assembly (it sat at "0 / 342" for
    ///     the whole first phase when only plan items were counted).
    /// </summary>
    [Fact]
    public void AddDelta_AdvancesTheCount()
    {
        Recorder recorder = new();
        NeteaseGamesProgressTracker tracker = new(recorder.Callback, totalBytes: 1000, totalCount: 3);

        tracker.AddDelta(100);
        tracker.AddDelta(100);

        Assert.Equal([1, 2], recorder.Counts);
    }

    /// <summary>
    ///     A resumed prefix arrives as one callback carrying several bytes and several units, and must land as
    ///     exactly that many units - otherwise a resumed run's badge starts behind its byte counter.
    /// </summary>
    [Fact]
    public void AddDelta_AcceptsAUnitCountForAResumedPrefix()
    {
        Recorder recorder = new();
        NeteaseGamesProgressTracker tracker = new(recorder.Callback, totalBytes: 1000, totalCount: 10);

        tracker.AddDelta(500, units: 6);
        tracker.AddDelta(100, units: 1);

        Assert.Equal([6, 7], recorder.Counts);
        Assert.Equal([500L, 600L], recorder.Bytes);
    }

    /// <summary>The delta downloader reports bytes separately, so its completion callback only moves the count.</summary>
    [Fact]
    public void AddCompletedUnit_CountsWithoutBytes()
    {
        Recorder recorder = new();
        NeteaseGamesProgressTracker tracker = new(recorder.Callback, totalBytes: 1000, totalCount: 3);

        tracker.AddCompletedUnit();
        tracker.AddCompletedUnit();

        Assert.Equal([1, 2], recorder.Counts);
        Assert.Equal([0L, 0L], recorder.Bytes);
    }

    /// <summary>Completion lands both counters on their maximum.</summary>
    [Fact]
    public void Complete_ReachesBothMaximums()
    {
        Recorder recorder = new();
        NeteaseGamesProgressTracker tracker = new(recorder.Callback, totalBytes: 1000, totalCount: 342);

        tracker.AddDelta(100);
        tracker.Complete();

        Assert.Equal(1000, recorder.Bytes[^1]);
        Assert.Equal(342, recorder.Counts[^1]);
    }

    /// <summary>An overshoot must not produce a percentage above 100.</summary>
    [Fact]
    public void Bytes_AreClampedToTheTotal()
    {
        Recorder recorder = new();
        NeteaseGamesProgressTracker tracker = new(recorder.Callback, totalBytes: 100, totalCount: 1);

        tracker.AddDelta(80);
        tracker.AddDelta(80);

        Assert.Equal([80L, 100L], recorder.Bytes);
    }

    /// <summary>A tracker with no host callback must still account bytes (it is used on the non-UI paths too).</summary>
    [Fact]
    public void NullDelegate_StillTracks()
    {
        NeteaseGamesProgressTracker tracker = new(null, totalBytes: 100, totalCount: 1);

        tracker.AddDelta(40);
        tracker.AddDelta(60);

        Assert.Equal(100, tracker.Bytes);
    }
}
