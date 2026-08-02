using RatioGhost.Core.Announcements;
using RatioGhost.Core.Configuration;

namespace RatioGhost.Core.Tests;

public sealed class SessionStatisticsTests
{
    [Fact]
    public void StartAndAddTotals_PreservesImportedLifetimeStatistics()
    {
        var settings = SessionStatistics.StartSession(new RatioGhostSettings
        {
            Sessions = 17,
            LifetimeRuntimeSeconds = 100,
            LifetimeActualDownloaded = 1000,
            LifetimeActualUploaded = 2000,
            LifetimeReportedDownloaded = 3000,
            LifetimeReportedUploaded = 4000
        });
        var snapshot = new TorrentSnapshot(
            "hash", "tracker", 0, 0, 0, 0, 0, 0,
            ActualDownloadedTotal: 10,
            ActualUploadedTotal: 20,
            ReportedDownloadedTotal: 30,
            ReportedUploadedTotal: 40,
            CompletePeers: 0,
            IncompletePeers: 0,
            LastAnnounce: null);

        var result = SessionStatistics.AddSessionTotals(settings, [snapshot], TimeSpan.FromSeconds(50));

        Assert.Equal(18, result.Sessions);
        Assert.Equal(150, result.LifetimeRuntimeSeconds);
        Assert.Equal(1010, result.LifetimeActualDownloaded);
        Assert.Equal(2020, result.LifetimeActualUploaded);
        Assert.Equal(3030, result.LifetimeReportedDownloaded);
        Assert.Equal(4040, result.LifetimeReportedUploaded);
    }
}
