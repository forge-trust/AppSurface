namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

public sealed class PostgreSqlRoleRecipePackagingTests
{
    private const string PackageId = "ForgeTrust.AppSurface.Durable.PostgreSql";
    private const string PackageVersion = "0.2.0-preview.13";
    private const string RecipeFileName = "configure-postgresql-roles.sql";
    private const int MaximumRecipeBytes = 1024 * 1024;
    private const int MaximumCapturedCharactersPerStream = 64 * 1024;

    [Fact]
    public async Task DirectPackageConsumer_CopiesCanonicalRoleRecipeFromFreshPackageToAppContextBaseDirectory()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var temporaryRoot = OwnedTemporaryRoot.Create();
        try
        {
            AssertOutsideRepository(repositoryRoot, temporaryRoot.Path);

            var providerProject = TestPathUtils.PathUnder(
                repositoryRoot,
                "Durable",
                "ForgeTrust.AppSurface.Durable.PostgreSql",
                "ForgeTrust.AppSurface.Durable.PostgreSql.csproj");
            var canonicalRecipePath = TestPathUtils.PathUnder(repositoryRoot, "Durable", RecipeFileName);
            var canonicalRecipe = await ReadBoundedFileAsync(canonicalRecipePath);
            var packageFeed = CreateDirectory(temporaryRoot.Path, "feed");

            await RunSuccessfulDotNetCommandAsync(
                temporaryRoot,
                "provider pack",
                repositoryRoot,
                null,
                null,
                TimeSpan.FromSeconds(300),
                "pack",
                providerProject,
                "--no-build",
                "--no-restore",
                "--verbosity",
                "minimal",
                "-m:1",
                "-nr:false",
                "--configuration",
                GetBuildConfiguration(),
                "--output",
                packageFeed,
                $"-p:PackageVersion={PackageVersion}");

            var packagePath = Assert.Single(Directory.EnumerateFiles(packageFeed, "*.nupkg", SearchOption.TopDirectoryOnly));
            var archiveRecipe = await ReadRecipeFromPackageAsync(packagePath);
            Assert.Equal(canonicalRecipe, archiveRecipe.Bytes);
            AssertArchiveContentMetadata(archiveRecipe.NuspecBytes);

            var consumerRoot = CreateDirectory(temporaryRoot.Path, "clean-consumer");
            var isolatedPackages = CreateDirectory(temporaryRoot.Path, "consumer-packages");
            var consumerCliHome = CreateDirectory(temporaryRoot.Path, "consumer-dotnet-home");
            var projectPath = TestPathUtils.PathUnder(consumerRoot, "PackageOnlyConsumer.csproj");
            var programPath = TestPathUtils.PathUnder(consumerRoot, "Program.cs");
            var nuGetConfigPath = TestPathUtils.PathUnder(consumerRoot, "NuGet.Config");

            await File.WriteAllTextAsync(projectPath, CreateConsumerProject());
            await File.WriteAllTextAsync(programPath, CreateConsumerProgram());
            await WriteNuGetConfigAsync(nuGetConfigPath, packageFeed, isolatedPackages);

            var consumerProject = XDocument.Load(projectPath);
            Assert.DoesNotContain(consumerProject.Descendants(), element => element.Name.LocalName == "ProjectReference");
            var packageReferences = consumerProject.Descendants().Where(element => element.Name.LocalName == "PackageReference");
            var packageReference = Assert.Single(packageReferences);
            Assert.Equal(PackageId, (string?)packageReference.Attribute("Include"));
            Assert.Equal(PackageVersion, (string?)packageReference.Attribute("Version"));

            await RunSuccessfulDotNetCommandAsync(
                temporaryRoot,
                "clean consumer restore",
                consumerRoot,
                consumerCliHome,
                isolatedPackages,
                TimeSpan.FromSeconds(300),
                "restore",
                projectPath,
                "--configfile",
                nuGetConfigPath,
                "--verbosity",
                "minimal",
                "-m:1",
                "-nr:false");

            var assetsPath = TestPathUtils.PathUnder(consumerRoot, "obj", "project.assets.json");
            AssertRestoredFromFreshPackage(assetsPath, isolatedPackages, archiveRecipe.Bytes);

            await RunSuccessfulDotNetCommandAsync(
                temporaryRoot,
                "clean consumer build",
                consumerRoot,
                consumerCliHome,
                isolatedPackages,
                TimeSpan.FromSeconds(300),
                "build",
                projectPath,
                "--configuration",
                "Release",
                "--no-restore",
                "--verbosity",
                "minimal",
                "-m:1",
                "-nr:false",
                "-p:UseSharedCompilation=false");

            var outputDirectory = TestPathUtils.PathUnder(consumerRoot, "bin", "Release", "net10.0");
            var outputRecipePath = TestPathUtils.PathUnder(outputDirectory, RecipeFileName);
            Assert.Equal(archiveRecipe.Bytes, await ReadBoundedFileAsync(outputRecipePath));
            Assert.Equal(
                outputRecipePath,
                Assert.Single(Directory.EnumerateFiles(outputDirectory, RecipeFileName, SearchOption.AllDirectories)));

            var execution = await RunSuccessfulDotNetCommandAsync(
                temporaryRoot,
                "clean consumer AppContext verification",
                consumerRoot,
                consumerCliHome,
                isolatedPackages,
                TimeSpan.FromSeconds(60),
                "run",
                "--project",
                projectPath,
                "--configuration",
                "Release",
                "--no-build",
                "--no-restore");
            var expectedHash = Convert.ToHexString(SHA256.HashData(archiveRecipe.Bytes));
            Assert.Contains(expectedHash, execution.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            temporaryRoot.Dispose();
        }
    }

    private static string GetBuildConfiguration()
        => new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";

    private static string CreateDirectory(string parent, string name)
    {
        var path = TestPathUtils.PathUnder(parent, name);
        Directory.CreateDirectory(path);
        Assert.False((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0, "An owned proof directory cannot be a reparse point.");
        return path;
    }

    private static void AssertOutsideRepository(string repositoryRoot, string candidateRoot)
    {
        var relativePath = Path.GetRelativePath(Path.GetFullPath(repositoryRoot), Path.GetFullPath(candidateRoot));
        Assert.True(
            relativePath == "."
            || relativePath == ".."
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal),
            "The generated package consumer must remain outside the repository checkout.");
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path)
    {
        var info = new FileInfo(path);
        Assert.True(info.Exists, $"Expected file was not found: {path}");
        Assert.InRange(info.Length, 0, MaximumRecipeBytes);
        return await File.ReadAllBytesAsync(path);
    }

    private static async Task<(byte[] Bytes, byte[] NuspecBytes)> ReadRecipeFromPackageAsync(string packagePath)
    {
        using var package = ZipFile.OpenRead(packagePath);
        var entry = Assert.Single(package.Entries, candidate =>
            string.Equals(
                candidate.FullName,
                $"contentFiles/any/any/{RecipeFileName}",
                StringComparison.Ordinal));
        Assert.InRange(entry.Length, 0, MaximumRecipeBytes);
        var recipeBytes = await ReadBoundedArchiveEntryAsync(entry);

        var nuspecEntry = Assert.Single(package.Entries, candidate =>
            candidate.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(nuspecEntry.Length, 0, MaximumRecipeBytes);
        var nuspecBytes = await ReadBoundedArchiveEntryAsync(nuspecEntry);
        return (recipeBytes, nuspecBytes);
    }

    private static async Task<byte[]> ReadBoundedArchiveEntryAsync(ZipArchiveEntry entry)
    {
        await using var input = entry.Open();
        using var output = new MemoryStream(checked((int)entry.Length));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            Assert.True(output.Length + read <= MaximumRecipeBytes, "The package entry exceeded the one MiB proof limit.");
            await output.WriteAsync(buffer.AsMemory(0, read));
        }

        Assert.Equal(entry.Length, output.Length);
        return output.ToArray();
    }

    private static void AssertArchiveContentMetadata(byte[] nuspecBytes)
    {
        using var nuspecStream = new MemoryStream(nuspecBytes, writable: false);
        var nuspec = XDocument.Load(nuspecStream);
        var file = Assert.Single(nuspec.Descendants(), element =>
            element.Name.LocalName == "files"
            && string.Equals(
                (string?)element.Attribute("include"),
                $"any/any/{RecipeFileName}",
                StringComparison.Ordinal));

        Assert.Equal("None", (string?)file.Attribute("buildAction"));
        Assert.Equal("true", (string?)file.Attribute("copyToOutput"));
        Assert.Equal("true", (string?)file.Attribute("flatten"));
    }

    private static string CreateConsumerProject()
        => $"""
           <Project Sdk="Microsoft.NET.Sdk">
             <PropertyGroup>
               <OutputType>Exe</OutputType>
               <TargetFramework>net10.0</TargetFramework>
               <ImplicitUsings>enable</ImplicitUsings>
               <Nullable>enable</Nullable>
             </PropertyGroup>
             <ItemGroup>
               <PackageReference Include="{PackageId}" Version="{PackageVersion}" />
             </ItemGroup>
           </Project>
           """;

    private static string CreateConsumerProgram()
        => """
           using System.Security.Cryptography;

           var recipePath = Path.Combine(AppContext.BaseDirectory, "configure-postgresql-roles.sql");
           if (!File.Exists(recipePath))
           {
               return 41;
           }

           Console.WriteLine(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(recipePath))));
           return 0;
           """;

    private static Task WriteNuGetConfigAsync(string path, string packageFeed, string packagesDirectory)
    {
        var config = new XDocument(
            new XElement(
                "configuration",
                new XElement(
                    "packageSources",
                    new XElement("clear"),
                    new XElement("add", new XAttribute("key", "fresh-provider-artifact"), new XAttribute("value", packageFeed)),
                    new XElement("add", new XAttribute("key", "nuget.org"), new XAttribute("value", "https://api.nuget.org/v3/index.json"))),
                new XElement(
                    "packageSourceMapping",
                    new XElement(
                        "packageSource",
                        new XAttribute("key", "fresh-provider-artifact"),
                        new XElement("package", new XAttribute("pattern", PackageId))),
                    new XElement(
                        "packageSource",
                        new XAttribute("key", "nuget.org"),
                        new XElement("package", new XAttribute("pattern", "*")))),
                new XElement(
                    "config",
                    new XElement("add", new XAttribute("key", "globalPackagesFolder"), new XAttribute("value", packagesDirectory)))));

        return File.WriteAllTextAsync(path, config.ToString(SaveOptions.DisableFormatting));
    }

    private static void AssertRestoredFromFreshPackage(string assetsPath, string packagesDirectory, byte[] expectedRecipe)
    {
        using var assets = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        var libraries = assets.RootElement.GetProperty("libraries");
        var packageLibrary = Assert.Single(libraries.EnumerateObject(), property =>
            string.Equals(property.Name, $"{PackageId}/{PackageVersion}", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("package", packageLibrary.Value.GetProperty("type").GetString());

        var relativePackagePath = packageLibrary.Value.GetProperty("path").GetString();
        Assert.False(string.IsNullOrWhiteSpace(relativePackagePath));
        var packageDirectory = TestPathUtils.PathUnder(packagesDirectory, relativePackagePath!.Split('/'));
        var restoredRecipePath = TestPathUtils.PathUnder(packageDirectory, "contentFiles", "any", "any", RecipeFileName);
        Assert.True(File.Exists(restoredRecipePath), "The fresh provider package must be expanded into the consumer's private cache.");
        Assert.Equal(expectedRecipe, File.ReadAllBytes(restoredRecipePath));

        var packageFolders = assets.RootElement.GetProperty("packageFolders");
        Assert.Single(packageFolders.EnumerateObject(), folder =>
            string.Equals(System.IO.Path.TrimEndingDirectorySeparator(folder.Name), System.IO.Path.TrimEndingDirectorySeparator(packagesDirectory), GetPathComparison()));
        Assert.DoesNotContain(
            libraries.EnumerateObject(),
            library => !string.Equals(library.Value.GetProperty("type").GetString(), "package", StringComparison.Ordinal));
    }

    private static StringComparison GetPathComparison()
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static async Task<CommandResult> RunSuccessfulDotNetCommandAsync(
        OwnedTemporaryRoot temporaryRoot,
        string operation,
        string workingDirectory,
        string? cliHome,
        string? packagesDirectory,
        TimeSpan timeout,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (cliHome is not null)
        {
            startInfo.Environment["DOTNET_CLI_HOME"] = cliHome;
        }
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        if (packagesDirectory is not null)
        {
            startInfo.Environment["NUGET_PACKAGES"] = packagesDirectory;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start dotnet for {operation}.");
        var standardOutputTask = ReadBoundedOutputAsync(process.StandardOutput);
        var standardErrorTask = ReadBoundedOutputAsync(process.StandardError);

        using var timeoutSource = new CancellationTokenSource(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            timedOut = true;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process may exit between the timeout check and process-tree termination.
            }

            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                temporaryRoot.PreserveForSafety();
                throw new TimeoutException($"dotnet {operation} timed out and its process tree did not terminate; owned proof files were preserved at {temporaryRoot.Path}.");
            }
        }

        var output = await standardOutputTask.WaitAsync(TimeSpan.FromSeconds(10));
        var error = await standardErrorTask.WaitAsync(TimeSpan.FromSeconds(10));
        if (timedOut)
        {
            throw new TimeoutException($"dotnet {operation} exceeded {timeout}. stdout: {output} stderr: {error}");
        }

        var result = new CommandResult(process.ExitCode, output, error);
        Assert.True(result.ExitCode == 0, $"dotnet {operation} failed with exit code {result.ExitCode}. stdout: {result.StandardOutput} stderr: {result.StandardError}");
        return result;
    }

    private static async Task<string> ReadBoundedOutputAsync(StreamReader reader)
    {
        var output = new StringBuilder(MaximumCapturedCharactersPerStream);
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory());
            if (read == 0)
            {
                break;
            }

            var retained = Math.Min(read, MaximumCapturedCharactersPerStream - output.Length);
            if (retained > 0)
            {
                output.Append(buffer, 0, retained);
            }

            truncated |= retained < read;
        }

        if (truncated)
        {
            output.AppendLine();
            output.Append("[output truncated after the bounded capture limit]");
        }

        return output.ToString();
    }

    private readonly record struct CommandResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class OwnedTemporaryRoot : IDisposable
    {
        private const string Prefix = "appsurface-postgresql-role-package-proof-";
        private readonly string _expectedParent;
        private bool _preserveForSafety;

        private OwnedTemporaryRoot(string path, string expectedParent)
        {
            Path = path;
            _expectedParent = expectedParent;
        }

        internal string Path { get; }

        internal static OwnedTemporaryRoot Create()
        {
            var temporaryDirectory = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            var name = $"{Prefix}{Guid.NewGuid():N}";
            var path = TestPathUtils.PathUnder(temporaryDirectory, name);
            if (Directory.Exists(path) || File.Exists(path))
            {
                throw new IOException("The unique package-proof temporary directory already exists.");
            }

            Directory.CreateDirectory(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The package-proof temporary root cannot be a reparse point.");
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return new OwnedTemporaryRoot(path, temporaryDirectory);
        }

        internal void PreserveForSafety() => _preserveForSafety = true;

        public void Dispose()
        {
            if (_preserveForSafety || !Directory.Exists(Path))
            {
                return;
            }

            var normalizedPath = System.IO.Path.GetFullPath(Path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var actualParent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetDirectoryName(normalizedPath)!);
            var expectedParent = System.IO.Path.TrimEndingDirectorySeparator(_expectedParent);
            if (!string.Equals(actualParent, expectedParent, comparison)
                || !System.IO.Path.GetFileName(normalizedPath).StartsWith(Prefix, comparison))
            {
                throw new IOException(
                    $"Refusing to remove a package-proof directory outside its owned temporary root. Actual parent '{actualParent}', expected parent '{expectedParent}'.");
            }

            if ((File.GetAttributes(normalizedPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Refusing to recursively remove a reparse-point package-proof root.");
            }

            Directory.Delete(normalizedPath, recursive: true);
        }
    }
}
