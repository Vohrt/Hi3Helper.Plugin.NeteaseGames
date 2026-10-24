using System;
using System.Collections.Generic;
using System.IO;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     An up-to-date installation must produce an <b>empty</b> download plan.
///     <para>
///         Regression for a defect measured on 2026-10-24: the plan of a fully current installation was
///         3 items / 10,579,120 bytes, all of them <c>*.mpkinfo</c> indexes. The layout gate
///         (<see cref="NeteaseGamesGameInstaller.IsLayoutConsistent" />) cannot apply to an index - the indexes
///         are the files that describe the shards, so they are absent from the marker map by construction, and
///         the gate correctly answers <c>false</c> for "an index exists but does not describe this file".
///         Applying it to them put every index into every plan, so <c>plan.IsEmpty</c> was never true: the host
///         never saw a finished update, the "nothing to download" branch never ran, and every update
///         re-downloaded ~10 MB for nothing.
///     </para>
/// </summary>
public class PlanConvergenceTests
{
    /// <summary>
    ///     Creates a scratch directory next to the test binary (the system temp directory may not be writable
    ///     for the test host).
    /// </summary>
    private static string CreateScratchDirectory(string prefix)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "scratch", prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    ///     An index whose size matches the announcement is complete. The layout gate must not be consulted: the
    ///     marker map never contains an index name, so the gate would report "unprovable" and re-plan it forever.
    /// </summary>
    [Fact]
    public void UpToDateIndex_IsNotPlanned()
    {
        string directory = CreateScratchDirectory("plan-index-");
        try
        {
            const string name = "script.mpkinfo";
            string path = Path.Combine(directory, name);
            File.WriteAllBytes(path, new byte[1_870_332]);

            // The real marker map of a live installation: shards only, never an index.
            Dictionary<string, uint> markers = new() { ["Patch1000.mpk"] = 2000, ["Patch2000.mpk"] = 4000 };
            // The real layouts of a live installation: the indexes describe the shards, not themselves.
            Dictionary<uint, long> layouts = new() { [2000] = 16_095_638, [4000] = 15_656_291 };

            Assert.False(NeteaseGamesGameInstaller.NeedsPackageDownload(
                path, name, 1_870_332, markers, layouts));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    ///     A missing or truncated index is still planned: the fix must not turn the index gate into "never check".
    /// </summary>
    [Fact]
    public void MissingOrTruncatedIndex_IsStillPlanned()
    {
        string directory = CreateScratchDirectory("plan-index-missing-");
        try
        {
            const string name = "s_file.mpkinfo";
            string path = Path.Combine(directory, name);
            Dictionary<string, uint> markers = new() { ["Patch1000.mpk"] = 2000 };
            Dictionary<uint, long> layouts = new() { [2000] = 16_095_638 };

            // Not on disk at all.
            Assert.True(NeteaseGamesGameInstaller.NeedsPackageDownload(path, name, 8_396_136, markers, layouts));

            // Present but shorter than the announcement (a truncated download).
            File.WriteAllBytes(path, new byte[100]);
            Assert.True(NeteaseGamesGameInstaller.NeedsPackageDownload(path, name, 8_396_136, markers, layouts));

            // Present and complete.
            File.WriteAllBytes(path, new byte[8_396_136]);
            Assert.False(NeteaseGamesGameInstaller.NeedsPackageDownload(path, name, 8_396_136, markers, layouts));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    ///     A shard keeps <b>both</b> gates: an index that does not describe it (a stale index, or a wrong marker)
    ///     still has to be re-downloaded even when its size matches. This is the behaviour that guards against the
    ///     <c>Patch2500</c> marker defect and must survive the index fix.
    /// </summary>
    [Fact]
    public void Shard_KeepsTheLayoutGate()
    {
        string directory = CreateScratchDirectory("plan-shard-");
        try
        {
            const string name = "Patch1000.mpk";
            string path = Path.Combine(directory, name);
            File.WriteAllBytes(path, new byte[1500]);

            Dictionary<string, uint> markers = new() { [name] = 2000 };

            // The index describes a longer payload than the shard holds: truncated, plan it.
            Dictionary<uint, long> truncated = new() { [2000] = 16_095_638 };
            Assert.True(NeteaseGamesGameInstaller.NeedsPackageDownload(path, name, 1500, markers, truncated));

            // The index does not describe this shard at all: unprovable, plan it.
            Dictionary<uint, long> other = new() { [4000] = 100 };
            Assert.True(NeteaseGamesGameInstaller.NeedsPackageDownload(path, name, 1500, markers, other));

            // The index describes a payload the shard covers: healthy.
            Dictionary<uint, long> healthy = new() { [2000] = 1000 };
            Assert.False(NeteaseGamesGameInstaller.NeedsPackageDownload(path, name, 1500, markers, healthy));

            // A wrong size is still caught by the first gate.
            Assert.True(NeteaseGamesGameInstaller.NeedsPackageDownload(path, name, 1501, markers, healthy));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    ///     End-to-end shape of the measured defect: on a fully current installation every object passes its gate,
    ///     so a plan built from them is empty. Mirrors the live numbers of 2026-10-24 (3 indexes + 47 shards).
    /// </summary>
    [Fact]
    public void UpToDateInstallation_ProducesAnEmptyPlan()
    {
        string directory = CreateScratchDirectory("plan-empty-");
        try
        {
            Dictionary<string, uint> markers = [];
            Dictionary<uint, long> layouts = [];

            // Three indexes, sizes as the live patch list announces them.
            (string Name, long Size)[] indexes =
            [
                ("indep.mpkinfo", 312_652),
                ("s_file.mpkinfo", 8_396_136),
                ("script.mpkinfo", 1_870_332)
            ];

            // Two shards with their index-derived payload lengths (the live values for marker 2000 / 4000).
            (string Name, uint Marker, long Payload, long OnDisk)[] shards =
            [
                ("Patch1000.mpk", 2000, 16_095_638, 16_150_134),
                ("Patch2000.mpk", 4000, 15_656_291, 15_656_291)
            ];

            NeteaseGamesDownloadPlan plan = new();
            foreach ((string name, long size) in indexes)
            {
                string path = Path.Combine(directory, name);
                File.WriteAllBytes(path, new byte[size]);
                if (NeteaseGamesGameInstaller.NeedsPackageDownload(path, name, size, markers, layouts))
                {
                    plan.Items.Add(new NeteaseGamesDownloadItem
                    {
                        RelativePath = name,
                        TargetRelativePath = name,
                        Kind = NeteaseGamesDownloadKind.Index,
                        ExpectedSize = size
                    });
                }
            }

            foreach ((string name, uint marker, long payload, long onDisk) in shards)
            {
                string path = Path.Combine(directory, name);
                File.WriteAllBytes(path, new byte[onDisk]);
                markers[name] = marker;
                layouts[marker] = payload;
                if (NeteaseGamesGameInstaller.NeedsPackageDownload(path, name, onDisk, markers, layouts))
                {
                    plan.Items.Add(new NeteaseGamesDownloadItem
                    {
                        RelativePath = name,
                        TargetRelativePath = name,
                        Kind = NeteaseGamesDownloadKind.Package,
                        ExpectedSize = onDisk
                    });
                }
            }

            Assert.True(plan.IsEmpty);
            Assert.Equal(0, plan.TotalSize);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
