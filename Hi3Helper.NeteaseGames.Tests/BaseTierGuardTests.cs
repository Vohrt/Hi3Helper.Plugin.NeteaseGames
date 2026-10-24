using System;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The base shard tier (<c>Patch.mpk</c> / <c>Patch1..127</c>) is not served by any public single-shard
///     endpoint, so a round that really rewrites it can only be finished by the official launcher.
///     <para>
///         ⚠ <c>@bigpatchopen@</c> alone does <b>not</b> mean that: it is a branch capability flag and was
///         <c>true</c> on every round measured 2026-10-22 .. 10-24 while the delta tier was sufficient. Refusing on
///         it unconditionally (the previous behaviour) made every update fail. The decision has to look at whether
///         the base tier <b>moved</b>, which <c>@bigpatchmd5@</c> answers.
///     </para>
/// </summary>
public class BaseTierGuardTests
{
    private const string Fingerprint = "6128997482385675719";
    private const string OtherFingerprint = "1234567890123456789";

    private static NeteaseGamesPatchManifest Manifest(bool bigPatchOpen, bool isFull, string? fingerprint = Fingerprint)
        => new()
        {
            ContentVersion = "20261023122834919",
            PatchVersion = 6589318,
            BaseVersion = "20261021113421287",
            EngineVersion = "261021114347_cd5d7a8d5_release",
            IsBigPatchOpen = bigPatchOpen,
            IsFullPackage = isFull,
            BigPatchMd5 = fingerprint ?? string.Empty
        };

    private static NeteaseGamesLocalState Local(string fingerprint)
        => new() { PatchVersion = 6585899, BigPatchMd5 = fingerprint };

    private static NeteaseGamesDownloadPlan PlanWithWork()
    {
        NeteaseGamesDownloadPlan plan = new();
        plan.Items.Add(new NeteaseGamesDownloadItem
        {
            RelativePath = "20261023122834919/mpk_script/Patch1000.mpk",
            TargetRelativePath = "Patch1000.mpk",
            Kind = NeteaseGamesDownloadKind.Package,
            ExpectedSize = 16_095_638
        });
        return plan;
    }

    [Fact]
    public void Allows_WhenTheSwitchIsOnButTheBaseTierDidNotMove()
    {
        // This is the live situation of 2026-10-22 .. 10-24: @bigpatchopen@ is true on every round while the
        // fingerprint stays put. The round is a pure delta and must be applied.
        NeteaseGamesGameInstaller.VerifyBaseTierCompletable(
            Manifest(bigPatchOpen: true, isFull: false), PlanWithWork(), Local(Fingerprint));
    }

    [Fact]
    public void Refuses_WhenTheBaseTierFingerprintMoved()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            NeteaseGamesGameInstaller.VerifyBaseTierCompletable(
                Manifest(bigPatchOpen: true, isFull: false), PlanWithWork(), Local(OtherFingerprint)));

        Assert.Contains("official launcher", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allows_WhenNothingRecordedTheFingerprintYet()
    {
        // An installation the plugin never updated has no fingerprint on disk. The delta tier still brings the
        // patch version up to date, so the round is applied and the fingerprint is recorded afterwards.
        NeteaseGamesGameInstaller.VerifyBaseTierCompletable(
            Manifest(bigPatchOpen: true, isFull: false), PlanWithWork(), Local(string.Empty));
    }

    [Fact]
    public void Refuses_WhenTheSwitchIsOnWithoutAFingerprint()
    {
        // Cannot be judged: stay on the safe side rather than writing a version over a possibly half-updated tier.
        Assert.Throws<NotSupportedException>(() => NeteaseGamesGameInstaller.VerifyBaseTierCompletable(
            Manifest(bigPatchOpen: true, isFull: false, fingerprint: null), PlanWithWork(), Local(Fingerprint)));
    }

    [Fact]
    public void Refuses_WhenRoundIsAFullPackage()
    {
        Assert.Throws<NotSupportedException>(() => NeteaseGamesGameInstaller.VerifyBaseTierCompletable(
            Manifest(bigPatchOpen: false, isFull: true), PlanWithWork(), Local(Fingerprint)));
    }

    [Fact]
    public void Allows_ADeltaRound()
    {
        NeteaseGamesGameInstaller.VerifyBaseTierCompletable(
            Manifest(bigPatchOpen: false, isFull: false), PlanWithWork(), Local(Fingerprint));
    }

    [Fact]
    public void Allows_WhenThereIsNothingToDownload()
    {
        // An installation that is already current is never blocked: there is no update to half-apply.
        NeteaseGamesGameInstaller.VerifyBaseTierCompletable(
            Manifest(bigPatchOpen: true, isFull: true), new NeteaseGamesDownloadPlan(), Local(OtherFingerprint));
    }

    [Fact]
    public void Allows_WhenTheManifestIsMissing()
    {
        NeteaseGamesGameInstaller.VerifyBaseTierCompletable(null, PlanWithWork(), Local(Fingerprint));
    }
}
