using RatioGhost.Desktop.Platform;

namespace RatioGhost.Desktop.Tests;

public sealed class WindowsAutostartServiceTests
{
    [Fact]
    public async Task EnableAndDisable_ManageOnlyInjectedRunValue()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var runKey = new IsolatedRunKey();
        var service = new WindowsAutostartService(
            runKey,
            () => "\"C:\\Program Files\\RatioGhost\\RatioGhost.exe\" --minimized");
        Assert.False(await service.IsEnabledAsync());

        await service.SetEnabledAsync(true);

        Assert.True(await service.IsEnabledAsync());
        Assert.Equal(
            "\"C:\\Program Files\\RatioGhost\\RatioGhost.exe\" --minimized",
            runKey.Command);

        await service.SetEnabledAsync(false);

        Assert.False(await service.IsEnabledAsync());
        Assert.Equal(1, runKey.DeleteCalls);
    }

    [Fact]
    public async Task Disable_WhenValueIsAbsent_DoesNotCreateAnything()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var runKey = new IsolatedRunKey();
        var service = new WindowsAutostartService(runKey, () => throw new InvalidOperationException());

        await service.SetEnabledAsync(false);

        Assert.Null(runKey.Command);
        Assert.Equal(0, runKey.WriteCalls);
        Assert.Equal(1, runKey.DeleteCalls);
    }

    private sealed class IsolatedRunKey : IWindowsRunKey
    {
        public string? Command { get; private set; }
        public int WriteCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public string? Read() => Command;

        public void Write(string command)
        {
            WriteCalls++;
            Command = command;
        }

        public void Delete()
        {
            DeleteCalls++;
            Command = null;
        }
    }
}
