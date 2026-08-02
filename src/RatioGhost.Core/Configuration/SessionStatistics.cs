using RatioGhost.Core.Announcements;

namespace RatioGhost.Core.Configuration;

public static class SessionStatistics
{
    public static RatioGhostSettings StartSession(RatioGhostSettings settings) =>
        settings with { Sessions = checked(settings.Sessions + 1) };

    public static RatioGhostSettings AddSessionTotals(
        RatioGhostSettings settings,
        IReadOnlyCollection<TorrentSnapshot> snapshots,
        TimeSpan elapsed)
    {
        return settings with
        {
            LifetimeRuntimeSeconds = AddNonNegative(
                settings.LifetimeRuntimeSeconds,
                Math.Max(0, (long)elapsed.TotalSeconds)),
            LifetimeActualDownloaded = AddNonNegative(
                settings.LifetimeActualDownloaded,
                snapshots.Sum(item => item.ActualDownloadedTotal)),
            LifetimeActualUploaded = AddNonNegative(
                settings.LifetimeActualUploaded,
                snapshots.Sum(item => item.ActualUploadedTotal)),
            LifetimeReportedDownloaded = AddNonNegative(
                settings.LifetimeReportedDownloaded,
                snapshots.Sum(item => item.ReportedDownloadedTotal)),
            LifetimeReportedUploaded = AddNonNegative(
                settings.LifetimeReportedUploaded,
                snapshots.Sum(item => item.ReportedUploadedTotal))
        };
    }

    private static long AddNonNegative(long baseline, long delta) =>
        Math.Max(0, checked(baseline + delta));
}
