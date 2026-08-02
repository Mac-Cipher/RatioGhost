using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using RatioGhost.Core.Announcements;
using RatioGhost.Core.Configuration;
using RatioGhost.Core.Platform;
using RatioGhost.Proxy;

namespace RatioGhost.Desktop;

public sealed class MainWindow : Window
{
    private readonly ISettingsStore _store;
    private readonly IAutostartService _autostart;
    private readonly ICertificateAuthorityService _certificates;
    private readonly IProxyDebugLogger? _debugLogger;
    private readonly Action _shutdown;
    private readonly AnnounceTransformer _transformer = new();
    private readonly ListBox _activity = new();
    private readonly ListBox _torrents = new();
    private readonly TextBlock _status = new();
    private readonly Button _toggle = new();
    private readonly Button _pause = new();
    private readonly Button _hide = new();
    private readonly TextBox _port = new();
    private readonly TextBox _minimumPeers = new();
    private readonly TextBox _downloadRatioMin = new();
    private readonly TextBox _downloadRatioMax = new();
    private readonly TextBox _uploadRatioMin = new();
    private readonly TextBox _uploadRatioMax = new();
    private readonly TextBox _boost = new();
    private readonly TextBox _boostChance = new();
    private readonly CheckBox _onlyTrackers = new();
    private readonly CheckBox _onlyLocal = new();
    private readonly CheckBox _proxyDebugLogging = new();
    private readonly CheckBox _noDownload = new();
    private readonly CheckBox _pretendSeed = new();
    private readonly CheckBox _autoStart = new();
    private readonly CheckBox _startMinimized = new();
    private readonly CheckBox _certificateConsent = new();
    private readonly TextBlock _certificateStatus = new();
    private RatioGhostSettings _settings = new();
    private HttpProxyServer? _proxy;
    private bool _exiting;
    private bool _paused;
    private bool _sessionPersisted;
    private DateTimeOffset _sessionStarted;

    public MainWindow(
        ISettingsStore store,
        IAutostartService autostart,
        ICertificateAuthorityService certificates,
        Action shutdown,
        IProxyDebugLogger? debugLogger = null)
    {
        _store = store;
        _autostart = autostart;
        _certificates = certificates;
        _debugLogger = debugLogger;
        _shutdown = shutdown;
        Title = ResolveWindowTitle(OperatingSystem.IsWindows());
        Width = 880;
        Height = 650;
        MinWidth = 720;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = BuildContent();
        Closing += OnClosing;
        Opened += OnOpened;
    }

    internal static string ResolveWindowTitle(bool isWindows) =>
        isWindows ? "RatioGhost .NET — Windows milestone" : "RatioGhost .NET";

    internal static bool ShouldStartMinimized(
        bool trayAvailable,
        bool startMinimizedSetting,
        bool minimizedCommandLine) =>
        trayAvailable && (startMinimizedSetting || minimizedCommandLine);

    internal static bool ShouldHideOnWindowClose(bool trayAvailable) => trayAvailable;

    private Control BuildContent()
    {
        _status.Text = "Loading configuration…";
        _status.VerticalAlignment = VerticalAlignment.Center;
        _toggle.Content = "Start proxy";
        _toggle.Click += async (_, _) => await ToggleProxyAsync();
        _pause.Content = "Pause rewriting";
        _pause.Click += (_, _) => TogglePause();
        var save = new Button { Content = "Save and apply" };
        save.Click += async (_, _) => await SaveAndApplyAsync();
        _hide.Content = "Hide to tray";
        _hide.IsVisible = ShouldHideOnWindowClose(IsTrayAvailable());
        _hide.Click += (_, _) => Hide();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { _toggle, _pause, save, _hide, _status }
        };

        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Activity", Content = BuildActivityTab() },
                new TabItem { Header = "Torrents", Content = BuildTorrentsTab() },
                new TabItem { Header = "Options", Content = BuildOptionsTab() },
                new TabItem { Header = "Platform", Content = BuildPlatformTab() }
            }
        };

        return new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 12,
            Children =
            {
                toolbar,
                Place(tabs, row: 1)
            }
        };
    }

    private Control BuildActivityTab()
    {
        return new Border { Padding = new Thickness(8), Child = _activity };
    }

    private Control BuildTorrentsTab()
    {
        var copyHash = new MenuItem { Header = "Copy Info Hash" };
        copyHash.Click += async (_, _) => await CopySelectedTorrentHashAsync();
        var resetStatistics = new MenuItem { Header = "Reset Statistics" };
        resetStatistics.Click += async (_, _) => await ResetSelectedTorrentAsync();
        _torrents.ContextMenu = new ContextMenu
        {
            Items = { copyHash, resetStatistics }
        };

        return new Border
        {
            Padding = new Thickness(8),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                RowSpacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Hash | tracker | seeds/leechers | status | actual down/up/left | reported down/up/left | last announce",
                        FontWeight = Avalonia.Media.FontWeight.SemiBold
                    },
                    Place(_torrents, row: 1)
                }
            }
        };
    }

    private Control BuildOptionsTab()
    {
        var form = new Grid
        {
            Margin = new Thickness(16),
            ColumnDefinitions = new ColumnDefinitions("260,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            RowSpacing = 10,
            ColumnSpacing = 14
        };
        AddField(form, 0, "HTTP proxy port", _port);
        AddField(form, 1, "Minimum leechers", _minimumPeers);
        AddField(form, 2, "Upload/download multiplier min", _downloadRatioMin);
        AddField(form, 3, "Upload/download multiplier max", _downloadRatioMax);
        AddField(form, 4, "Upload/upload multiplier min", _uploadRatioMin);
        AddField(form, 5, "Upload/upload multiplier max", _uploadRatioMax);
        AddField(form, 6, "Boost maximum (KiB/s)", _boost);
        AddField(form, 7, "Boost chance (%)", _boostChance);
        AddField(form, 8, "Accept tracker traffic only", _onlyTrackers);
        AddField(form, 9, "Listen on localhost only", _onlyLocal);
        AddField(form, 10, "Write redacted proxy debug log", _proxyDebugLogging);
        AddField(form, 11, "Report download as zero", _noDownload);
        AddField(form, 12, "Pretend to seed", _pretendSeed);
        return new ScrollViewer { Content = form };
    }

    private Control BuildPlatformTab()
    {
        _autoStart.Content = "Start automatically with the user session";
        _autoStart.IsEnabled = _autostart.Capability.IsSupported;
        _startMinimized.Content = "Start minimized to tray";
        _startMinimized.IsEnabled = IsTrayAvailable();
        _certificateConsent.Content =
            "I understand that RatioGhost will add its installation CA to my Windows user trust store.";
        _certificateConsent.IsVisible = _certificates.Capability.IsSupported;
        var trustCertificate = new Button
        {
            Content = "Enable HTTPS interception",
            IsEnabled = _certificates.Capability.IsSupported
        };
        trustCertificate.Click += async (_, _) => await EnableHttpsAsync();
        var removeCertificate = new Button
        {
            Content = "Remove CA trust",
            IsEnabled = _certificates.Capability.IsSupported
        };
        removeCertificate.Click += async (_, _) => await DisableHttpsAsync();
        return new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"Autostart: {_autostart.Capability.Description}",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                _autoStart,
                _startMinimized,
                new Separator(),
                new TextBlock
                {
                    Text = $"HTTPS certificates: {_certificates.Capability.Description}",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                _certificateStatus,
                _certificateConsent,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { trustCertificate, removeCertificate }
                }
            }
        };
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            _sessionStarted = DateTimeOffset.UtcNow;
            var loadedSettings = await _store.LoadAsync();
            _settings = SessionStatistics.StartSession(loadedSettings);
            await _store.SaveAsync(_settings);
            PopulateForm(_settings);
            AddActivity(_store.LastLoadSource switch
            {
                SettingsLoadSource.LegacyTcl =>
                    "Imported settings.dat into settings.json; the Tcl file was left unchanged.",
                SettingsLoadSource.LegacyTclBackup =>
                    "Imported settings.dat.bak after the primary Tcl settings were invalid; both Tcl files were left unchanged.",
                SettingsLoadSource.JsonBackup =>
                    "Loaded the JSON settings backup because settings.json was invalid.",
                SettingsLoadSource.Defaults => "Using default configuration.",
                _ => "Configuration loaded."
            });
            if (_autostart.Capability.IsSupported)
                _autoStart.IsChecked = await _autostart.IsEnabledAsync();
            await RefreshCertificateStatusAsync();
            await StartProxyAsync();
            if (ShouldStartMinimized(
                    IsTrayAvailable(),
                    _settings.StartMinimized,
                    Environment.GetCommandLineArgs().Contains("--minimized")))
                Hide();
        }
        catch (Exception exception)
        {
            AddActivity($"Startup error: {exception.Message}");
            _status.Text = "Startup failed";
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting)
            return;
        e.Cancel = true;
        if (ShouldHideOnWindowClose(IsTrayAvailable()))
        {
            Hide();
            return;
        }

        // A non-Windows platform currently has no validated native tray
        // backend. Closing its only window must therefore exit instead of
        // hiding the process with no discoverable way to restore it.
        await PrepareForExitAsync();
    }

    public async Task ToggleProxyAsync()
    {
        if (_proxy?.IsRunning == true)
            await StopProxyAsync();
        else
            await StartProxyAsync();
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void TogglePause()
    {
        _paused = !_paused;
        _pause.Content = _paused ? "Resume rewriting" : "Pause rewriting";
        _status.Text = _paused
            ? $"Paused on 127.0.0.1:{_proxy?.BoundPort ?? _settings.ListenPort}"
            : _proxy?.IsRunning == true
                ? $"HTTP/HTTPS active on 127.0.0.1:{_proxy.BoundPort}"
                : "Proxy stopped";
        AddActivity(_paused
            ? "Rewriting paused; counters will not regress below previously reported values."
            : "Rewriting resumed.");
    }

    public async Task PrepareForExitAsync()
    {
        _exiting = true;
        await PersistSessionTotalsAsync();
        await StopProxyAsync();
        if (_certificates is IDisposable disposableCertificates)
            disposableCertificates.Dispose();
        Close();
        _shutdown();
    }

    private async Task PersistSessionTotalsAsync()
    {
        if (_sessionPersisted)
            return;
        _sessionPersisted = true;
        var snapshots = _transformer.GetSnapshots();
        _settings = SessionStatistics.AddSessionTotals(
            _settings,
            snapshots,
            DateTimeOffset.UtcNow - _sessionStarted);
        await _store.SaveAsync(_settings);
    }

    private async Task EnableHttpsAsync()
    {
        if (_certificateConsent.IsChecked != true)
        {
            AddActivity("HTTPS was not enabled: explicit CA trust confirmation is required.");
            return;
        }
        try
        {
            await _certificates.RequestTrustAsync();
            await RefreshCertificateStatusAsync();
            AddActivity("HTTPS interception enabled for the current Windows user.");
        }
        catch (Exception exception)
        {
            AddActivity($"Could not enable HTTPS: {exception.Message}");
        }
    }

    private async Task DisableHttpsAsync()
    {
        try
        {
            await _certificates.RemoveTrustAsync();
            _certificateConsent.IsChecked = false;
            await RefreshCertificateStatusAsync();
            AddActivity("RatioGhost CA trust removed from the current Windows user.");
        }
        catch (Exception exception)
        {
            AddActivity($"Could not remove CA trust: {exception.Message}");
        }
    }

    private async Task RefreshCertificateStatusAsync()
    {
        _certificateStatus.Text = !_certificates.Capability.IsSupported
            ? "HTTPS interception unavailable."
            : await _certificates.IsTrustedAsync()
                ? "HTTPS interception: enabled for this installation."
                : "HTTPS interception: disabled until you explicitly trust this installation CA.";
    }

    private async Task SaveAndApplyAsync()
    {
        try
        {
            var previousPort = _settings.ListenPort;
            var previousLocalOnly = _settings.OnlyLocalConnections;
            _settings = ReadForm().Validate();
            await _store.SaveAsync(_settings);
            if (_autostart.Capability.IsSupported)
                await _autostart.SetEnabledAsync(_settings.AutoStart);
            if (_proxy?.IsRunning == true &&
                (previousPort != _settings.ListenPort || previousLocalOnly != _settings.OnlyLocalConnections))
            {
                await StopProxyAsync();
                await StartProxyAsync();
            }
            AddActivity("Configuration saved.");
        }
        catch (Exception exception)
        {
            AddActivity($"Configuration error: {exception.Message}");
        }
    }

    private async Task StartProxyAsync()
    {
        if (_proxy?.IsRunning == true)
            return;
        _proxy = new HttpProxyServer(
            _transformer,
            () => _settings,
            _certificates,
            isPaused: () => _paused,
            debugLogger: _debugLogger,
            isDebugLogging: () => _settings.ProxyDebugLogging);
        _proxy.Activity += OnProxyActivity;
        try
        {
            await _proxy.StartAsync();
            _toggle.Content = "Stop proxy";
            _status.Text = $"HTTP/HTTPS active on 127.0.0.1:{_proxy.BoundPort}";
        }
        catch
        {
            await _proxy.DisposeAsync();
            _proxy = null;
            throw;
        }
    }

    private async Task StopProxyAsync()
    {
        if (_proxy is null)
            return;
        _proxy.Activity -= OnProxyActivity;
        await _proxy.DisposeAsync();
        _proxy = null;
        _toggle.Content = "Start proxy";
        _status.Text = "Proxy stopped";
    }

    private void OnProxyActivity(object? sender, ProxyEvent activity) =>
        Dispatcher.UIThread.Post(() =>
        {
            AddActivity($"{activity.Timestamp:HH:mm:ss}  {activity.Disposition,-18} {activity.Message}");
            RefreshTorrents();
        });

    private void RefreshTorrents()
    {
        var selectedHash = (_torrents.SelectedItem as TorrentRow)?.Snapshot.InfoHash;
        _torrents.Items.Clear();
        foreach (var torrent in _transformer.GetSnapshots())
        {
            var row = new TorrentRow(torrent, GetTorrentStatus(torrent));
            _torrents.Items.Add(row);
            if (torrent.InfoHash == selectedHash)
                _torrents.SelectedItem = row;
        }
    }

    private string GetTorrentStatus(TorrentSnapshot torrent)
    {
        if (_paused)
            return "Paused";
        if (_transformer.IsSeedOnlyStandby)
            return "Seed-only standby";
        return torrent.IncompletePeers < _settings.MinimumPeers
            ? "Waiting for leechers"
            : "Ready";
    }

    private async Task CopySelectedTorrentHashAsync()
    {
        if (_torrents.SelectedItem is not TorrentRow row)
        {
            AddActivity("Select a torrent before copying its info hash.");
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            AddActivity("Clipboard is unavailable.");
            return;
        }
        await clipboard.SetTextAsync(row.Snapshot.InfoHash);
        AddActivity($"Copied info hash to clipboard: {row.Snapshot.InfoHash}");
    }

    private async Task ResetSelectedTorrentAsync()
    {
        if (_torrents.SelectedItem is not TorrentRow row)
        {
            AddActivity("Select a torrent before resetting its statistics.");
            return;
        }
        if (!await ConfirmResetAsync(row.Snapshot.InfoHash))
            return;

        if (_transformer.ResetTorrent(row.Snapshot.InfoHash))
        {
            RefreshTorrents();
            AddActivity($"Reset stats for torrent hash: {AbbreviateHash(row.Snapshot.InfoHash)}");
        }
    }

    private async Task<bool> ConfirmResetAsync(string infoHash)
    {
        var confirmed = false;
        var dialog = new Window
        {
            Title = "Reset Statistics",
            Width = 460,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var yes = new Button { Content = "Reset", MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        yes.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = $"Reset all tracked statistics for {AbbreviateHash(infoHash)}?",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancel, yes }
                }
            }
        };
        await dialog.ShowDialog(this);
        return confirmed;
    }

    private static string AbbreviateHash(string infoHash) =>
        infoHash.Length <= 8 ? infoHash : $"{infoHash[..8]}…";

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (Math.Abs(value) >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return index == 0
            ? $"{value:0}{suffixes[index]}"
            : $"{value:0.0}{suffixes[index]}";
    }

    private void AddActivity(string message)
    {
        _activity.Items.Add(message);
        if (_activity.ItemCount > 500)
            _activity.Items.RemoveAt(0);
        _activity.ScrollIntoView(_activity.ItemCount - 1);
    }

    private void PopulateForm(RatioGhostSettings settings)
    {
        _port.Text = settings.ListenPort.ToString(CultureInfo.InvariantCulture);
        _minimumPeers.Text = settings.MinimumPeers.ToString(CultureInfo.InvariantCulture);
        _downloadRatioMin.Text = settings.UploadPerDownloadMinimum.ToString(CultureInfo.InvariantCulture);
        _downloadRatioMax.Text = settings.UploadPerDownloadMaximum.ToString(CultureInfo.InvariantCulture);
        _uploadRatioMin.Text = settings.UploadPerUploadMinimum.ToString(CultureInfo.InvariantCulture);
        _uploadRatioMax.Text = settings.UploadPerUploadMaximum.ToString(CultureInfo.InvariantCulture);
        _boost.Text = settings.BoostKiBPerSecond.ToString(CultureInfo.InvariantCulture);
        _boostChance.Text = settings.BoostChancePercent.ToString(CultureInfo.InvariantCulture);
        _onlyTrackers.IsChecked = settings.OnlyTrackerTraffic;
        _onlyLocal.IsChecked = settings.OnlyLocalConnections;
        _proxyDebugLogging.IsChecked = settings.ProxyDebugLogging;
        _noDownload.IsChecked = settings.ReportDownloadAsZero;
        _pretendSeed.IsChecked = settings.PretendToSeed;
        _autoStart.IsChecked = settings.AutoStart;
        _startMinimized.IsChecked = IsTrayAvailable() && settings.StartMinimized;
    }

    private RatioGhostSettings ReadForm() => _settings with
    {
        ListenPort = ParseInt(_port, "HTTP proxy port"),
        MinimumPeers = ParseInt(_minimumPeers, "Minimum leechers"),
        UploadPerDownloadMinimum = ParseDouble(_downloadRatioMin, "Upload/download minimum"),
        UploadPerDownloadMaximum = ParseDouble(_downloadRatioMax, "Upload/download maximum"),
        UploadPerUploadMinimum = ParseDouble(_uploadRatioMin, "Upload/upload minimum"),
        UploadPerUploadMaximum = ParseDouble(_uploadRatioMax, "Upload/upload maximum"),
        BoostKiBPerSecond = ParseDouble(_boost, "Boost"),
        BoostChancePercent = ParseInt(_boostChance, "Boost chance"),
        OnlyTrackerTraffic = _onlyTrackers.IsChecked == true,
        OnlyLocalConnections = _onlyLocal.IsChecked == true,
        ProxyDebugLogging = _proxyDebugLogging.IsChecked == true,
        ReportDownloadAsZero = _noDownload.IsChecked == true,
        PretendToSeed = _pretendSeed.IsChecked == true,
        AutoStart = _autoStart.IsChecked == true,
        StartMinimized = IsTrayAvailable() && _startMinimized.IsChecked == true
    };

    private static bool IsTrayAvailable() => App.ShouldCreateTrayIcon(OperatingSystem.IsWindows());

    private static int ParseInt(TextBox input, string name) =>
        int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"{name} must be an integer.");

    private static double ParseDouble(TextBox input, string name) =>
        double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"{name} must be a number using '.' as decimal separator.");

    private static void AddField(Grid grid, int row, string label, Control editor)
    {
        grid.Children.Add(Place(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center
        }, row));
        grid.Children.Add(Place(editor, row, 1));
    }

    private static T Place<T>(T control, int row = 0, int column = 0) where T : Control
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        return control;
    }

    private sealed record TorrentRow(TorrentSnapshot Snapshot, string Status)
    {
        public override string ToString()
        {
            var lastAnnounce = Snapshot.LastAnnounce?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                               ?? "—";
            return $"{Snapshot.InfoHash} | {Snapshot.Tracker} | " +
                   $"{Snapshot.CompletePeers}/{Snapshot.IncompletePeers} | {Status} | " +
                   $"{FormatBytes(Snapshot.ActualDownloaded)}/{FormatBytes(Snapshot.ActualUploaded)}/{FormatBytes(Snapshot.ActualLeft)} | " +
                   $"{FormatBytes(Snapshot.ReportedDownloaded)}/{FormatBytes(Snapshot.ReportedUploaded)}/{FormatBytes(Snapshot.ReportedLeft)} | " +
                   lastAnnounce;
        }
    }
}
