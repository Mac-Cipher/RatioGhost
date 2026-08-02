using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using RatioGhost.Core.Configuration;
using RatioGhost.Core.Platform;
using RatioGhost.Desktop;

namespace RatioGhost.Desktop.Tests;

public sealed class MainWindowSurfaceTests
{
    [Fact]
    public void Constructor_BuildsEssentialDesktopSurface()
    {
        Assert.True(MainWindow.ShouldStartMinimized(
            trayAvailable: true,
            startMinimizedSetting: true,
            minimizedCommandLine: false));
        Assert.False(MainWindow.ShouldStartMinimized(
            trayAvailable: false,
            startMinimizedSetting: true,
            minimizedCommandLine: true));
        Assert.True(MainWindow.ShouldHideOnWindowClose(trayAvailable: true));
        Assert.False(MainWindow.ShouldHideOnWindowClose(trayAvailable: false));

        if (!OperatingSystem.IsWindows())
            return;

        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .SetupWithoutStarting();

        var window = new MainWindow(
            new InMemorySettingsStore(),
            new TestAutostartService(),
            new TestCertificateAuthorityService(),
            shutdown: static () => { });

        Assert.Equal("RatioGhost .NET — Windows milestone", window.Title);
        Assert.Equal("RatioGhost .NET — Windows milestone", MainWindow.ResolveWindowTitle(isWindows: true));
        Assert.Equal("RatioGhost .NET", MainWindow.ResolveWindowTitle(isWindows: false));
        Assert.True(App.ShouldCreateTrayIcon(isWindows: true));
        Assert.False(App.ShouldCreateTrayIcon(isWindows: false));
        Assert.Equal(880, window.Width);
        Assert.Equal(650, window.Height);

        var root = Assert.IsType<Grid>(window.Content);
        Assert.Equal(2, root.Children.Count);
        var toolbar = Assert.IsType<StackPanel>(root.Children[0]);
        Assert.Equal(5, toolbar.Children.Count);

        var tabs = Assert.IsType<TabControl>(root.Children[1]);
        var tabItems = tabs.Items.Cast<TabItem>().ToArray();
        Assert.Equal(
            ["Activity", "Torrents", "Options", "Platform"],
            tabItems.Select(item => item.Header).ToArray());

        var options = Assert.IsType<ScrollViewer>(tabItems[2].Content);
        var optionsGrid = Assert.IsType<Grid>(options.Content);
        Assert.Contains(optionsGrid.Children, child => child is TextBlock textBlock &&
            Equals(textBlock.Text, "Write redacted proxy debug log"));

        var platform = Assert.IsType<StackPanel>(tabItems[3].Content);
        Assert.Contains(platform.Children, child => child is CheckBox checkBox &&
            Equals(checkBox.Content, "Start automatically with the user session"));
        var certificateActions = Assert.Single(
            platform.Children.OfType<StackPanel>(),
            panel => panel.Children.OfType<Button>().Any());
        Assert.Contains(certificateActions.Children, child => child is Button button &&
            Equals(button.Content, "Enable HTTPS interception"));
        Assert.Contains(certificateActions.Children, child => child is Button button &&
            Equals(button.Content, "Remove CA trust"));

        var trayMenu = App.BuildTrayMenu(window);
        var trayItems = trayMenu.Items.ToArray();
        Assert.Equal(4, trayItems.Length);
        Assert.Equal("Show RatioGhost", Assert.IsType<NativeMenuItem>(trayItems[0]).Header);
        Assert.Equal("Pause / resume rewriting", Assert.IsType<NativeMenuItem>(trayItems[1]).Header);
        Assert.IsType<NativeMenuItemSeparator>(trayItems[2]);
        Assert.Equal("Exit", Assert.IsType<NativeMenuItem>(trayItems[3]).Header);
    }

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        public SettingsLoadSource LastLoadSource => SettingsLoadSource.Defaults;

        public Task<RatioGhostSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RatioGhostSettings());

        public Task SaveAsync(
            RatioGhostSettings settings,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestAutostartService : IAutostartService
    {
        public PlatformCapability Capability { get; } = new(true, "Test autostart");

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task SetEnabledAsync(
            bool enabled,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestCertificateAuthorityService : ICertificateAuthorityService
    {
        public PlatformCapability Capability { get; } = new(false, "Test certificate service");

        public Task<bool> IsTrustedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task RequestTrustAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveTrustAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<System.Security.Cryptography.X509Certificates.X509Certificate2> GetServerCertificateAsync(
            string host,
            CancellationToken cancellationToken = default) =>
            throw new PlatformNotSupportedException();
    }
}
