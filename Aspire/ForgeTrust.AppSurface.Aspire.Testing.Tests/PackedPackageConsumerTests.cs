using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Xunit.Abstractions;

public sealed class PackedPackageConsumerTests : IClassFixture<PackedPackageConsumerTests.PackedProducerFixture>
{
    private const string AspireOverrideVersion = "13.4.6";
    private const string AspireBaselineVersion = "13.4.4";
    private const string HostingBaselineVersion = "10.0.8";
    private const string AppSurfaceTestingPackageId = "ForgeTrust.AppSurface.Aspire.Testing";
    private const string SkoolitSourceCommit = "a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e";
    private static readonly string[] PackedPackageIds =
    [
        "ForgeTrust.AppSurface.Core",
        "ForgeTrust.AppSurface.Deployment",
        "ForgeTrust.AppSurface.Console",
        "ForgeTrust.AppSurface.Aspire",
        AppSurfaceTestingPackageId
    ];
    private readonly ITestOutputHelper _output;
    private readonly PackedProducerFixture _producer;

    public PackedPackageConsumerTests(ITestOutputHelper output, PackedProducerFixture producer)
    {
        _output = output;
        _producer = producer;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PackedPackage_PreservesConsumerAspireOverrideCleanupProof()
    {
        var packageRun = _producer.Packages;
        var workDirectory = packageRun.WorkDirectory;
        var feedDirectory = packageRun.FeedDirectory;
        var consumerDirectory = Path.Join(workDirectory, "consumer-aspire-override");
        var consumerPackagesDirectory = Path.Join(workDirectory, "consumer-cache", "aspire-override-packages");
        var consumerHttpCacheDirectory = Path.Join(workDirectory, "consumer-cache", "aspire-override-http");
        Directory.CreateDirectory(consumerDirectory);
        var isolatedDotNetEnvironment = CreateIsolatedNuGetEnvironment(
            consumerPackagesDirectory,
            consumerHttpCacheDirectory);

        try
        {
            var packedPackageVersion = packageRun.PackageVersion;
            var nugetConfigPath = await WriteNuGetConfigAsync(consumerDirectory, feedDirectory);
            await File.WriteAllTextAsync(
                Path.Join(consumerDirectory, "Consumer.csproj"),
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="ForgeTrust.AppSurface.Aspire.Testing" Version="{{packedPackageVersion}}" />
                    <PackageReference Include="Aspire.Hosting" Version="{{AspireOverrideVersion}}" />
                    <PackageReference Include="Aspire.Hosting.AppHost" Version="{{AspireOverrideVersion}}" />
                    <PackageReference Include="Aspire.Hosting.Testing" Version="{{AspireOverrideVersion}}" />
                    <PackageReference Include="Microsoft.Extensions.Hosting" Version="{{HostingBaselineVersion}}" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Join(consumerDirectory, "Consumer.cs"),
                """
                using CliFx.Binding;
                using ForgeTrust.AppSurface.Aspire;
                using ForgeTrust.AppSurface.Aspire.Testing;
                using ForgeTrust.AppSurface.Core;
                using Microsoft.Extensions.DependencyInjection;
                using Microsoft.Extensions.Hosting;
                using Microsoft.Extensions.Logging;

                public sealed class ConsumerAppHost
                {
                    public static string ProjectPath => AppContext.BaseDirectory;
                }

                public sealed class ConsumerModule : IAppSurfaceHostModule
                {
                    public void ConfigureServices(StartupContext context, IServiceCollection services) { }
                    public void RegisterDependentModules(ModuleDependencyBuilder builder) { }
                    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder) { }
                    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder) { }
                }

                [Command("consumer")]
                public sealed partial class ConsumerProfile : AspireProfile
                {
                    public ConsumerProfile(ILogger<ConsumerProfile> logger) : base(logger) { }
                    public override IEnumerable<IAspireComponent> GetComponents() => [];
                }

                public static class ConsumerProof
                {
                    public static Task<AppSurfaceAspireProfileTestingBuilder> CreateAsync() =>
                        AppSurfaceAspireTestingBuilder.CreateAsync<ConsumerAppHost, ConsumerModule, ConsumerProfile>();

                    public static async Task Main()
                    {
                        await using var builder = await CreateAsync();
                        builder.Services.AddSingleton<ConsumerBuildProbe>();
                        builder.Services.AddSingleton<IHostLifetime>(services =>
                        {
                            ConsumerBuildProbe.Instance = services.GetRequiredService<ConsumerBuildProbe>();
                            throw new ConsumerBuildException();
                        });

                        try
                        {
                            await builder.BuildAsync();
                            throw new InvalidOperationException("Expected packed Aspire host construction to fail.");
                        }
                        catch (ConsumerBuildException)
                        {
                        }

                        if (ConsumerBuildProbe.Instance?.IsDisposed != true)
                        {
                            throw new InvalidOperationException("Expected the packed package to dispose the partial provider.");
                        }
                    }
                }

                public sealed class ConsumerBuildProbe : IDisposable
                {
                    public static ConsumerBuildProbe? Instance { get; set; }
                    public bool IsDisposed { get; private set; }
                    public void Dispose() => IsDisposed = true;
                }

                public sealed class ConsumerBuildException : Exception
                {
                }
                """);

            AssertConsumerProjectHasNoProjectReferences(Path.Join(consumerDirectory, "Consumer.csproj"));
            await RunDotNetAsync(
                consumerDirectory,
                ["restore", "Consumer.csproj", "--configfile", nugetConfigPath, "--force", "--no-cache", "--nologo"],
                "Aspire 13.4.6 override consumer restore",
                isolatedDotNetEnvironment,
                RemainingBudget(packageRun.Budget, "consumer proof"));
            await AssertConsumerAssetsAsync(
                consumerDirectory,
                consumerPackagesDirectory,
                packageRun,
                AspireOverrideVersion);
            await RunDotNetAsync(
                consumerDirectory,
                ["build", "Consumer.csproj", "--no-restore", "--nologo"],
                "Aspire 13.4.6 override consumer build",
                isolatedDotNetEnvironment,
                RemainingBudget(packageRun.Budget, "consumer proof"));
            await RunDotNetAsync(
                consumerDirectory,
                ["run", "--project", "Consumer.csproj", "--no-build", "--no-restore"],
                "Aspire 13.4.6 override cleanup proof",
                new Dictionary<string, string>(isolatedDotNetEnvironment)
                {
                    ["ASPIRE_DCP_PATH"] = "dummy",
                    ["ASPIRE_DASHBOARD_PATH"] = "dummy"
                },
                RemainingBudget(packageRun.Budget, "override cleanup proof"));

            _output.WriteLine(
                $"Legacy override proof passed: AppSurfaceSourceCommit={packageRun.SourceCommit}; " +
                $"PackageVersion={packageRun.PackageVersion}; ConsumerAspireVersion={AspireOverrideVersion}; " +
                $"Feed={packageRun.FeedDirectory}; PartialProviderDisposed=true.");
        }
        finally
        {
            TryDeleteDirectory(consumerDirectory, _output.WriteLine);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PackedPackage_ConfiguredPortableConsumerComposesReceiptGraphsAtMinimumVersions()
    {
        var packageRun = _producer.Packages;
        var consumerDirectory = Path.Join(packageRun.WorkDirectory, "consumer-configured-baseline");
        var consumerPackagesDirectory = Path.Join(packageRun.WorkDirectory, "consumer-cache", "configured-baseline-packages");
        var consumerHttpCacheDirectory = Path.Join(packageRun.WorkDirectory, "consumer-cache", "configured-baseline-http");
        Directory.CreateDirectory(consumerDirectory);
        var isolatedDotNetEnvironment = CreateIsolatedNuGetEnvironment(
            consumerPackagesDirectory,
            consumerHttpCacheDirectory);

        try
        {
            var fixtureDirectory = Path.Join(
                packageRun.RepositoryRoot,
                "Aspire",
                "ForgeTrust.AppSurface.Aspire.Testing.Tests",
                "Fixtures",
                "PortableConsumer");
            var consumerProjectPath = Path.Join(consumerDirectory, "Consumer.csproj");
            var projectTemplate = await File.ReadAllTextAsync(Path.Join(fixtureDirectory, "Consumer.csproj.template"));
            Assert.Contains("{{PACKAGE_VERSION}}", projectTemplate, StringComparison.Ordinal);
            await File.WriteAllTextAsync(
                consumerProjectPath,
                projectTemplate.Replace("{{PACKAGE_VERSION}}", packageRun.PackageVersion, StringComparison.Ordinal));
            File.Copy(
                Path.Join(fixtureDirectory, "Program.cs.fixture"),
                Path.Join(consumerDirectory, "Program.cs"));
            AssertConsumerProjectHasNoProjectReferences(consumerProjectPath);

            var nugetConfigPath = await WriteNuGetConfigAsync(consumerDirectory, packageRun.FeedDirectory);
            await RunDotNetAsync(
                consumerDirectory,
                ["restore", "Consumer.csproj", "--configfile", nugetConfigPath, "--force", "--no-cache", "--nologo"],
                "Aspire 13.4.4 configured portable consumer restore",
                isolatedDotNetEnvironment,
                RemainingBudget(packageRun.Budget, "consumer proof"));
            await AssertConsumerAssetsAsync(
                consumerDirectory,
                consumerPackagesDirectory,
                packageRun,
                AspireBaselineVersion);
            await RunDotNetAsync(
                consumerDirectory,
                ["build", "Consumer.csproj", "--no-restore", "--nologo"],
                "Aspire 13.4.4 configured portable consumer build",
                isolatedDotNetEnvironment,
                RemainingBudget(packageRun.Budget, "consumer proof"));

            var output = await RunDotNetAsync(
                consumerDirectory,
                ["run", "--project", "Consumer.csproj", "--no-build", "--no-restore", "--no-launch-profile"],
                "Aspire 13.4.4 configured graph execution",
                new Dictionary<string, string>(isolatedDotNetEnvironment)
                {
                    ["ASPIRE_DCP_PATH"] = "dummy",
                    ["ASPIRE_DASHBOARD_PATH"] = "dummy"
                },
                RemainingBudget(packageRun.Budget, "configured graph execution"));
            Assert.Contains("PORTABLE_GRAPH_PROOF", output, StringComparison.Ordinal);
            Assert.Contains(SkoolitSourceCommit, output, StringComparison.Ordinal);
            Assert.Contains("receiptsOn=true/SeededInternal/household_dev_billing", output, StringComparison.Ordinal);
            Assert.Contains("receiptsOff=false/Disabled/", output, StringComparison.Ordinal);

            _output.WriteLine(
                $"Configured packed-consumer proof passed: AppSurfaceSourceCommit={packageRun.SourceCommit}; " +
                $"SkoolitFixtureSourceCommit={SkoolitSourceCommit}; PackageVersion={packageRun.PackageVersion}; " +
                $"AspireVersion={AspireBaselineVersion}; HostingVersion={HostingBaselineVersion}; " +
                $"Provider=Microsoft.Extensions.DependencyInjection; Feed={packageRun.FeedDirectory}.");
            _output.WriteLine(output);
        }
        finally
        {
            TryDeleteDirectory(consumerDirectory, _output.WriteLine);
        }
    }

    private static async Task<PackageRun> CreatePackedProducerPackagesAsync()
    {
        var budget = Stopwatch.StartNew();
        var repositoryRoot = GetRepositoryRoot();
        var workDirectory = Path.Join(Path.GetTempPath(), $"appsurface-issue866-packed-consumer-&-{Guid.NewGuid():N}");
        var feedDirectory = Path.Join(workDirectory, "isolated-local-feed");
        var producerPackagesDirectory = Path.Join(workDirectory, "producer-cache", "packages");
        var producerHttpCacheDirectory = Path.Join(workDirectory, "producer-cache", "http");
        var producerArtifactsDirectory = Path.Join(workDirectory, "producer-artifacts");
        Directory.CreateDirectory(feedDirectory);
        Directory.CreateDirectory(producerArtifactsDirectory);
        var producerEnvironment = CreateIsolatedNuGetEnvironment(
            producerPackagesDirectory,
            producerHttpCacheDirectory);

        try
        {
            var sourceCommit = (await RunProcessAsync(
                    "git",
                    ["rev-parse", "HEAD"],
                    repositoryRoot,
                    "read worktree source commit",
                    timeout: TimeSpan.FromSeconds(15)))
                .Trim();
            Assert.Equal(40, sourceCommit.Length);
            var packageVersion = $"0.0.0-issue866.{sourceCommit[..12]}.{Guid.NewGuid():N}";
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            var packageProperties = new[]
            {
                $"/p:Version={packageVersion}",
                $"/p:PackageVersion={packageVersion}",
                $"/p:RepositoryCommit={sourceCommit}",
                "/p:RepositoryUrl=https://github.com/forge-trust/AppSurface",
                "/p:PublishRepositoryUrl=true"
            };
            var producerProject = "Aspire/ForgeTrust.AppSurface.Aspire.Testing/ForgeTrust.AppSurface.Aspire.Testing.csproj";
            var producerConfig = await WriteNuGetConfigAsync(workDirectory, feedDirectory);
            var buildArguments = new List<string>
            {
                "build",
                producerProject,
                "--configuration",
                configuration,
                "--force",
                "--no-incremental",
                "--artifacts-path",
                producerArtifactsDirectory,
                "--nologo",
                $"/p:RestoreConfigFile={producerConfig}"
            };
            buildArguments.AddRange(packageProperties);
            await RunDotNetAsync(
                repositoryRoot,
                buildArguments,
                $"fresh Issue #866 producer build ({sourceCommit[..12]}, {packageVersion})",
                producerEnvironment,
                RemainingBudget(budget, "fresh producer build"));

            var projects = new[]
            {
                "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                "Deployment/ForgeTrust.AppSurface.Deployment/ForgeTrust.AppSurface.Deployment.csproj",
                "Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj",
                "Aspire/ForgeTrust.AppSurface.Aspire/ForgeTrust.AppSurface.Aspire.csproj",
                producerProject
            };
            foreach (var project in projects)
            {
                var packArguments = new List<string>
                {
                    "pack",
                    project,
                    "--configuration",
                    configuration,
                    "--no-build",
                    "--no-restore",
                    "--artifacts-path",
                    producerArtifactsDirectory,
                    "--output",
                    feedDirectory,
                    "--nologo"
                };
                packArguments.AddRange(packageProperties);
                await RunDotNetAsync(
                    repositoryRoot,
                    packArguments,
                    $"no-build pack {Path.GetFileNameWithoutExtension(project)} ({packageVersion})",
                    producerEnvironment,
                    RemainingBudget(budget, "producer package pack"));
            }

            var packagePaths = Directory.GetFiles(feedDirectory, "*.nupkg");
            var packages = packagePaths.ToDictionary(
                GetNuspecPackageId,
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal(
                PackedPackageIds.OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase),
                packages.Keys.OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase));
            foreach (var packagePath in packagePaths)
            {
                AssertPackageProvenance(packagePath, packageVersion, sourceCommit);
            }

            AssertMinimumAspireDependencies(
                packages[AppSurfaceTestingPackageId],
                packageVersion,
                sourceCommit);

            return new PackageRun(
                repositoryRoot,
                workDirectory,
                feedDirectory,
                packageVersion,
                sourceCommit,
                packages,
                budget);
        }
        catch
        {
            TryDeleteDirectory(workDirectory, Console.Error.WriteLine);
            throw;
        }
    }

    private static async Task<string> WriteNuGetConfigAsync(string consumerDirectory, string feedDirectory)
    {
        var nugetConfigPath = Path.Join(consumerDirectory, "NuGet.config");
        var nugetConfiguration = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                "configuration",
                new XElement(
                    "packageSources",
                    new XElement("clear"),
                    new XElement(
                        "add",
                        new XAttribute("key", "issue866-worktree-feed"),
                        new XAttribute("value", feedDirectory)),
                    new XElement(
                        "add",
                        new XAttribute("key", "nuget.org"),
                        new XAttribute("value", "https://api.nuget.org/v3/index.json")))));
        await using var stream = File.Create(nugetConfigPath);
        await nugetConfiguration.SaveAsync(stream, SaveOptions.None, CancellationToken.None);
        return nugetConfigPath;
    }

    private static Dictionary<string, string> CreateIsolatedNuGetEnvironment(
        string packagesDirectory,
        string httpCacheDirectory)
    {
        Directory.CreateDirectory(packagesDirectory);
        Directory.CreateDirectory(httpCacheDirectory);
        return new Dictionary<string, string>
        {
            ["DOTNET_USE_POLLING_FILE_WATCHER"] = "1",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["UseSharedCompilation"] = "false",
            ["NUGET_PACKAGES"] = packagesDirectory,
            ["NUGET_HTTP_CACHE_PATH"] = httpCacheDirectory
        };
    }

    private static void AssertConsumerProjectHasNoProjectReferences(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "ProjectReference");
        Assert.Contains(document.Descendants(), element => element.Name.LocalName == "PackageReference");
    }

    private static async Task AssertConsumerAssetsAsync(
        string consumerDirectory,
        string consumerPackagesDirectory,
        PackageRun packageRun,
        string expectedAspireVersion)
    {
        var assetsPath = Path.Join(consumerDirectory, "obj", "project.assets.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(assetsPath));
        var root = document.RootElement;
        var packageFolders = root.GetProperty("packageFolders");
        var consumerPackageFolder = Assert.Single(packageFolders.EnumerateObject());
        Assert.Equal(
            NormalizeDirectory(consumerPackagesDirectory),
            NormalizeDirectory(consumerPackageFolder.Name));

        var restoreSources = root.GetProperty("project").GetProperty("restore").GetProperty("sources");
        Assert.Equal(2, restoreSources.EnumerateObject().Count());
        Assert.Contains(
            restoreSources.EnumerateObject(),
            source => NormalizeDirectory(source.Name) == NormalizeDirectory(packageRun.FeedDirectory));
        Assert.Contains(
            restoreSources.EnumerateObject(),
            source => source.Name.Equals(
                "https://api.nuget.org/v3/index.json",
                StringComparison.OrdinalIgnoreCase));

        var libraries = root.GetProperty("libraries");
        foreach (var packageId in PackedPackageIds)
        {
            var packageKey = $"{packageId}/{packageRun.PackageVersion}";
            var library = FindJsonProperty(libraries, packageKey, "resolved packed AppSurface package");
            Assert.Equal("package", library.GetProperty("type").GetString());
            Assert.Equal(
                $"{packageId.ToLowerInvariant()}/{packageRun.PackageVersion}",
                library.GetProperty("path").GetString());

            var packagePath = packageRun.Packages[packageId];
            var packageHash = Convert.ToBase64String(
                SHA512.HashData(await File.ReadAllBytesAsync(packagePath)));
            var cachedHashPath = Path.Join(
                consumerPackagesDirectory,
                packageId.ToLowerInvariant(),
                packageRun.PackageVersion,
                $"{packageId.ToLowerInvariant()}.{packageRun.PackageVersion}.nupkg.sha512");
            Assert.True(File.Exists(cachedHashPath), $"Expected isolated package cache hash at '{cachedHashPath}'.");
            Assert.Equal(packageHash, (await File.ReadAllTextAsync(cachedHashPath)).Trim());
            Assert.Equal(packageHash, library.GetProperty("sha512").GetString());
        }

        AssertResolvedPackageVersion(libraries, "Aspire.Hosting", expectedAspireVersion);
        AssertResolvedPackageVersion(libraries, "Aspire.Hosting.AppHost", expectedAspireVersion);
        AssertResolvedPackageVersion(libraries, "Aspire.Hosting.Testing", expectedAspireVersion);
        AssertResolvedPackageVersion(libraries, "Microsoft.Extensions.Hosting", HostingBaselineVersion);
        AssertResolvedPackageVersion(
            libraries,
            "Microsoft.Extensions.DependencyInjection",
            HostingBaselineVersion);

        var directDependencies = root.GetProperty("project")
            .GetProperty("frameworks")
            .GetProperty("net10.0")
            .GetProperty("dependencies");
        Assert.Equal(
            "Package",
            FindJsonProperty(directDependencies, AppSurfaceTestingPackageId, "direct package dependency")
                .GetProperty("target")
                .GetString());
    }

    private static void AssertResolvedPackageVersion(JsonElement libraries, string packageId, string expectedVersion)
    {
        var match = Assert.Single(
            libraries.EnumerateObject(), library =>
                library.Name.StartsWith($"{packageId}/", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expectedVersion, match.Name[(packageId.Length + 1)..]);
    }

    private static JsonElement FindJsonProperty(JsonElement parent, string name, string description)
    {
        foreach (var property in parent.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new InvalidOperationException($"project.assets.json did not contain the {description} '{name}'.");
    }

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string GetNuspecPackageId(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var nuspecEntry = Assert.Single(
            archive.Entries,
            entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using var stream = nuspecEntry.Open();
        var metadata = XDocument.Load(stream).Descendants().Single(element => element.Name.LocalName == "metadata");
        return metadata.Elements().Single(element => element.Name.LocalName == "id").Value;
    }

    private static void AssertPackageProvenance(string packagePath, string expectedVersion, string expectedCommit)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var nuspecEntry = Assert.Single(
            archive.Entries,
            entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using var stream = nuspecEntry.Open();
        var metadata = XDocument.Load(stream).Descendants().Single(element => element.Name.LocalName == "metadata");
        Assert.Equal(expectedVersion, metadata.Elements().Single(element => element.Name.LocalName == "version").Value);
        var repository = metadata.Elements().Single(element => element.Name.LocalName == "repository");
        Assert.Equal("git", repository.Attribute("type")?.Value);
        Assert.Equal("https://github.com/forge-trust/AppSurface", repository.Attribute("url")?.Value);
        Assert.Equal(expectedCommit, repository.Attribute("commit")?.Value);
    }

    private static void AssertMinimumAspireDependencies(
        string packagePath,
        string expectedPackageVersion,
        string expectedCommit)
    {
        AssertPackageProvenance(packagePath, expectedPackageVersion, expectedCommit);
        using var archive = ZipFile.OpenRead(packagePath);
        var nuspecEntry = Assert.Single(archive.Entries, entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using var stream = nuspecEntry.Open();
        var document = XDocument.Load(stream);
        var dependencies = document.Descendants()
            .Where(element => element.Name.LocalName == "dependency")
            .ToDictionary(
                element => element.Attribute("id")?.Value ?? string.Empty,
                element => element.Attribute("version")?.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        Assert.Equal("13.4.4", dependencies["Aspire.Hosting"]);
        Assert.Equal("13.4.4", dependencies["Aspire.Hosting.AppHost"]);
        Assert.Equal("13.4.4", dependencies["Aspire.Hosting.Testing"]);
        Assert.Equal(expectedPackageVersion, dependencies["ForgeTrust.AppSurface.Aspire"]);
    }

    private static async Task<string> RunDotNetAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string stage,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        TimeSpan? timeout = null)
    {
        return await RunProcessAsync("dotnet", arguments, workingDirectory, stage, environmentVariables, timeout);
    }

    private static async Task<string> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string stage,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        TimeSpan? timeout = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        process.StartInfo.Environment["UseSharedCompilation"] = "false";
        if (environmentVariables is not null)
        {
            foreach (var (name, value) in environmentVariables)
            {
                process.StartInfo.Environment[name] = value;
            }
        }

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start(), $"Expected the '{stage}' child process to start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(2);
        using var timeoutSource = new CancellationTokenSource(effectiveTimeout);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(), standardOutput, standardError).WaitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            Exception? cleanupFailure = null;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception error)
            {
                cleanupFailure = error;
            }

            try
            {
                await Task.WhenAll(process.WaitForExitAsync(), standardOutput, standardError)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error)
            {
                cleanupFailure ??= error;
            }

            var timedOutOutput = string.Join(Environment.NewLine,
                CompletedOutput(standardOutput), CompletedOutput(standardError));
            throw new TimeoutException(
                $"The '{stage}' child process exceeded its bounded timeout of {effectiveTimeout}." +
                $" Cleanup result: {cleanupFailure?.GetType().Name ?? "completed"}." +
                $"{Environment.NewLine}{timedOutOutput}");
        }

        var output = string.Join(Environment.NewLine, await standardOutput, await standardError);

        Assert.True(
            process.ExitCode == 0,
            $"Stage '{stage}' failed: {executable} {string.Join(' ', arguments)} exited {process.ExitCode}." +
            $"{Environment.NewLine}{output}");
        return output;
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "ForgeTrust.AppSurface.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }

    private static string CompletedOutput(Task<string> output)
    {
        if (output.IsCompletedSuccessfully) return output.Result;
        _ = output.Exception;
        return "Output was not drained before the cleanup deadline.";
    }

    private static TimeSpan RemainingBudget(Stopwatch budget, string stage)
    {
        var remaining = TimeSpan.FromMinutes(8) - budget.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException($"The shared packed proof exceeded its eight-minute budget before '{stage}'.");
        }

        // Each child retains the original two-minute bound within the expanded proof's total deadline.
        return remaining < TimeSpan.FromMinutes(2) ? remaining : TimeSpan.FromMinutes(2);
    }

    private static void TryDeleteDirectory(string directory, Action<string> warning)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception error)
        {
            try
            {
                warning($"Secondary packed-proof cleanup failed: {error.GetType().Name}; retained '{directory}'.");
            }
            catch (Exception)
            {
                // A diagnostic sink failure must not replace the proof's primary failure.
            }
        }
    }

    public sealed class PackedProducerFixture : IAsyncLifetime
    {
        internal PackageRun Packages { get; private set; } = null!;
        public async Task InitializeAsync() => Packages = await CreatePackedProducerPackagesAsync();
        public Task DisposeAsync()
        {
            if (Packages is not null) TryDeleteDirectory(Packages.WorkDirectory, Console.Error.WriteLine);
            return Task.CompletedTask;
        }
    }

    internal sealed record PackageRun(
        string RepositoryRoot,
        string WorkDirectory,
        string FeedDirectory,
        string PackageVersion,
        string SourceCommit,
        IReadOnlyDictionary<string, string> Packages,
        Stopwatch Budget);
}
