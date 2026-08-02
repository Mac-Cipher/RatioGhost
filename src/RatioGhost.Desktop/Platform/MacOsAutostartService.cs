using System.Diagnostics;
using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;
using RatioGhost.Core.Platform;

namespace RatioGhost.Desktop.Platform;

[SupportedOSPlatform("macos")]
internal sealed class MacOsAutostartService : IAutostartService
{
    private const string Label = "com.ratioghost.desktop";
    private const string ManagedKey = "X-RatioGhost-Managed";
    private readonly string _path;
    private readonly Func<LaunchAgentCommand> _command;

    public MacOsAutostartService()
        : this(ResolveDefaultPath(), ResolveLaunchCommand)
    {
    }

    internal MacOsAutostartService(
        string path,
        Func<LaunchAgentCommand> command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(command);
        _path = path;
        _command = command;
    }

    public PlatformCapability Capability { get; } =
        new(true, "macOS LaunchAgent (tested file integration; native session launch pending on macOS).");

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(_path) && IsManaged(_path));
    }

    public async Task SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!enabled)
        {
            if (File.Exists(_path) && IsManaged(_path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(_path);
            }
            return;
        }

        if (File.Exists(_path) && !IsManaged(_path))
            throw new InvalidOperationException(
                $"Refusing to overwrite an unmanaged LaunchAgent at '{_path}'.");

        var directory = Path.GetDirectoryName(_path) ??
                        throw new InvalidOperationException("Cannot resolve the LaunchAgent directory.");
        Directory.CreateDirectory(directory);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var document = BuildDocument(_command());
            await File.WriteAllTextAsync(
                temporary,
                document.ToString(SaveOptions.DisableFormatting),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Re-check immediately before replacement so an unmanaged plist
            // created after the initial collision check is never overwritten.
            if (File.Exists(_path) && !IsManaged(_path))
                throw new InvalidOperationException(
                    $"Refusing to overwrite an unmanaged LaunchAgent at '{_path}'.");

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static bool IsManaged(string path)
    {
        try
        {
            using var reader = XmlReader.Create(
                path,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            var dictionary = document.Root?.Element("dict");
            if (dictionary is null)
                return false;

            var elements = dictionary.Elements().ToArray();
            for (var index = 0; index + 1 < elements.Length; index++)
            {
                if (elements[index].Name.LocalName.Equals("key", StringComparison.Ordinal) &&
                    elements[index].Value.Equals(ManagedKey, StringComparison.Ordinal) &&
                    elements[index + 1].Name.LocalName.Equals("true", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidOperationException or XmlException)
        {
            return false;
        }
    }

    private static XDocument BuildDocument(LaunchAgentCommand command)
    {
        var arguments = new XElement(
            "array",
            new XElement("string", command.Executable),
            command.Arguments.Select(argument => new XElement("string", argument)));
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(
                "plist",
                new XAttribute("version", "1.0"),
                new XElement(
                    "dict",
                    new XElement("key", "Label"),
                    new XElement("string", Label),
                    new XElement("key", "ProgramArguments"),
                    arguments,
                    new XElement("key", "RunAtLoad"),
                    new XElement("true"),
                    new XElement("key", ManagedKey),
                    new XElement("true"))));
    }

    private static string ResolveDefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            throw new InvalidOperationException("Cannot resolve the macOS user home directory.");
        return Path.Combine(home, "Library", "LaunchAgents", Label + ".plist");
    }

    private static LaunchAgentCommand ResolveLaunchCommand()
    {
        var executable = Environment.ProcessPath ??
                         Process.GetCurrentProcess().MainModule?.FileName ??
                         throw new InvalidOperationException("Cannot resolve the executable path.");
        return new(executable, ["--minimized"]);
    }
}

internal sealed record LaunchAgentCommand(
    string Executable,
    IReadOnlyList<string> Arguments);
