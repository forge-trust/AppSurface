using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using CliWrap;
using CliCommand = CliWrap.Cli;

namespace ForgeTrust.RazorWire.Cli.Tests;

[Collection(ProgramEntryPointCollection.Name)]
public sealed class ToolPackageContractTests
{
    private const string PackageId = "ForgeTrust.RazorWire.Cli";
    private const string ToolCommandName = "razorwire";
    private const string PackageDescription = "Command-line export tooling for RazorWire applications.";

    [Fact]
    [Trait("Category", "PackageVerification")]
    public async Task PackagedTool_Should_Run_Through_Dnx_ToolInstall_And_InstalledExport()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var workspace = TempDirectory.Create("razorwire-tool-package-");
        var packageDirectory = workspace.CreateSubdirectory("packages");
        var cliHomeDirectory = workspace.CreateSubdirectory("dotnet-home");
        var toolDirectory = workspace.CreateSubdirectory("tool-path");
        var exportDirectory = workspace.CreateSubdirectory("export-output");
        var version = "0.0.0-toolverifier." + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var nugetPackagesDirectory = GetDefaultNuGetPackagesPath();
        using var packageCacheCleanup = new GeneratedPackageCacheCleanup(nugetPackagesDirectory, PackageId, version);
        var repositoryRunner = new ToolProcessRunner(
            repositoryRoot,
            new Dictionary<string, string?>
            {
                ["MSBUILDDISABLENODEREUSE"] = "1",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
            });
        var isolatedToolRunner = new ToolProcessRunner(
            repositoryRoot,
            new Dictionary<string, string?>
            {
                ["DOTNET_CLI_HOME"] = cliHomeDirectory.Path,
                // Keep package assets pointed at a stable cache while isolating .NET CLI first-run state.
                ["NUGET_PACKAGES"] = nugetPackagesDirectory,
                ["MSBUILDDISABLENODEREUSE"] = "1",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
            });

        var packResult = await repositoryRunner.RunDotNetAsync(
            TimeSpan.FromMinutes(3),
            "pack",
            Path.Combine("Web", "ForgeTrust.RazorWire.Cli", "ForgeTrust.RazorWire.Cli.csproj"),
            "--configuration",
            "Release",
            "--no-restore",
            "--output",
            packageDirectory.Path,
            "/p:EnableRazorWireCliToolPackaging=true",
            $"/p:PackageVersion={version}");

        packResult.AssertSucceeded("Expected the RazorWire CLI project to pack as a .NET tool package.");
        var packagePath = Path.Combine(packageDirectory.Path, $"{PackageId}.{version}.nupkg");
        Assert.True(File.Exists(packagePath), $"Expected package file to exist at {packagePath}.");

        AssertToolPackageContract(packagePath, version);

        var dnxResult = await isolatedToolRunner.RunDotNetAsync(
            TimeSpan.FromMinutes(1),
            "dnx",
            $"{PackageId}@{version}",
            "--yes",
            "--source",
            packageDirectory.Path,
            "--",
            "--help");

        dnxResult.AssertSucceeded("Expected dnx to run the exact local RazorWire CLI package.");
        Assert.Contains("usage", dnxResult.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("export", dnxResult.AllText, StringComparison.Ordinal);

        var installResult = await isolatedToolRunner.RunDotNetAsync(
            TimeSpan.FromMinutes(1),
            "tool",
            "install",
            PackageId,
            "--tool-path",
            toolDirectory.Path,
            "--source",
            packageDirectory.Path,
            "--version",
            version);

        installResult.AssertSucceeded("Expected dotnet tool install to install the exact local RazorWire CLI package.");
        var installedToolPath = Path.Combine(toolDirectory.Path, OperatingSystem.IsWindows() ? "razorwire.exe" : "razorwire");
        Assert.True(File.Exists(installedToolPath), $"Expected installed tool shim to exist at {installedToolPath}.");

        var rootHelpResult = await isolatedToolRunner.RunAsync(
            installedToolPath,
            TimeSpan.FromMinutes(1),
            "--help");

        rootHelpResult.AssertSucceeded("Expected the installed RazorWire tool to print root help.");
        Assert.Contains("usage", rootHelpResult.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("export", rootHelpResult.AllText, StringComparison.Ordinal);

        var exportHelpResult = await isolatedToolRunner.RunAsync(
            installedToolPath,
            TimeSpan.FromMinutes(1),
            "export",
            "--help");

        exportHelpResult.AssertSucceeded("Expected the installed RazorWire tool to print export help.");
        Assert.Contains("Export a RazorWire site to a static directory.", exportHelpResult.AllText, StringComparison.Ordinal);

        var sampleProjectPath = Path.Combine(
            repositoryRoot,
            "examples",
            "razorwire-mvc",
            "RazorWireWebExample.csproj");
        var exportResult = await isolatedToolRunner.RunAsync(
            installedToolPath,
            TimeSpan.FromMinutes(4),
            "export",
            "--project",
            sampleProjectPath,
            "--mode",
            "hybrid",
            "--output",
            exportDirectory.Path);

        exportResult.AssertSucceeded("Expected the installed RazorWire tool to export the MVC sample in hybrid mode.");
        var indexPath = Path.Combine(exportDirectory.Path, "index.html");
        Assert.True(File.Exists(indexPath), $"Expected the export to create {indexPath}.");
        var indexHtml = await File.ReadAllTextAsync(indexPath);
        Assert.Contains("RazorWire", indexHtml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ForgeTrust.AppSurface.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to find repository root from the current test assembly path.");
    }

    private static string GetDefaultNuGetPackagesPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".nuget", "packages");
    }

    private static void AssertToolPackageContract(string packagePath, string expectedVersion)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var nuspecEntry = Assert.Single(
            archive.Entries,
            entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using var nuspecStream = nuspecEntry.Open();
        var nuspec = XDocument.Load(nuspecStream);
        var ns = nuspec.Root!.Name.Namespace;

        Assert.Equal(PackageId, nuspec.Descendants(ns + "id").Single().Value);
        Assert.Equal(expectedVersion, nuspec.Descendants(ns + "version").Single().Value);
        var description = nuspec.Descendants(ns + "description").Single().Value;
        Assert.Equal(PackageDescription, description);
        Assert.DoesNotContain("Package Description", description, StringComparison.Ordinal);

        var packageType = Assert.Single(nuspec.Descendants(ns + "packageType"));
        Assert.Equal("DotnetTool", packageType.Attribute("name")?.Value);
        Assert.Empty(nuspec.Descendants(ns + "dependency"));

        var settingsEntry = Assert.Single(
            archive.Entries,
            entry =>
                entry.FullName.StartsWith("tools/", StringComparison.Ordinal)
                && entry.FullName.EndsWith("/any/DotnetToolSettings.xml", StringComparison.Ordinal));
        using var settingsStream = settingsEntry.Open();
        var settings = XDocument.Load(settingsStream);
        var command = Assert.Single(settings.Descendants("Command"));
        Assert.Equal(ToolCommandName, command.Attribute("Name")?.Value);
        Assert.Equal("ForgeTrust.RazorWire.Cli.dll", command.Attribute("EntryPoint")?.Value);
        Assert.Equal("dotnet", command.Attribute("Runner")?.Value);
    }

    private sealed class ToolProcessRunner(
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environmentOverrides)
    {
        public Task<ToolProcessResult> RunDotNetAsync(TimeSpan timeout, params string[] arguments) =>
            RunAsync("dotnet", timeout, arguments);

        public async Task<ToolProcessResult> RunAsync(
            string fileName,
            TimeSpan timeout,
            params string[] arguments)
        {
            using var timeoutSource = new CancellationTokenSource(timeout);
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var commandLine = BuildCommandLine(fileName, arguments);

            try
            {
                var result = await CliCommand.Wrap(fileName)
                    .WithArguments(arguments)
                    .WithWorkingDirectory(workingDirectory)
                    .WithEnvironmentVariables(environmentOverrides)
                    .WithValidation(CommandResultValidation.None)
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
                    .ExecuteAsync(timeoutSource.Token);

                return new ToolProcessResult(commandLine, result.ExitCode, TimedOut: false, stdout.ToString(), stderr.ToString());
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                return new ToolProcessResult(commandLine, ExitCode: null, TimedOut: true, stdout.ToString(), stderr.ToString());
            }
        }

        private static string BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
        {
            var builder = new StringBuilder(EscapeArgument(fileName));
            foreach (var argument in arguments)
            {
                builder.Append(' ');
                builder.Append(EscapeArgument(argument));
            }

            return builder.ToString();
        }

        private static string EscapeArgument(string argument) =>
            argument.Any(char.IsWhiteSpace) || argument.Contains('"', StringComparison.Ordinal)
                ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
                : argument;
    }

    private sealed record ToolProcessResult(
        string CommandLine,
        int? ExitCode,
        bool TimedOut,
        string Stdout,
        string Stderr)
    {
        public string AllText => Stdout + Environment.NewLine + Stderr;

        public void AssertSucceeded(string message)
        {
            Assert.False(
                TimedOut,
                $"{message}{Environment.NewLine}Process timed out: {CommandLine}{Environment.NewLine}STDOUT:{Environment.NewLine}{Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{Stderr}");
            Assert.True(
                ExitCode == 0,
                $"{message}{Environment.NewLine}Command: {CommandLine}{Environment.NewLine}Exit code: {ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "<timeout>"}{Environment.NewLine}STDOUT:{Environment.NewLine}{Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{Stderr}");
        }
    }

    private sealed class GeneratedPackageCacheCleanup(
        string nugetPackagesDirectory,
        string packageId,
        string packageVersion) : IDisposable
    {
        public void Dispose()
        {
            var packageIdSegment = GetSafePackagePathSegment(packageId, nameof(packageId));
            var packageVersionSegment = GetSafePackagePathSegment(packageVersion, nameof(packageVersion));
            var packageVersionDirectory = System.IO.Path.Combine(
                nugetPackagesDirectory,
                packageIdSegment,
                packageVersionSegment);

            try
            {
                if (Directory.Exists(packageVersionDirectory))
                {
                    Directory.Delete(packageVersionDirectory, recursive: true);
                }
            }
            catch (IOException ex)
            {
                Trace.TraceWarning(
                    "Could not delete generated NuGet package cache directory '{0}': {1}",
                    packageVersionDirectory,
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Trace.TraceWarning(
                    "Could not delete generated NuGet package cache directory '{0}': {1}",
                    packageVersionDirectory,
                    ex);
            }
        }

        private static string GetSafePackagePathSegment(string value, string parameterName)
        {
            if (System.IO.Path.IsPathRooted(value)
                || value.IndexOf(System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) >= 0
                || value.IndexOf(System.IO.Path.AltDirectorySeparatorChar, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException($"{parameterName} must be a NuGet package path segment.");
            }

            return value.ToLowerInvariant();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create(string prefix)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public TempDirectory CreateSubdirectory(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException ex)
            {
                Trace.TraceWarning("Could not delete temporary directory '{0}': {1}", Path, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Trace.TraceWarning("Could not delete temporary directory '{0}': {1}", Path, ex);
            }
        }
    }
}
