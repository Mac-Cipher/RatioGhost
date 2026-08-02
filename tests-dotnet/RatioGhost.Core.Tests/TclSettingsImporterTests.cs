using RatioGhost.Core.Configuration;

namespace RatioGhost.Core.Tests;

public sealed class TclSettingsImporterTests
{
    private const string RealisticSettings =
        "reported_up 12 seed 1 geometry {700x470+78+78} reported_down 34 runtime 947795 " +
        "listen_port 3773 actual_up 56 boost 15 autostart 1 boost_chance 5 " +
        "upup_ratio_a 4.0 upup_ratio_b 8.0 actual_down 78 min_peers 5 start_minimized 0 " +
        "listen_port_https 3774 updown_ratio_a 0.00 only_local 1 updown_ratio_b 0.05 " +
        "no_download 1 only_tracker 1 proxy_debug_logging 0 sessions 17";

    [Fact]
    public void ParseAndMap_CharacterizesRealSettingsDatShape()
    {
        var values = TclSettingsImporter.ParseArrayList(RealisticSettings);
        var settings = TclSettingsImporter.Map(values, new RatioGhostSettings());

        Assert.True(settings.PretendToSeed);
        Assert.True(settings.ReportDownloadAsZero);
        Assert.True(settings.AutoStart);
        Assert.Equal(3773, settings.ListenPort);
        Assert.Equal(947795, settings.LifetimeRuntimeSeconds);
        Assert.Equal(78, settings.LifetimeActualDownloaded);
        Assert.Equal(56, settings.LifetimeActualUploaded);
        Assert.Equal(34, settings.LifetimeReportedDownloaded);
        Assert.Equal(12, settings.LifetimeReportedUploaded);
        Assert.Equal(17, settings.Sessions);
    }

    [Fact]
    public void Parse_TreatsCommandSyntaxAsLiteralData()
    {
        var values = TclSettingsImporter.ParseArrayList(
            "listen_port 3773 ignored {[exec dangerous-command]} note \"literal $value\"");

        Assert.Equal("[exec dangerous-command]", values["ignored"]);
        Assert.Equal("literal $value", values["note"]);
    }

    [Fact]
    public void Parse_RejectsOddAndUnterminatedLists()
    {
        Assert.Throws<FormatException>(() => TclSettingsImporter.ParseArrayList("listen_port"));
        Assert.Throws<FormatException>(() => TclSettingsImporter.ParseArrayList("listen_port {3773"));
        Assert.Throws<FormatException>(() => TclSettingsImporter.ParseArrayList("listen_port \"3773"));
    }

    [Fact]
    public async Task Store_ImportsOnceAndLeavesLegacyBytesUnchanged()
    {
        var directory = CreateTestDirectory();
        var legacyPath = Path.Combine(directory, "settings.dat");
        var original = RealisticSettings + Environment.NewLine;
        await File.WriteAllTextAsync(legacyPath, original);
        var originalWriteTime = File.GetLastWriteTimeUtc(legacyPath);
        var store = new JsonSettingsStore(directory);

        var imported = await store.LoadAsync();

        Assert.Equal(SettingsLoadSource.LegacyTcl, store.LastLoadSource);
        Assert.True(imported.ReportDownloadAsZero);
        Assert.True(File.Exists(store.SettingsPath));
        Assert.Equal(original, await File.ReadAllTextAsync(legacyPath));
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(legacyPath));

        await File.WriteAllTextAsync(legacyPath, "listen_port 49999");
        var reloaded = await new JsonSettingsStore(directory).LoadAsync();
        Assert.Equal(3773, reloaded.ListenPort);
    }

    [Fact]
    public async Task Store_UsesLegacyBackupWhenPrimaryIsInvalid()
    {
        var directory = CreateTestDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory, "settings.dat"), "listen_port");
        await File.WriteAllTextAsync(Path.Combine(directory, "settings.dat.bak"), "listen_port 48123 seed 1");
        var store = new JsonSettingsStore(directory);

        var imported = await store.LoadAsync();

        Assert.Equal(SettingsLoadSource.LegacyTclBackup, store.LastLoadSource);
        Assert.Equal(48123, imported.ListenPort);
        Assert.True(imported.PretendToSeed);
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.SettingsTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
