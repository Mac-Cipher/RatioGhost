namespace RatioGhost.Core.Configuration;

public sealed record RatioGhostSettings
{
    public int ListenPort { get; init; } = 3773;
    public bool OnlyTrackerTraffic { get; init; } = true;
    public bool OnlyLocalConnections { get; init; } = true;
    public bool ProxyDebugLogging { get; init; }
    public bool StartMinimized { get; init; }
    public bool AutoStart { get; init; }
    public int MinimumPeers { get; init; } = 5;
    public double UploadPerDownloadMinimum { get; init; } = 0.00;
    public double UploadPerDownloadMaximum { get; init; } = 0.05;
    public double UploadPerUploadMinimum { get; init; } = 4.0;
    public double UploadPerUploadMaximum { get; init; } = 8.0;
    public double BoostKiBPerSecond { get; init; } = 15;
    public int BoostChancePercent { get; init; } = 5;
    public bool ReportDownloadAsZero { get; init; }
    public bool PretendToSeed { get; init; }
    public long LifetimeRuntimeSeconds { get; init; }
    public long LifetimeActualDownloaded { get; init; }
    public long LifetimeActualUploaded { get; init; }
    public long LifetimeReportedDownloaded { get; init; }
    public long LifetimeReportedUploaded { get; init; }
    public int Sessions { get; init; }

    public RatioGhostSettings Validate()
    {
        if (ListenPort is < 1 or > 65534)
            throw new ArgumentOutOfRangeException(nameof(ListenPort));
        if (MinimumPeers is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(MinimumPeers));
        if (BoostChancePercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(BoostChancePercent));
        if (UploadPerDownloadMinimum < 0 || UploadPerDownloadMaximum < 0 ||
            UploadPerUploadMinimum < 0 || UploadPerUploadMaximum < 0 ||
            BoostKiBPerSecond < 0)
            throw new ArgumentOutOfRangeException(nameof(RatioGhostSettings));
        if (LifetimeRuntimeSeconds < 0 || LifetimeActualDownloaded < 0 ||
            LifetimeActualUploaded < 0 || LifetimeReportedDownloaded < 0 ||
            LifetimeReportedUploaded < 0 || Sessions < 0)
            throw new ArgumentOutOfRangeException(nameof(RatioGhostSettings));
        return this;
    }
}
