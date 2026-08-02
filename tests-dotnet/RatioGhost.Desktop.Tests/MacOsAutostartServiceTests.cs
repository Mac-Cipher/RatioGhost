using System.Runtime.Versioning;
using RatioGhost.Desktop.Platform;

namespace RatioGhost.Desktop.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacOsAutostartServiceTests
{
    [Fact]
    public async Task EnableAndDisable_ManageOnlyTheInjectedLaunchAgent()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "LaunchAgents", "com.ratioghost.desktop.plist");
        var service = new MacOsAutostartService(
            path,
            () => new LaunchAgentCommand(
                "/Applications/Ratio Ghost.app/Contents/MacOS/RatioGhost",
                ["--minimized"]));
        try
        {
            Assert.True(service.Capability.IsSupported);
            Assert.False(await service.IsEnabledAsync());

            await service.SetEnabledAsync(true);

            Assert.True(await service.IsEnabledAsync());
            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("<key>Label</key>", content);
            Assert.Contains("com.ratioghost.desktop", content);
            Assert.Contains("X-RatioGhost-Managed", content);
            Assert.Contains("--minimized", content);

            await service.SetEnabledAsync(false);

            Assert.False(File.Exists(path));
            Assert.False(await service.IsEnabledAsync());
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task UnmanagedCollision_IsNeverOverwrittenOrRemoved()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "LaunchAgents", "com.ratioghost.desktop.plist");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string unmanaged = "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>Label</key><string>other.agent</string></dict></plist>";
        await File.WriteAllTextAsync(path, unmanaged);
        var service = new MacOsAutostartService(
            path,
            () => new LaunchAgentCommand("/opt/RatioGhost", ["--minimized"]));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SetEnabledAsync(true));
            Assert.Equal(unmanaged, await File.ReadAllTextAsync(path));
            Assert.False(await service.IsEnabledAsync());

            await service.SetEnabledAsync(false);

            Assert.True(File.Exists(path));
            Assert.Equal(unmanaged, await File.ReadAllTextAsync(path));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task UnmanagedCollisionCreatedDuringEnable_IsNeverOverwritten()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "LaunchAgents", "com.ratioghost.desktop.plist");
        const string unmanaged = "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>Label</key><string>other.agent</string></dict></plist>";
        var service = new MacOsAutostartService(
            path,
            () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, unmanaged);
                return new LaunchAgentCommand("/opt/RatioGhost", ["--minimized"]);
            });
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SetEnabledAsync(true));
            Assert.Equal(unmanaged, await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task DtdDocument_IsNeverTreatedAsManaged()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "LaunchAgents", "com.ratioghost.desktop.plist");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string dtd = "<?xml version=\"1.0\"?><!DOCTYPE plist SYSTEM \"file:///missing-ratioghost-secret\"><plist version=\"1.0\"><dict><key>X-RatioGhost-Managed</key><true/></dict></plist>";
        await File.WriteAllTextAsync(path, dtd);
        var service = new MacOsAutostartService(
            path,
            () => new LaunchAgentCommand("/opt/RatioGhost", ["--minimized"]));
        try
        {
            Assert.False(await service.IsEnabledAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SetEnabledAsync(true));
            Assert.Equal(dtd, await File.ReadAllTextAsync(path));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task CanceledEnable_DoesNotLeaveAnEntry()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "LaunchAgents", "com.ratioghost.desktop.plist");
        var service = new MacOsAutostartService(
            path,
            () => new LaunchAgentCommand("/opt/RatioGhost", ["--minimized"]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.SetEnabledAsync(true, cancellation.Token));
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task CanceledDisable_PreservesManagedEntry()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "LaunchAgents", "com.ratioghost.desktop.plist");
        var service = new MacOsAutostartService(
            path,
            () => new LaunchAgentCommand("/opt/RatioGhost", ["--minimized"]));
        using var cancellation = new CancellationTokenSource();
        try
        {
            await service.SetEnabledAsync(true);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.SetEnabledAsync(false, cancellation.Token));
            Assert.True(File.Exists(path));
            Assert.True(await service.IsEnabledAsync());
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.MacOsAutostartTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
