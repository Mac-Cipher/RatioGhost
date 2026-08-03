using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
    private readonly StackPanel _torrentsEmptyState = new();
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
        Width = 1100;
        Height = 760;
        MinWidth = 900;
        MinHeight = 620;
        Background = RatioGhostPalette.Canvas;
        Icon = App.CreateAppIcon();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = BuildContent();
        Closing += OnClosing;
        Opened += OnOpened;
    }

    internal static string ResolveWindowTitle(bool isWindows) => "RatioGhost";

    internal static bool ShouldStartMinimized(
        bool trayAvailable,
        bool startMinimizedSetting,
        bool minimizedCommandLine) =>
        trayAvailable && (startMinimizedSetting || minimizedCommandLine);

    internal static bool ShouldHideOnWindowClose(bool trayAvailable) => trayAvailable;

    private Control BuildContent()
    {
        _status.Text = "Loading configuration…";
        _status.Foreground = RatioGhostPalette.Ink;
        _status.FontSize = 12;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.MaxWidth = 250;
        _status.VerticalAlignment = VerticalAlignment.Center;
        StyleButton(_toggle, ButtonTone.Primary, minWidth: 98);
        _toggle.Content = "Start proxy";
        _toggle.Click += async (_, _) => await ToggleProxyAsync();
        StyleButton(_pause, ButtonTone.Secondary, minWidth: 102);
        _pause.Content = "Pause rewriting";
        _pause.Click += (_, _) => TogglePause();
        var save = CreateButton("Save and apply", ButtonTone.Secondary, minWidth: 118);
        save.Click += async (_, _) => await SaveAndApplyAsync();
        StyleButton(_hide, ButtonTone.Quiet, minWidth: 84);
        _hide.Content = "Hide to tray";
        _hide.IsVisible = ShouldHideOnWindowClose(IsTrayAvailable());
        _hide.Click += (_, _) => Hide();

        var tabs = new TabControl
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
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
            Background = RatioGhostPalette.Canvas,
            Margin = new Thickness(26),
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 18,
            Children =
            {
                BuildHeader(save),
                Place(new Border
                {
                    Background = RatioGhostPalette.Surface,
                    BorderBrush = RatioGhostPalette.Border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(16),
                    Padding = new Thickness(8),
                    Child = tabs
                }, row: 1)
            }
        };
    }

    private Control BuildHeader(Button save)
    {
        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                BuildLogo(),
                new StackPanel
                {
                    Spacing = 2,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "RatioGhost",
                            FontSize = 16,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = RatioGhostPalette.Ink
                        },
                        new TextBlock
                        {
                            Text = "Local proxy control",
                            FontSize = 12,
                            Foreground = RatioGhostPalette.Muted
                        }
                    }
                }
            }
        };

        var statusBadge = new Border
        {
            Background = RatioGhostPalette.AccentSoft,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Border
                    {
                        Width = 8,
                        Height = 8,
                        CornerRadius = new CornerRadius(4),
                        Background = RatioGhostPalette.Accent,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    _status
                }
            }
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _toggle, _pause, save, _hide }
        };

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            ColumnSpacing = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Place(brand),
                Place(statusBadge, column: 2),
                Place(actions, column: 3)
            }
        };
    }

    private static Image BuildLogo()
    {
        using var stream = AssetLoader.Open(new Uri("avares://RatioGhost/Assets/logo.png"));
        return new Image
        {
            Source = new Bitmap(stream),
            Width = 126,
            Height = 24,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private Control BuildActivityTab()
    {
        ConfigureList(_activity);
        return BuildTabLayout(
            "Activity",
            "A live view of proxy decisions and rewriting events.",
            BuildListSurface(_activity));
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

        ConfigureList(_torrents);
        _torrentsEmptyState.IsHitTestVisible = false;
        _torrentsEmptyState.HorizontalAlignment = HorizontalAlignment.Center;
        _torrentsEmptyState.VerticalAlignment = VerticalAlignment.Center;
        _torrentsEmptyState.Spacing = 4;
        _torrentsEmptyState.Children.Add(new TextBlock
        {
            Text = "No tracked torrents yet",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = RatioGhostPalette.Ink,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        _torrentsEmptyState.Children.Add(new TextBlock
        {
            Text = "Tracker announcements will appear here automatically.",
            FontSize = 12,
            Foreground = RatioGhostPalette.Muted,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        var torrentSurface = new Border
        {
            Background = RatioGhostPalette.SurfaceSubtle,
            BorderBrush = RatioGhostPalette.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                RowSpacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Hash · tracker · peers · status · transfer counters · last announce",
                        FontSize = 12,
                        Foreground = RatioGhostPalette.Muted,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    Place(_torrents, row: 1),
                    Place(_torrentsEmptyState, row: 1)
                }
            }
        };
        return BuildTabLayout(
            "Torrents",
            "Tracked sessions stay visible here as announcements arrive.",
            torrentSurface);
    }

    private Control BuildOptionsTab()
    {
        ConfigureTextBox(_port, "e.g. 3773");
        ConfigureTextBox(_minimumPeers, "e.g. 5");
        ConfigureTextBox(_downloadRatioMin, "e.g. 0");
        ConfigureTextBox(_downloadRatioMax, "e.g. 0.05");
        ConfigureTextBox(_uploadRatioMin, "e.g. 4");
        ConfigureTextBox(_uploadRatioMax, "e.g. 8");
        ConfigureTextBox(_boost, "e.g. 15");
        ConfigureTextBox(_boostChance, "e.g. 5");
        _onlyTrackers.Content = "Accept tracker traffic only";
        _onlyLocal.Content = "Listen on localhost only";
        _proxyDebugLogging.Content = "Write redacted proxy debug log";
        _noDownload.Content = "Report download as zero";
        _pretendSeed.Content = "Pretend to seed";
        ConfigureCheckBox(_onlyTrackers);
        ConfigureCheckBox(_onlyLocal);
        ConfigureCheckBox(_proxyDebugLogging);
        ConfigureCheckBox(_noDownload);
        ConfigureCheckBox(_pretendSeed);

        var connection = BuildSettingsSection(
            "Connection",
            "Keep the local listener narrow and predictable.",
            BuildSettingsBody(
                BuildFieldGrid(
                    ("HTTP proxy port", (Control)_port),
                    ("Minimum leechers", (Control)_minimumPeers)),
                BuildToggleGroup(_onlyTrackers, _onlyLocal, _proxyDebugLogging)));

        var ratio = BuildSettingsSection(
            "Ratio shaping",
            "Control how announced counters are adjusted over time.",
            BuildFieldGrid(
                ("Upload/download multiplier min", (Control)_downloadRatioMin),
                ("Upload/download multiplier max", (Control)_downloadRatioMax),
                ("Upload/upload multiplier min", (Control)_uploadRatioMin),
                ("Upload/upload multiplier max", (Control)_uploadRatioMax),
                ("Boost maximum (KiB/s)", (Control)_boost),
                ("Boost chance (%)", (Control)_boostChance)));

        var announce = BuildSettingsSection(
            "Announce behavior",
            "Choose the information the proxy reports to trackers.",
            BuildToggleGroup(_noDownload, _pretendSeed));

        var content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(18),
            Children =
            {
                BuildTabHeading("Options", "Tune the proxy without losing the important defaults."),
                connection,
                ratio,
                announce
            }
        };
        return new ScrollViewer
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = content
        };
    }

    private Control BuildPlatformTab()
    {
        _autoStart.Content = "Start automatically with the user session";
        ConfigureCheckBox(_autoStart);
        _autoStart.IsEnabled = _autostart.Capability.IsSupported;
        _startMinimized.Content = "Start minimized to tray";
        ConfigureCheckBox(_startMinimized);
        _startMinimized.IsEnabled = IsTrayAvailable();
        _certificateConsent.Content =
            "I understand that RatioGhost will add its installation CA to my Windows user trust store.";
        ConfigureCheckBox(_certificateConsent);
        _certificateConsent.IsVisible = _certificates.Capability.IsSupported;
        var trustCertificate = CreateButton("Enable HTTPS interception", ButtonTone.Primary, 184);
        trustCertificate.IsEnabled = _certificates.Capability.IsSupported;
        trustCertificate.Click += async (_, _) => await EnableHttpsAsync();
        var removeCertificate = CreateButton("Remove CA trust", ButtonTone.Secondary, 132);
        removeCertificate.IsEnabled = _certificates.Capability.IsSupported;
        removeCertificate.Click += async (_, _) => await DisableHttpsAsync();
        _certificateStatus.Foreground = RatioGhostPalette.Muted;
        _certificateStatus.FontSize = 12;
        _certificateStatus.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var startup = BuildSettingsSection(
            "Startup",
            "Choose how RatioGhost should behave when your session begins.",
            BuildSettingsBody(
                new TextBlock
                {
                    Text = $"Autostart: {_autostart.Capability.Description}",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Foreground = RatioGhostPalette.Muted,
                    FontSize = 12
                },
                _autoStart,
                _startMinimized));

        var certificateActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { trustCertificate, removeCertificate }
        };
        var https = BuildSettingsSection(
            "HTTPS interception",
            "Trust is explicit and scoped to the current Windows user.",
            BuildSettingsBody(
                new TextBlock
                {
                    Text = $"Certificates: {_certificates.Capability.Description}",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Foreground = RatioGhostPalette.Muted,
                    FontSize = 12
                },
                _certificateStatus,
                _certificateConsent,
                certificateActions));

        var content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
            Children =
            {
                BuildTabHeading("Platform", "System integrations and HTTPS trust live here."),
                startup,
                https
            }
        };
        return new ScrollViewer
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = content
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
        _torrentsEmptyState.IsVisible = _torrents.ItemCount == 0;
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
            Width = 480,
            Height = 210,
            CanResize = false,
            Background = RatioGhostPalette.Canvas,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var yes = CreateButton("Reset", ButtonTone.Primary, 90);
        var cancel = CreateButton("Cancel", ButtonTone.Secondary, 90);
        yes.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new Border
        {
            Background = RatioGhostPalette.Surface,
            BorderBrush = RatioGhostPalette.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(12),
            Padding = new Thickness(18),
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Reset all tracked statistics for {AbbreviateHash(infoHash)}?",
                        Foreground = RatioGhostPalette.Ink,
                        FontSize = 14,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, yes }
                    }
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

    private Control BuildTabLayout(string title, string subtitle, Control surface)
    {
        return new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(8),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                RowSpacing = 18,
                Children =
                {
                    BuildTabHeading(title, subtitle),
                    Place(surface, row: 1)
                }
            }
        };
    }

    private static Control BuildTabHeading(string title, string subtitle)
    {
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 21,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = RatioGhostPalette.Ink,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = subtitle,
                    FontSize = 13,
                    Foreground = RatioGhostPalette.Muted,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                }
            }
        };
    }

    private static Border BuildListSurface(Control list)
    {
        return new Border
        {
            Background = RatioGhostPalette.SurfaceSubtle,
            BorderBrush = RatioGhostPalette.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14),
            Child = list
        };
    }

    private static Border BuildSettingsSection(string title, string subtitle, Control body)
    {
        return new Border
        {
            Background = RatioGhostPalette.SurfaceSubtle,
            BorderBrush = RatioGhostPalette.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 3,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = title,
                                FontSize = 15,
                                FontWeight = FontWeight.SemiBold,
                                Foreground = RatioGhostPalette.Ink
                            },
                            new TextBlock
                            {
                                Text = subtitle,
                                FontSize = 12,
                                Foreground = RatioGhostPalette.Muted,
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap
                            }
                        }
                    },
                    body
                }
            }
        };
    }

    private static StackPanel BuildSettingsBody(params Control[] controls)
    {
        var body = new StackPanel { Spacing = 12 };
        foreach (var control in controls)
            body.Children.Add(control);
        return body;
    }

    private static Grid BuildFieldGrid(params (string Label, Control Editor)[] fields)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("250,*"),
            RowSpacing = 10,
            ColumnSpacing = 18
        };
        for (var index = 0; index < fields.Length; index++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var index = 0; index < fields.Length; index++)
            AddField(grid, index, fields[index].Label, fields[index].Editor);
        return grid;
    }

    private static StackPanel BuildToggleGroup(params CheckBox[] toggles)
    {
        var group = new StackPanel { Spacing = 8 };
        foreach (var toggle in toggles)
            group.Children.Add(toggle);
        return group;
    }

    private static void ConfigureList(ListBox list)
    {
        list.Background = Brushes.Transparent;
        list.BorderThickness = new Thickness(0);
        list.Foreground = RatioGhostPalette.Ink;
        list.FontSize = 12;
        list.Padding = new Thickness(0);
    }

    private static void ConfigureTextBox(TextBox input, string watermark)
    {
        input.Background = RatioGhostPalette.Surface;
        input.BorderBrush = RatioGhostPalette.Border;
        input.BorderThickness = new Thickness(1);
        input.CornerRadius = new CornerRadius(10);
        input.Foreground = RatioGhostPalette.Ink;
        input.FontSize = 13;
        input.MinHeight = 38;
        input.MinWidth = 180;
        input.Width = 220;
        input.Padding = new Thickness(11, 8);
        input.PlaceholderText = watermark;
        input.HorizontalAlignment = HorizontalAlignment.Left;
    }

    private static void ConfigureCheckBox(CheckBox checkBox)
    {
        checkBox.Foreground = RatioGhostPalette.Ink;
        checkBox.FontSize = 13;
        checkBox.HorizontalAlignment = HorizontalAlignment.Left;
        checkBox.Margin = new Thickness(0, 0, 0, 2);
    }

    private static Button CreateButton(string content, ButtonTone tone, double minWidth)
    {
        var button = new Button { Content = content };
        StyleButton(button, tone, minWidth);
        return button;
    }

    private static void StyleButton(Button button, ButtonTone tone, double minWidth)
    {
        button.Background = tone switch
        {
            ButtonTone.Primary => RatioGhostPalette.Accent,
            ButtonTone.Secondary => RatioGhostPalette.Surface,
            _ => Brushes.Transparent
        };
        button.BorderBrush = tone switch
        {
            ButtonTone.Primary => RatioGhostPalette.Accent,
            ButtonTone.Secondary => RatioGhostPalette.Border,
            _ => Brushes.Transparent
        };
        button.BorderThickness = new Thickness(1);
        button.CornerRadius = new CornerRadius(10);
        button.Foreground = tone == ButtonTone.Primary
            ? RatioGhostPalette.OnAccent
            : RatioGhostPalette.Ink;
        button.FontSize = 12;
        button.FontWeight = FontWeight.SemiBold;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.MinHeight = 38;
        button.MinWidth = minWidth;
        button.Padding = new Thickness(12, 8);
    }

    private static void AddField(Grid grid, int row, string label, Control editor)
    {
        grid.Children.Add(Place(new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = RatioGhostPalette.Ink,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        }, row));
        grid.Children.Add(Place(editor, row, 1));
    }

    private enum ButtonTone
    {
        Primary,
        Secondary,
        Quiet
    }

    private static class RatioGhostPalette
    {
        public static readonly SolidColorBrush Canvas = Brush("#F5F6FA");
        public static readonly SolidColorBrush Surface = Brush("#FFFFFF");
        public static readonly SolidColorBrush SurfaceSubtle = Brush("#FAFBFD");
        public static readonly SolidColorBrush Ink = Brush("#191A24");
        public static readonly SolidColorBrush Muted = Brush("#656875");
        public static readonly SolidColorBrush Border = Brush("#E1E3EA");
        public static readonly SolidColorBrush Accent = Brush("#3838A5");
        public static readonly SolidColorBrush AccentSoft = Brush("#ECECFF");
        public static readonly SolidColorBrush OnAccent = Brush("#FFFFFF");

        private static SolidColorBrush Brush(string hex) =>
            new(Color.Parse(hex));
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
