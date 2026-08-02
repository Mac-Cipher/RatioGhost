namespace RatioGhost.Desktop.Tests;

internal static class DisposableTestDirectory
{
    public static string Create(string containerName)
    {
        ValidateContainerName(containerName);
        var container = GetContainerPath(containerName);
        Directory.CreateDirectory(container);

        var root = Path.Combine(container, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static void Delete(string path, string containerName)
    {
        ValidateContainerName(containerName);
        var container = GetContainerPath(containerName);
        var root = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(container, root);
        if (relative == "." ||
            Path.IsPathRooted(relative) ||
            relative.StartsWith("..", StringComparison.Ordinal) ||
            relative.Contains(Path.DirectorySeparatorChar) ||
            relative.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException(
                $"Refusing to delete a test directory outside its disposable container: {root}");
        }

        // qBittorrent can release its profile lock a few seconds after the
        // parent process has exited. Retry only this generated directory so a
        // transient Windows handle does not turn a successful smoke into a
        // cleanup failure.
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
                break;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(500));
            }
            catch (UnauthorizedAccessException) when (attempt < 20)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(500));
            }
        }

        if (Directory.Exists(container) && !Directory.EnumerateFileSystemEntries(container).Any())
            Directory.Delete(container);
    }

    private static string GetContainerPath(string containerName) =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), containerName));

    private static void ValidateContainerName(string containerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        if (Path.GetFileName(containerName) != containerName)
            throw new ArgumentException("The disposable test container must be a single directory name.", nameof(containerName));
    }
}
