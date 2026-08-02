using System.Globalization;
using RatioGhost.Core.Configuration;

namespace RatioGhost.Core.Announcements;

public sealed class AnnounceTransformer
{
    private readonly object _gate = new();
    private readonly IRandomSource _random;
    private readonly Dictionary<string, TorrentState> _torrents = new(StringComparer.Ordinal);

    public AnnounceTransformer(IRandomSource? random = null) => _random = random ?? new SystemRandomSource();

    public AnnounceTransformResult Transform(
        Uri target,
        RatioGhostSettings settings,
        bool paused = false,
        DateTimeOffset? now = null)
    {
        settings.Validate();
        if (target.Scheme is not ("http" or "https"))
            return new(AnnounceDisposition.RejectedInvalid, null, "Unsupported target scheme.");

        var resource = target.PathAndQuery + target.Fragment;
        var query = QueryStringEditor.Parse(resource);
        if (!query.Contains("info_hash"))
        {
            return settings.OnlyTrackerTraffic
                ? new(AnnounceDisposition.BlockedNonTracker, null, "Blocked non-tracker traffic.")
                : new(AnnounceDisposition.Forwarded, target, "Forwarding non-tracker traffic.");
        }

        var hash = query.GetLast("info_hash") ?? string.Empty;
        var downloadedText = query.GetLast("downloaded");
        var uploadedText = query.GetLast("uploaded");
        var leftText = query.GetLast("left");
        var eventName = query.GetLast("event") ?? string.Empty;
        if (!TryCounter(downloadedText, out var downloaded) ||
            !TryCounter(uploadedText, out var uploaded) ||
            !TryCounter(leftText, out var left))
            return new(AnnounceDisposition.Forwarded, target, "Forwarding non-announce tracker traffic.", hash);

        lock (_gate)
        {
            var timestamp = now ?? DateTimeOffset.UtcNow;
            var state = GetState(hash);
            state.Tracker = target.IsDefaultPort ? target.Host : $"{target.Host}:{target.Port}";
            var actualDownDifference = state.ActualLast is null ? downloaded : downloaded - state.ActualLast.Downloaded;
            var actualUpDifference = state.ActualLast is null ? uploaded : uploaded - state.ActualLast.Uploaded;
            if (eventName.Equals("started", StringComparison.Ordinal))
            {
                actualDownDifference = downloaded;
                actualUpDifference = uploaded;
            }

            state.ActualFirst ??= new Counters(downloaded, uploaded, left);
            if (state.ActualLast is not null && !eventName.Equals("started", StringComparison.Ordinal))
            {
                state.ActualDownloadedTotal += downloaded - state.ActualLast.Downloaded;
                state.ActualUploadedTotal += uploaded - state.ActualLast.Uploaded;
            }
            state.ActualLast = new Counters(downloaded, uploaded, left);

            var reportedPrevious = eventName.Equals("started", StringComparison.Ordinal)
                ? null
                : state.ReportedLast;
            var reportedPreviousUp = reportedPrevious?.Uploaded ?? 0;
            var reportedPreviousDown = reportedPrevious?.Downloaded ?? 0;
            var elapsedSeconds = state.ReportedAt is null || eventName.Equals("started", StringComparison.Ordinal)
                ? 0
                : Math.Max(0, (timestamp - state.ReportedAt.Value).TotalSeconds);

            var rewrittenResource = resource;
            if (paused)
            {
                uploaded = Math.Max(uploaded, reportedPreviousUp);
                downloaded = Math.Max(downloaded, reportedPreviousDown);
            }
            else
            {
                if (settings.ReportDownloadAsZero)
                {
                    downloaded = 0;
                    left = state.ActualFirst.Left;
                    if (eventName.Equals("completed", StringComparison.Ordinal))
                    {
                        rewrittenResource = QueryStringEditor.Parse(rewrittenResource)
                            .Rewrite(new Dictionary<string, string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "event" });
                    }
                }

                if (settings.PretendToSeed)
                    left = 0;

                if (state.IncompletePeers >= settings.MinimumPeers)
                {
                    var downRatio = BetweenLegacyBounds(
                        settings.UploadPerDownloadMaximum, settings.UploadPerDownloadMinimum);
                    var upRatio = BetweenLegacyBounds(
                        settings.UploadPerUploadMaximum, settings.UploadPerUploadMinimum);
                    var calculated = reportedPreviousUp + actualUpDifference +
                                     downRatio * actualDownDifference +
                                     upRatio * actualUpDifference;
                    if (_random.NextDouble() * 100 < settings.BoostChancePercent)
                        calculated += settings.BoostKiBPerSecond * 1024 * elapsedSeconds * _random.NextDouble();
                    uploaded = checked((long)Math.Round(calculated, MidpointRounding.ToEven));
                }
                else
                {
                    uploaded = checked(reportedPreviousUp + actualUpDifference);
                }
            }

            if (!eventName.Equals("started", StringComparison.Ordinal) && uploaded < reportedPreviousUp)
                return new(AnnounceDisposition.RejectedInvalid, null,
                    "Upload regression rejected to preserve tracker consistency.", hash);

            var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["downloaded"] = downloaded.ToString(CultureInfo.InvariantCulture),
                ["uploaded"] = uploaded.ToString(CultureInfo.InvariantCulture),
                ["left"] = left.ToString(CultureInfo.InvariantCulture)
            };
            rewrittenResource = QueryStringEditor.Parse(rewrittenResource).Rewrite(updates);
            var builder = new UriBuilder(target)
            {
                Path = rewrittenResource.Split('?', '#')[0],
                Query = ExtractQuery(rewrittenResource),
                Fragment = ExtractFragment(rewrittenResource)
            };

            if (state.ReportedLast is not null && !eventName.Equals("started", StringComparison.Ordinal))
            {
                state.ReportedDownloadedTotal += downloaded - state.ReportedLast.Downloaded;
                state.ReportedUploadedTotal += uploaded - state.ReportedLast.Uploaded;
            }
            state.ReportedLast = new Counters(downloaded, uploaded, left);
            state.ReportedAt = timestamp;
            return new(AnnounceDisposition.Rewritten, builder.Uri, "Announce statistics rewritten.", hash);
        }
    }

    public void ObserveTrackerResponse(string infoHash, TrackerResponse response)
    {
        lock (_gate)
        {
            var state = GetState(infoHash);
            if (response.Complete is not null)
                state.CompletePeers = response.Complete.Value;
            if (response.Incomplete is not null)
                state.IncompletePeers = response.Incomplete.Value;
        }
    }

    public bool ResetTorrent(string infoHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(infoHash);
        lock (_gate)
            return _torrents.Remove(infoHash);
    }

    public bool HasActiveDownloads
    {
        get
        {
            lock (_gate)
                return _torrents.Values.Any(state => state.ActualLast?.Left > 0);
        }
    }

    public bool IsSeedOnlyStandby
    {
        get
        {
            lock (_gate)
                return _torrents.Count > 0 && !_torrents.Values.Any(state => state.ActualLast?.Left > 0);
        }
    }

    public IReadOnlyList<TorrentSnapshot> GetSnapshots()
    {
        lock (_gate)
        {
            return _torrents.Select(item =>
                {
                    var actual = item.Value.ActualLast ?? new Counters(0, 0, 0);
                    var reported = item.Value.ReportedLast ?? new Counters(0, 0, 0);
                    return new TorrentSnapshot(
                        item.Key,
                        item.Value.Tracker,
                        actual.Downloaded,
                        actual.Uploaded,
                        actual.Left,
                        reported.Downloaded,
                        reported.Uploaded,
                        reported.Left,
                        item.Value.ActualDownloadedTotal,
                        item.Value.ActualUploadedTotal,
                        item.Value.ReportedDownloadedTotal,
                        item.Value.ReportedUploadedTotal,
                        item.Value.CompletePeers,
                        item.Value.IncompletePeers,
                        item.Value.ReportedAt);
                })
                .OrderByDescending(snapshot => snapshot.LastAnnounce)
                .ToArray();
        }
    }

    private static bool TryCounter(string? value, out long result) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result >= 0;

    private double BetweenLegacyBounds(double lowerExpression, double upperExpression) =>
        lowerExpression + _random.NextDouble() * (upperExpression - lowerExpression);

    private TorrentState GetState(string hash)
    {
        if (!_torrents.TryGetValue(hash, out var state))
            _torrents.Add(hash, state = new TorrentState());
        return state;
    }

    private static string ExtractQuery(string resource)
    {
        var query = resource.IndexOf('?');
        if (query < 0) return string.Empty;
        var fragment = resource.IndexOf('#', query);
        return fragment < 0 ? resource[(query + 1)..] : resource[(query + 1)..fragment];
    }

    private static string ExtractFragment(string resource)
    {
        var fragment = resource.IndexOf('#');
        return fragment < 0 ? string.Empty : resource[(fragment + 1)..];
    }

    private sealed class TorrentState
    {
        public string Tracker { get; set; } = string.Empty;
        public Counters? ActualFirst { get; set; }
        public Counters? ActualLast { get; set; }
        public Counters? ReportedLast { get; set; }
        public DateTimeOffset? ReportedAt { get; set; }
        public int IncompletePeers { get; set; }
        public long ActualDownloadedTotal { get; set; }
        public long ActualUploadedTotal { get; set; }
        public long ReportedDownloadedTotal { get; set; }
        public long ReportedUploadedTotal { get; set; }
        public int CompletePeers { get; set; }
    }

    private sealed record Counters(long Downloaded, long Uploaded, long Left);
}
