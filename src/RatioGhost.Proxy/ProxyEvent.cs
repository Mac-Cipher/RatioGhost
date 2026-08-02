using RatioGhost.Core.Announcements;

namespace RatioGhost.Proxy;

public sealed record ProxyEvent(
    DateTimeOffset Timestamp,
    AnnounceDisposition Disposition,
    string Message,
    Uri? Target = null,
    string? InfoHash = null);
