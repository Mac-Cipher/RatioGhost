using RatioGhost.Core.Announcements;
using RatioGhost.Core.Configuration;

namespace RatioGhost.Core.Tests;

public sealed class AnnounceTransformerTests
{
    [Fact]
    public void Transform_BlocksNonTrackerTrafficByDefault()
    {
        var transformer = new AnnounceTransformer(new FixedRandomSource());

        var result = transformer.Transform(new Uri("http://example.test/index.html"), new RatioGhostSettings());

        Assert.Equal(AnnounceDisposition.BlockedNonTracker, result.Disposition);
        Assert.Null(result.Target);
    }

    [Fact]
    public void Transform_PortsFreeLeechAndPretendSeedBehavior()
    {
        var transformer = new AnnounceTransformer(new FixedRandomSource());
        var settings = new RatioGhostSettings
        {
            ReportDownloadAsZero = true,
            PretendToSeed = true
        };

        var result = transformer.Transform(
            new Uri("http://tracker.test/announce?info_hash=abc&downloaded=50&uploaded=20&left=700&event=completed"),
            settings);

        Assert.Equal(AnnounceDisposition.Rewritten, result.Disposition);
        Assert.Contains("downloaded=0", result.Target!.Query, StringComparison.Ordinal);
        Assert.Contains("uploaded=20", result.Target.Query, StringComparison.Ordinal);
        Assert.Contains("left=0", result.Target.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("event=", result.Target.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Transform_UsesPeerThresholdAndLegacyRatios()
    {
        var transformer = new AnnounceTransformer(new FixedRandomSource(0.5));
        var settings = new RatioGhostSettings
        {
            MinimumPeers = 5,
            UploadPerDownloadMinimum = 2,
            UploadPerDownloadMaximum = 2,
            UploadPerUploadMinimum = 3,
            UploadPerUploadMaximum = 3,
            BoostChancePercent = 0
        };
        transformer.ObserveTrackerResponse("abc", new TrackerResponse(null, 5, null, null));

        var result = transformer.Transform(
            new Uri("http://tracker.test/announce?info_hash=abc&downloaded=10&uploaded=4&left=1"),
            settings);

        Assert.Contains("uploaded=36", result.Target!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Standby_FollowsKnownTorrentLeftValues()
    {
        var transformer = new AnnounceTransformer(new FixedRandomSource());
        var settings = new RatioGhostSettings();

        transformer.Transform(
            new Uri("http://tracker.test/announce?info_hash=a&downloaded=10&uploaded=4&left=0"),
            settings);
        Assert.True(transformer.IsSeedOnlyStandby);
        transformer.Transform(
            new Uri("http://tracker.test/announce?info_hash=b&downloaded=10&uploaded=4&left=42"),
            settings);
        Assert.True(transformer.HasActiveDownloads);
        Assert.False(transformer.IsSeedOnlyStandby);
    }

    [Fact]
    public void Transform_WhenPausedPreservesPreviouslyReportedCounters()
    {
        var transformer = new AnnounceTransformer(new FixedRandomSource());
        var settings = new RatioGhostSettings();
        transformer.Transform(
            new Uri("http://tracker.test/announce?info_hash=a&downloaded=100&uploaded=200&left=10"),
            settings);

        var paused = transformer.Transform(
            new Uri("http://tracker.test/announce?info_hash=a&downloaded=5&uploaded=7&left=10"),
            settings,
            paused: true);

        Assert.Contains("downloaded=100", paused.Target!.Query, StringComparison.Ordinal);
        Assert.Contains("uploaded=200", paused.Target.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshots_AccumulateActualAndReportedDeltasLikeTcl()
    {
        var transformer = new AnnounceTransformer(new FixedRandomSource());
        var settings = new RatioGhostSettings();
        transformer.Transform(
            new Uri("http://tracker.test/announce?info_hash=a&downloaded=100&uploaded=200&left=50&event=started"),
            settings);
        transformer.Transform(
            new Uri("http://tracker.test/announce?info_hash=a&downloaded=150&uploaded=260&left=0"),
            settings);

        var snapshot = Assert.Single(transformer.GetSnapshots());
        Assert.Equal("tracker.test", snapshot.Tracker);
        Assert.Equal(50, snapshot.ActualDownloadedTotal);
        Assert.Equal(60, snapshot.ActualUploadedTotal);
        Assert.Equal(50, snapshot.ReportedDownloadedTotal);
        Assert.Equal(60, snapshot.ReportedUploadedTotal);
        Assert.Equal(0, snapshot.ActualLeft);
    }

    [Fact]
    public void ResetTorrent_RemovesOnlySelectedTorrentState()
    {
        var transformer = new AnnounceTransformer(new FixedRandomSource());
        var settings = new RatioGhostSettings();
        transformer.Transform(
            new Uri("http://tracker.test/announce?info_hash=a&downloaded=10&uploaded=20&left=0"),
            settings);
        transformer.Transform(
            new Uri("http://tracker.test/announce?info_hash=b&downloaded=30&uploaded=40&left=50"),
            settings);
        transformer.ObserveTrackerResponse("a", new TrackerResponse(7, 3, 60, null));

        Assert.True(transformer.ResetTorrent("a"));
        var remaining = Assert.Single(transformer.GetSnapshots());
        Assert.Equal("b", remaining.InfoHash);
        Assert.False(transformer.ResetTorrent("a"));
        Assert.True(transformer.HasActiveDownloads);
    }

    [Fact]
    public void Snapshots_IncludeSeedsAndLeechersFromTrackerResponse()
    {
        var transformer = new AnnounceTransformer(new FixedRandomSource());
        transformer.Transform(
            new Uri("http://tracker.test/announce?info_hash=a&downloaded=1&uploaded=2&left=3"),
            new RatioGhostSettings());

        transformer.ObserveTrackerResponse("a", new TrackerResponse(11, 7, 60, null));

        var snapshot = Assert.Single(transformer.GetSnapshots());
        Assert.Equal(11, snapshot.CompletePeers);
        Assert.Equal(7, snapshot.IncompletePeers);
    }

    private sealed class FixedRandomSource(double value = 0) : IRandomSource
    {
        public double NextDouble() => value;
    }
}
