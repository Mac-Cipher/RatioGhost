namespace RatioGhost.Core.Announcements;

public enum AnnounceDisposition
{
    Forwarded,
    Rewritten,
    BlockedNonTracker,
    RejectedInvalid
}

public sealed record AnnounceTransformResult(
    AnnounceDisposition Disposition,
    Uri? Target,
    string Message,
    string? InfoHash = null);

public sealed record TorrentSnapshot(
    string InfoHash,
    string Tracker,
    long ActualDownloaded,
    long ActualUploaded,
    long ActualLeft,
    long ReportedDownloaded,
    long ReportedUploaded,
    long ReportedLeft,
    long ActualDownloadedTotal,
    long ActualUploadedTotal,
    long ReportedDownloadedTotal,
    long ReportedUploadedTotal,
    int CompletePeers,
    int IncompletePeers,
    DateTimeOffset? LastAnnounce);

public interface IRandomSource
{
    double NextDouble();
}

public sealed class SystemRandomSource : IRandomSource
{
    public double NextDouble() => Random.Shared.NextDouble();
}
