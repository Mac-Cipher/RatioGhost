using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using RatioGhost.Core.Platform;

namespace RatioGhost.Desktop.Platform;

[SupportedOSPlatform("linux")]
internal sealed class LinuxAutostartService : IAutostartService
{
    private const string ManagedMarker = "X-RatioGhost-Managed=true";
    private readonly string _desktopFilePath;
    private readonly Func<string> _launchCommand;

    public LinuxAutostartService()
        : this(ResolveDesktopFilePath(), ResolveLaunchCommand)
    {
    }

    internal LinuxAutostartService(
        string desktopFilePath,
        Func<string> launchCommand)
    {
        if (string.IsNullOrWhiteSpace(desktopFilePath))
            throw new ArgumentException("The desktop-file path is required.", nameof(desktopFilePath));
        ArgumentNullException.ThrowIfNull(launchCommand);

        _desktopFilePath = desktopFilePath;
        _launchCommand = launchCommand;
    }

    public PlatformCapability Capability { get; } = new(
        true,
        "Linux XDG autostart desktop entry (tested under Ubuntu 20.04 WSL; full desktop integration remains unverified).");

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_desktopFilePath))
            return false;

        var content = await File.ReadAllTextAsync(_desktopFilePath, cancellationToken);
        return HasManagedMarker(content);
    }

    public async Task SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!enabled)
        {
            if (!File.Exists(_desktopFilePath))
                return;

            var existing = await File.ReadAllTextAsync(_desktopFilePath, cancellationToken);
            if (HasManagedMarker(existing))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(_desktopFilePath);
            }
            return;
        }

        if (File.Exists(_desktopFilePath))
        {
            var existing = await File.ReadAllTextAsync(_desktopFilePath, cancellationToken);
            if (!HasManagedMarker(existing))
            {
                throw new InvalidOperationException(
                    $"Refusing to overwrite unmanaged autostart entry '{_desktopFilePath}'.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(_desktopFilePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("The desktop-file path must include a directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{_desktopFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var content = BuildDesktopEntry(_launchCommand());
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            // Re-check immediately before replacement so a newly-created unmanaged
            // entry is not overwritten during the normal enable path.
            if (File.Exists(_desktopFilePath))
            {
                var existing = await File.ReadAllTextAsync(_desktopFilePath, cancellationToken);
                if (!HasManagedMarker(existing))
                {
                    throw new InvalidOperationException(
                        $"Refusing to overwrite unmanaged autostart entry '{_desktopFilePath}'.");
                }
            }

            File.Move(temporaryPath, _desktopFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string ResolveDesktopFilePath()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
                home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrWhiteSpace(home))
                throw new InvalidOperationException("Cannot resolve the Linux user configuration directory.");
            configHome = Path.Combine(home, ".config");
        }

        return Path.Combine(configHome, "autostart", "RatioGhost.desktop");
    }

    private static string ResolveLaunchCommand()
    {
        var executable = Environment.ProcessPath ??
                         Process.GetCurrentProcess().MainModule?.FileName ??
                         throw new InvalidOperationException("Cannot resolve the executable path.");
        return $"{QuoteDesktopExecToken(executable)} --minimized";
    }

    private static string BuildDesktopEntry(string launchCommand)
    {
        if (string.IsNullOrWhiteSpace(launchCommand))
            throw new InvalidOperationException("Cannot create an autostart entry without a launch command.");
        if (launchCommand.Contains('\r') || launchCommand.Contains('\n'))
            throw new InvalidOperationException("The autostart launch command cannot contain newlines.");

        return "[Desktop Entry]\n" +
               "Type=Application\n" +
               "Version=1.0\n" +
               "Name=RatioGhost\n" +
               "Comment=RatioGhost proxy\n" +
               $"Exec={launchCommand}\n" +
               "Terminal=false\n" +
               "X-GNOME-Autostart-enabled=true\n" +
               $"{ManagedMarker}\n";
    }

    private static bool HasManagedMarker(string content) =>
        content.Split('\n').Any(line =>
            string.Equals(line.Trim(), ManagedMarker, StringComparison.Ordinal));

    private static string QuoteDesktopExecToken(string value)
    {
        if (value.Length > 0 && value.All(static character =>
                !char.IsWhiteSpace(character) && character is not '"' and not '\\' and not '%'))
            return value;

        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal)
                         .Replace("\"", "\\\"", StringComparison.Ordinal)
                         .Replace("%", "%%", StringComparison.Ordinal)}\"";
    }
}
