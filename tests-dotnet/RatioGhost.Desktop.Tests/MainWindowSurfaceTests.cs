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

        Assert.Equal("RatioGhost", window.Title);
        Assert.Equal("RatioGhost", MainWindow.ResolveWindowTitle(isWindows: true));
        Assert.Equal("RatioGhost", MainWindow.ResolveWindowTitle(isWindows: false));
        Assert.True(App.ShouldCreateTrayIcon(isWindows: true));
        Assert.False(App.ShouldCreateTrayIcon(isWindows: false));
        Assert.Equal(1100, window.Width);
        Assert.Equal(760, window.Height);
        Assert.NotNull(window.Icon);

        var root = Assert.IsType<Grid>(window.Content);
        Assert.Equal(2, root.Children.Count);
        var header = Assert.IsType<Grid>(root.Children[0]);
        Assert.Contains(
            header.Children.OfType<StackPanel>(),
            panel => panel.Children.OfType<Button>().Any(button => Equals(button.Content, "Save and apply")));

        var tabSurface = Assert.IsType<Border>(root.Children[1]);
        Assert.Equal(20, tabSurface.CornerRadius.TopLeft);
        Assert.Equal(2, tabSurface.BoxShadow.Count);
        var tabs = Assert.IsType<TabControl>(tabSurface.Child);
        var tabItems = tabs.Items.Cast<TabItem>().ToArray();
        Assert.Equal(
            ["Activity", "Torrents", "Options", "Platform"],
            tabItems.Select(item => item.Header).ToArray());
        Assert.All(tabItems, item => Assert.True(item.MinHeight >= 40));

        var buttons = Descendants(root).OfType<Button>().ToArray();
        Assert.NotEmpty(buttons);
        Assert.All(buttons, button => Assert.True(button.MinHeight >= 40));
        Assert.All(
            Descendants(root).OfType<TextBox>(),
            input => Assert.True(input.MinHeight >= 40));
        Assert.All(
            Descendants(root).OfType<CheckBox>(),
            checkBox => Assert.True(checkBox.MinHeight >= 40));

        var options = Assert.IsType<ScrollViewer>(tabItems[2].Content);
        Assert.True(ContainsText(options, "Write redacted proxy debug log"));

        var platform = Assert.IsType<ScrollViewer>(tabItems[3].Content);
        Assert.True(ContainsText(platform, "Start automatically with the user session"));
        var platformButtons = Descendants(platform)
            .OfType<Button>()
            .Select(button => button.Content)
            .ToArray();
        Assert.Contains("Enable HTTPS interception", platformButtons);
        Assert.Contains("Remove CA trust", platformButtons);

        var trayMenu = App.BuildTrayMenu(window);
        var trayItems = trayMenu.Items.ToArray();
        Assert.Equal(4, trayItems.Length);
        Assert.Equal("Show RatioGhost", Assert.IsType<NativeMenuItem>(trayItems[0]).Header);
        Assert.Equal("Pause / resume rewriting", Assert.IsType<NativeMenuItem>(trayItems[1]).Header);
        Assert.IsType<NativeMenuItemSeparator>(trayItems[2]);
        Assert.Equal("Exit", Assert.IsType<NativeMenuItem>(trayItems[3]).Header);
    }

    private static bool ContainsText(Control root, string text) =>
        Descendants(root).OfType<TextBlock>().Any(block => Equals(block.Text, text)) ||
        Descendants(root).OfType<CheckBox>().Any(checkBox => Equals(checkBox.Content, text));

    private static IEnumerable<Control> Descendants(Control root)
    {
        yield return root;

        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                foreach (var descendant in Descendants(child))
                    yield return descendant;
            }
        }

        if (root is ContentControl contentControl && contentControl.Content is Control content)
        {
            foreach (var descendant in Descendants(content))
                yield return descendant;
        }

        if (root is Decorator decorator && decorator.Child is Control decoratedChild)
        {
            foreach (var descendant in Descendants(decoratedChild))
                yield return descendant;
        }

        if (root is ItemsControl itemsControl)
        {
            foreach (var item in itemsControl.Items.OfType<Control>())
            {
                foreach (var descendant in Descendants(item))
                    yield return descendant;
            }
        }
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
