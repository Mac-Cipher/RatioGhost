using RatioGhost.Core.Configuration;

namespace RatioGhost.Core.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RatioGhost.Tests", Guid.NewGuid().ToString("N"));
        var store = new JsonSettingsStore(directory);
        var expected = new RatioGhostSettings { ListenPort = 48123, PretendToSeed = true };
        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();

        Assert.Equal(expected, actual);
        Assert.True(File.Exists(store.SettingsPath));
    }

    [Fact]
    public async Task Load_WhenPrimaryAndBackupAreInvalid_FallsBackToDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RatioGhost.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{not-json");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "settings.json.bak"),
            """{"ListenPort":70000}""");
        var store = new JsonSettingsStore(directory);

        var actual = await store.LoadAsync();

        Assert.Equal(SettingsLoadSource.Defaults, store.LastLoadSource);
        Assert.Equal(3773, actual.ListenPort);
    }

    [Fact]
    public async Task CanceledSave_DoesNotLeaveTemporaryFileOrChangePrimary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RatioGhost.Tests", Guid.NewGuid().ToString("N"));
        var store = new JsonSettingsStore(directory);
        var original = new RatioGhostSettings { ListenPort = 48123 };
        await store.SaveAsync(original);
        var before = await File.ReadAllTextAsync(store.SettingsPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync(original with { ListenPort = 48124 }, cancellation.Token));

        Assert.Equal(before, await File.ReadAllTextAsync(store.SettingsPath));
        Assert.False(File.Exists(store.SettingsPath + ".tmp"));
    }

    [Fact]
    public void ProfileDirectory_UsesAbsoluteExplicitOverride()
    {
        var previous = Environment.GetEnvironmentVariable("RATIOGHOST_PROFILE_DIR");
        var relative = Path.Combine(".", "RatioGhost.ProfileOverride", Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("RATIOGHOST_PROFILE_DIR", relative);

            Assert.Equal(Path.GetFullPath(relative), ProfileDirectory.GetDefault());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RATIOGHOST_PROFILE_DIR", previous);
        }
    }
}
