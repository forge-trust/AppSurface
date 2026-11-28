namespace ForgeTrust.AppSurface.Testing;

/// <summary>Stages the built CoverageLeaseProbe as a renamed executable for test fixtures.</summary>
public static class CoverageLeaseProbeStager
{
    /// <summary>
    /// Copies the current test configuration's CoverageLeaseProbe build output and creates a renamed apphost.
    /// </summary>
    /// <param name="destinationDirectory">
    /// The fixture directory that will contain the copied build output. It is created when needed.
    /// </param>
    /// <param name="executableName">
    /// The staged apphost name without an extension. Windows appends <c>.exe</c>.
    /// </param>
    /// <returns>The full path to the renamed apphost.</returns>
    /// <remarks>
    /// On Unix, the returned apphost is assigned owner read, write, and execute permissions.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An argument is blank, or the executable name is not a valid path segment.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// The CoverageLeaseProbe apphost has not been built for this configuration.
    /// </exception>
    public static string Stage(string destinationDirectory, string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var configuration = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var source = TestPathUtils.PathUnder(repository, "tests", "ForgeTrust.AppSurface.CoverageLeaseProbe", "bin",
            configuration, "net10.0");
        var appHostName = "ForgeTrust.AppSurface.CoverageLeaseProbe" + (OperatingSystem.IsWindows() ? ".exe" : "");
        var appHostPath = TestPathUtils.PathUnder(source, appHostName);
        if (!File.Exists(appHostPath))
            throw new FileNotFoundException(
                "The CoverageLeaseProbe apphost has not been built for this configuration.", appHostPath);

        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, TestPathUtils.PathUnder(destinationDirectory, Path.GetFileName(file)));

        var stagedExecutable = TestPathUtils.PathUnder(destinationDirectory,
            executableName + (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.Copy(TestPathUtils.PathUnder(destinationDirectory, appHostName), stagedExecutable);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(stagedExecutable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return stagedExecutable;
    }
}
