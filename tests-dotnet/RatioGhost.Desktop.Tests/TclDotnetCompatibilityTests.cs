using System.Diagnostics;
using System.Text;
using RatioGhost.Core.Announcements;
using RatioGhost.Core.Configuration;

namespace RatioGhost.Desktop.Tests;

public sealed class TclDotnetCompatibilityTests
{
    [Fact]
    public async Task TclOracleAndDotnet_RewriteSameDeterministicAnnounce()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var repository = FindRepositoryRoot();
        var tclRuntime = Path.Combine(repository, "tclkitsh.exe");
        if (!File.Exists(tclRuntime))
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var tcl = await RunTclOracleAsync(repository, tclRuntime, timeout.Token);
        var dotnet = RunDotnetTransformation();

        Assert.Equal(dotnet, tcl);
        Assert.Contains("downloaded=0", tcl, StringComparison.Ordinal);
        Assert.Contains("uploaded=456", tcl, StringComparison.Ordinal);
        Assert.Contains("left=0", tcl, StringComparison.Ordinal);
        Assert.DoesNotContain("event=", tcl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TclPauseHook_RequiresIsolatedTestMode()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var repository = FindRepositoryRoot();
        var tclRuntime = Path.Combine(repository, "tclkitsh.exe");
        if (!File.Exists(tclRuntime))
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var scriptDirectory = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.TclHookTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scriptDirectory);
        var script = Path.Combine(scriptDirectory, "pause-hook.tcl");
        var normalizedRepository = repository.Replace('\\', '/');
        await File.WriteAllTextAsync(
            script,
            $$"""
              set root {{"{"}}{{normalizedRepository}}{{"}"}}
              set auto_path [linsert $auto_path 0 [file join $root rghost.vfs lib]]
              set ::WINDOWS 1
              set ::LINUX 0
              set ::MAC 0
              proc Event {args} {}
              source [file join $root rghost.vfs lib app-ghost util.tcl]
              source [file join $root rghost.vfs lib app-ghost proxy.tcl]
              puts $::paused
              """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            timeout.Token);

        var startInfo = new ProcessStartInfo
        {
            FileName = tclRuntime,
            WorkingDirectory = repository,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { script }
        };
        startInfo.Environment["RATIOGHOST_ISOLATED_PAUSED"] = "1";
        startInfo.Environment.Remove("RATIOGHOST_ISOLATED_TEST");
        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("Could not start the Tcl hook characterization runtime.");
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Tcl hook oracle failed with code {process.ExitCode}: {error}");

        Assert.Equal(
            "0",
            output.Split(
                    ["\r\n", "\n"],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Last());
    }

    private static async Task<string> RunTclOracleAsync(
        string repository,
        string runtime,
        CancellationToken cancellationToken)
    {
        var scriptDirectory = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.TclOracle",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scriptDirectory);
        var script = Path.Combine(scriptDirectory, "compare.tcl");
        var normalizedRepository = repository.Replace('\\', '/');
        await File.WriteAllTextAsync(
            script,
            $$"""
              set root {{"{"}}{{normalizedRepository}}{{"}"}}
              set auto_path [linsert $auto_path 0 [file join $root rghost.vfs lib]]
              set ::WINDOWS 1
              set ::LINUX 0
              set ::MAC 0
              proc Event {args} {}
              source [file join $root rghost.vfs lib app-ghost util.tcl]
              source [file join $root rghost.vfs lib app-ghost proxy.tcl]
              set ::settings(no_download) 1
              set ::settings(seed) 1
              set ::actual_first(compatibility) {123 456 789}
              set resource {/announce?info_hash=compatibility&downloaded=123&uploaded=456&left=789&event=completed}
              lassign [apply_download_reporting_options $resource compatibility completed 123 789] resource downloaded left
              set resource [rewrite_query_params $resource [dict create downloaded $downloaded uploaded 456 left $left] {}]
              puts $resource
              """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = runtime,
            WorkingDirectory = repository,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { script }
        }) ?? throw new InvalidOperationException("Could not start the Tcl characterization runtime.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Tcl oracle failed with code {process.ExitCode}: {error}");
        return output.Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Last();
    }

    private static string RunDotnetTransformation()
    {
        var transformer = new AnnounceTransformer(new FixedRandomSource());
        var result = transformer.Transform(
            new Uri(
                "http://tracker.test/announce?info_hash=compatibility&downloaded=123&uploaded=456&left=789&event=completed"),
            new RatioGhostSettings
            {
                OnlyTrackerTraffic = true,
                ReportDownloadAsZero = true,
                PretendToSeed = true,
                BoostChancePercent = 0
            });
        Assert.Equal(AnnounceDisposition.Rewritten, result.Disposition);
        return result.Target!.PathAndQuery;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tclkitsh.exe")) &&
                File.Exists(Path.Combine(directory.FullName, "rghost.vfs", "main.tcl")) &&
                File.Exists(Path.Combine(directory.FullName, "tests", "all.tcl")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the RatioGhost repository root.");
    }

    private sealed class FixedRandomSource : IRandomSource
    {
        public double NextDouble() => 0;
    }
}
