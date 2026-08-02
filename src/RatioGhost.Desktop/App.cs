using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using RatioGhost.Core.Configuration;
using RatioGhost.Core.Platform;
using RatioGhost.Desktop.Platform;
using RatioGhost.Proxy;

namespace RatioGhost.Desktop;

public sealed class App : Application
{
    private TrayIcon? _trayIcon;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var profileDirectory = ProfileDirectory.GetDefault();
            var store = new JsonSettingsStore(profileDirectory);
            var autostart = PlatformServices.CreateAutostart();
            var certificates = PlatformServices.CreateCertificateAuthority(profileDirectory);
            var debugLogger = new FileProxyDebugLogger(
                Path.Combine(profileDirectory, "proxy_debug.log"));
            var window = new MainWindow(
                store,
                autostart,
                certificates,
                () => desktop.Shutdown(),
                debugLogger);
            desktop.MainWindow = window;
            // The Windows tray path is validated end-to-end. Keep non-Windows
            // backends window-only until their native tray integration is tested.
            if (ShouldCreateTrayIcon(OperatingSystem.IsWindows()))
                _trayIcon = CreateTrayIcon(window, desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static TrayIcon CreateTrayIcon(
        MainWindow window,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://RatioGhost/Assets/logo.png"));
        var tray = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "RatioGhost",
            Menu = BuildTrayMenu(window),
            IsVisible = true
        };
        tray.Clicked += (_, _) => window.ShowFromTray();
        return tray;
    }

    internal static NativeMenu BuildTrayMenu(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var show = new NativeMenuItem { Header = "Show RatioGhost" };
        show.Click += (_, _) => window.ShowFromTray();
        var pause = new NativeMenuItem { Header = "Pause / resume rewriting" };
        pause.Click += (_, _) => window.TogglePause();
        var exit = new NativeMenuItem { Header = "Exit" };
        exit.Click += async (_, _) => await window.PrepareForExitAsync();
        return new NativeMenu
        {
            Items = { show, pause, new NativeMenuItemSeparator(), exit }
        };
    }

    internal static bool ShouldCreateTrayIcon(bool isWindows) => isWindows;
}
