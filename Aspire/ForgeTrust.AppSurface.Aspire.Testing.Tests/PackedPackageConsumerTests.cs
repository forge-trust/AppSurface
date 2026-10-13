using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Testing;
using Xunit.Abstractions;

public sealed class PackedPackageConsumerTests
{
    private const string PackageVersion = "0.0.0-consumer-override-proof";
    private const string AspireOverrideVersion = "13.4.6";
    private const string PinnedConsumerCommit = "a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e";
    private readonly ITestOutputHelper _output;

    public PackedPackageConsumerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PackedPackage_SetsAspireMinimumAndRunsConsumerOverrideCleanup()
    {
        var proofStopwatch = Stopwatch.StartNew();
        var repositoryRoot = GetRepositoryRoot();
        var canonicalTemporaryRoot = ResolveDirectoryAliases(Path.GetTempPath());
        var workDirectory = Path.Join(canonicalTemporaryRoot, $"appsurface-aspire-testing-consumer-&-{Guid.NewGuid():N}");
        var feedDirectory = Path.Join(workDirectory, "feed");
        var consumerDirectory = Path.Join(workDirectory, "consumer");
        var appHostDirectory = Path.Join(workDirectory, "checkout", "src", "Skoolit.AppHost");
        var appHostGitDirectory = Path.Join(workDirectory, "checkout");
        var firstWorkingDirectory = Path.Join(workDirectory, "unrelated-a");
        var secondWorkingDirectory = Path.Join(workDirectory, "unrelated-b");
        var nugetHttpCacheDirectory = Path.Join(workDirectory, "nuget-http-cache");
        var consumerPackagesDirectory = Path.Join(workDirectory, "consumer-nuget-packages");
        Directory.CreateDirectory(feedDirectory);
        Directory.CreateDirectory(consumerDirectory);
        Directory.CreateDirectory(appHostDirectory);
        Directory.CreateDirectory(firstWorkingDirectory);
        Directory.CreateDirectory(secondWorkingDirectory);
        Directory.CreateDirectory(nugetHttpCacheDirectory);
        var isolatedDotNetEnvironment = new Dictionary<string, string>
        {
            // This proof creates a disposable consumer, so keep restore state and file watching out of shared host state.
            ["DOTNET_USE_POLLING_FILE_WATCHER"] = "1",
            ["NUGET_HTTP_CACHE_PATH"] = nugetHttpCacheDirectory
        };
        var consumerDotNetEnvironment = new Dictionary<string, string>(isolatedDotNetEnvironment)
        {
            ["NUGET_PACKAGES"] = consumerPackagesDirectory,
            ["ASPIRE_DCP_PATH"] = "dummy",
            ["ASPIRE_DASHBOARD_PATH"] = "dummy",
            ["CONSUMER_APPHOST_DIRECTORY"] = appHostDirectory
        };

        try
        {
            await File.WriteAllTextAsync(Path.Join(appHostGitDirectory, ".git"), "gitdir: .git/worktrees/consumer-proof");
            Directory.CreateDirectory(Path.Join(appHostDirectory, "keycloak"));
            await File.WriteAllTextAsync(
                Path.Join(appHostDirectory, "keycloak", "skoolit-realm.json"),
                "{}");
            Directory.CreateDirectory(
                Path.Join(workDirectory, "checkout", "src", "Skoolit.KeycloakTheme", "theme", "skoolit"));

            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            var projects = new[]
            {
                "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                "Deployment/ForgeTrust.AppSurface.Deployment/ForgeTrust.AppSurface.Deployment.csproj",
                "Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj",
                "Aspire/ForgeTrust.AppSurface.Aspire/ForgeTrust.AppSurface.Aspire.csproj",
                "Aspire/ForgeTrust.AppSurface.Aspire.Testing/ForgeTrust.AppSurface.Aspire.Testing.csproj"
            };

            await RunDotNetAsync(
                repositoryRoot,
                ["build", projects[^1], "--configuration", configuration, "--no-restore", "--nologo"],
                isolatedDotNetEnvironment);

            var frameworkRevision = await RunGitCaptureAsync(repositoryRoot, "rev-parse", "HEAD");
            var frameworkDirtyDiff = await RunGitCaptureAsync(repositoryRoot, "diff", "--binary", "HEAD");
            var frameworkDirtyDiffIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(frameworkDirtyDiff)));
            _output.WriteLine(
                $"Framework revision={frameworkRevision}; dirty-diff-sha256={frameworkDirtyDiffIdentity}; " +
                $"framework package version={PackageVersion}; configuration={configuration}; " +
                "dependencies-built-before-no-build-pack=true");

            foreach (var project in projects)
            {
                await RunDotNetAsync(
                    repositoryRoot,
                    [
                        "pack",
                        project,
                        "--configuration",
                        configuration,
                        "--no-build",
                        "--no-restore",
                        "--output",
                        feedDirectory,
                        $"/p:Version={PackageVersion}",
                        $"/p:PackageVersion={PackageVersion}"
                    ],
                    isolatedDotNetEnvironment);
            }

            var packagePath = Assert.Single(
                Directory.GetFiles(feedDirectory, "ForgeTrust.AppSurface.Aspire.Testing.*.nupkg"));
            var packedPackageVersion = AssertMinimumAspireDependencies(packagePath);
            await using (var packageStream = File.OpenRead(packagePath))
            {
                var packageDigest = Convert.ToHexString(await SHA256.HashDataAsync(packageStream));
                _output.WriteLine(
                    $"Packed consumer package=ForgeTrust.AppSurface.Aspire.Testing {packedPackageVersion}; " +
                    $"sha256={packageDigest}; Aspire override={AspireOverrideVersion}; " +
                    $"pinned consumer={PinnedConsumerCommit}; " +
                    "assets=src/Skoolit.AppHost/keycloak/skoolit-realm.json, " +
                    "src/Skoolit.KeycloakTheme/theme/skoolit");
            }

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
                            new XAttribute("key", "consumer-proof"),
                            new XAttribute("value", feedDirectory)),
                        new XElement(
                            "add",
                            new XAttribute("key", "nuget.org"),
                            new XAttribute("value", "https://api.nuget.org/v3/index.json")))));
            await using (var nugetConfigStream = File.Create(nugetConfigPath))
            {
                await nugetConfiguration.SaveAsync(
                    nugetConfigStream,
                    SaveOptions.None,
                    CancellationToken.None);
            }
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
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Join(consumerDirectory, "Consumer.cs"),
                """
                using CliFx.Binding;
                using Aspire.Hosting;
                using Aspire.Hosting.ApplicationModel;
                using ForgeTrust.AppSurface.Aspire;
                using ForgeTrust.AppSurface.Aspire.Testing;
                using ForgeTrust.AppSurface.Core;
                using Microsoft.Extensions.DependencyInjection;
                using Microsoft.Extensions.Hosting;
                using Microsoft.Extensions.Logging;

                public sealed class ConsumerAppHost
                {
                    public static string ProjectPath => Environment.GetEnvironmentVariable("CONSUMER_APPHOST_DIRECTORY")!;
                }

                public sealed class RepositoryAssetsComponent : IAspireComponent<ParameterResource>
                {
                    public IResourceBuilder<ParameterResource> Generate(
                        AspireStartupContext context,
                        IDistributedApplicationBuilder appBuilder)
                    {
                        var expectedRoot = Directory.GetParent(ConsumerAppHost.ProjectPath)!.Parent!.FullName;
                        var realmPath = context.GetPathFromRoot("src/Skoolit.AppHost/keycloak/skoolit-realm.json");
                        var themePath = context.GetPathFromRoot("src/Skoolit.KeycloakTheme/theme/skoolit");
                        var expectedRealmPath = Path.GetFullPath(Path.Join(expectedRoot, "src/Skoolit.AppHost/keycloak/skoolit-realm.json"));
                        var expectedThemePath = Path.GetFullPath(Path.Join(expectedRoot, "src/Skoolit.KeycloakTheme/theme/skoolit"));

                        if (Path.GetFullPath(context.GetPathFromRoot(string.Empty)) != expectedRoot ||
                            realmPath != expectedRealmPath ||
                            themePath != expectedThemePath || !Path.IsPathFullyQualified(realmPath) ||
                            !Path.IsPathFullyQualified(themePath))
                        {
                            throw new InvalidOperationException(
                                $"Repository asset proof failed: root='{context.GetPathFromRoot(string.Empty)}', " +
                                $"realm='{realmPath}', theme='{themePath}'.");
                        }

                        if (!File.Exists(realmPath))
                        {
                            throw new FileNotFoundException("Expected the repository realm fixture to exist.", realmPath);
                        }

                        if (!Directory.Exists(themePath))
                        {
                            throw new DirectoryNotFoundException("Expected the repository theme fixture to exist.");
                        }

                        Console.WriteLine(
                            $"Repository path evidence: selected-root='{Path.GetFullPath(context.GetPathFromRoot(string.Empty))}'; " +
                            $"realm expected='{expectedRealmPath}', actual='{realmPath}', exists={File.Exists(realmPath)}; " +
                            $"theme expected='{expectedThemePath}', actual='{themePath}', exists={Directory.Exists(themePath)}.");

                        return appBuilder.AddParameter("repository-assets-assertion", "verified");
                    }
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
                    public override IEnumerable<IAspireComponent> GetComponents() =>
                        [new RepositoryAssetsComponent(), new KeycloakAssetsComponent()];
                }

                public static class ConsumerProof
                {
                    public static Task<AppSurfaceAspireProfileTestingBuilder> CreateAsync() =>
                        AppSurfaceAspireTestingBuilder.CreateAsync<ConsumerAppHost, ConsumerModule, ConsumerProfile>();

                    public static async Task Main(string[] args)
                    {
                        var initialDirectory = Environment.CurrentDirectory;
                        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || initialDirectory != args[0])
                        {
                            throw new InvalidOperationException("Consumer did not start in its requested unrelated working directory.");
                        }
                        Console.WriteLine($"Consumer CWD before path resolution='{initialDirectory}'.");

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

                        if (Environment.CurrentDirectory != initialDirectory)
                        {
                            throw new InvalidOperationException("Consumer path resolution changed its working directory.");
                        }
                        Console.WriteLine($"Consumer CWD after path resolution='{Environment.CurrentDirectory}'.");
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
            await File.WriteAllTextAsync(
                Path.Join(consumerDirectory, "KeycloakAssetsComponent.cs"),
                await ReadGuideComponentSnippetAsync(repositoryRoot));

            await RunDotNetAsync(
                consumerDirectory,
                ["build", Path.Join(consumerDirectory, "Consumer.csproj"), "--configuration", configuration, "--configfile", nugetConfigPath, "--nologo"],
                consumerDotNetEnvironment);

            var originalWorkingDirectory = Environment.CurrentDirectory;
            try
            {
                foreach (var workingDirectory in new[] { firstWorkingDirectory, secondWorkingDirectory })
                {
                    await RunDotNetAsync(
                        workingDirectory,
                        [Path.Join(consumerDirectory, "bin", configuration, "net10.0", "Consumer.dll"), workingDirectory],
                        consumerDotNetEnvironment);
                    Assert.Equal(originalWorkingDirectory, Environment.CurrentDirectory);
                }
            }
            finally
            {
                Assert.Equal(originalWorkingDirectory, Environment.CurrentDirectory);
            }

            proofStopwatch.Stop();
            _output.WriteLine($"Complete packed consumer proof elapsed={proofStopwatch.Elapsed}.");
        }
        finally
        {
            Directory.Delete(workDirectory, recursive: true);
        }
    }

    private static string AssertMinimumAspireDependencies(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var nuspecEntry = Assert.Single(archive.Entries, entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using var stream = nuspecEntry.Open();
        var document = XDocument.Load(stream);
        var packageVersion = Assert.Single(document.Descendants(), element => element.Name.LocalName == "version").Value;
        var dependencies = document.Descendants()
            .Where(element => element.Name.LocalName == "dependency")
            .ToDictionary(
                element => element.Attribute("id")?.Value ?? string.Empty,
                element => element.Attribute("version")?.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        Assert.Equal("13.4.4", dependencies["Aspire.Hosting"]);
        Assert.Equal("13.4.4", dependencies["Aspire.Hosting.AppHost"]);
        Assert.Equal("13.4.4", dependencies["Aspire.Hosting.Testing"]);
        Assert.Equal(packageVersion, dependencies["ForgeTrust.AppSurface.Aspire"]);
        return packageVersion;
    }

    private async Task RunDotNetAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var stopwatch = Stopwatch.StartNew();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
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

        Assert.True(process.Start(), "Expected the dotnet consumer-proof process to start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited between the state check and the kill request.
            }

            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(standardOutput, standardError);
            stopwatch.Stop();
            _output.WriteLine(
                $"Consumer-proof process cwd='{workingDirectory}', command='dotnet {string.Join(' ', arguments)}', " +
                $"timed-out after {stopwatch.Elapsed}.");
            _output.WriteLine(await standardOutput);
            _output.WriteLine(await standardError);
            throw;
        }

        var output = string.Join(Environment.NewLine, await standardOutput, await standardError);
        stopwatch.Stop();
        _output.WriteLine(
            $"Consumer-proof process cwd='{workingDirectory}', command='dotnet {string.Join(' ', arguments)}', " +
            $"elapsed={stopwatch.Elapsed}; exit-code={process.ExitCode}.");
        _output.WriteLine(output);

        Assert.True(
            process.ExitCode == 0,
            $"dotnet {string.Join(' ', arguments)} failed with exit code {process.ExitCode}:{Environment.NewLine}{output}");
    }

    private static async Task<string> RunGitCaptureAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start(), "Expected the git identity process to start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited between the state check and the kill request.
            }

            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(standardOutput, standardError);
            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.TrimEnd('\r', '\n');
    }

    private static async Task<string> ReadGuideComponentSnippetAsync(string repositoryRoot)
    {
        var guide = await File.ReadAllTextAsync(
            Path.Join(repositoryRoot, "Aspire", "ForgeTrust.AppSurface.Aspire", "README.md"));
        const string sectionHeading = "## Repository-relative assets";
        const string codeFence = "```csharp";
        const string closingFence = "```";
        var sectionStart = guide.IndexOf(sectionHeading, StringComparison.Ordinal);
        Assert.True(sectionStart >= 0, "Expected the canonical repository-relative assets guide section.");

        var sectionEnd = guide.IndexOf("\n## ", sectionStart + sectionHeading.Length, StringComparison.Ordinal);
        if (sectionEnd < 0)
        {
            sectionEnd = guide.Length;
        }

        var codeStart = guide.IndexOf(codeFence, sectionStart, sectionEnd - sectionStart, StringComparison.Ordinal);
        Assert.True(codeStart >= 0, "Expected a C# component example in the repository-relative assets section.");
        codeStart += codeFence.Length;
        if (guide[codeStart] == '\r')
        {
            codeStart++;
        }
        if (guide[codeStart] == '\n')
        {
            codeStart++;
        }

        var codeEnd = guide.IndexOf(closingFence, codeStart, sectionEnd - codeStart, StringComparison.Ordinal);
        Assert.True(codeEnd >= 0, "Expected the repository-relative assets C# example to close its code fence.");
        return guide[codeStart..codeEnd];
    }

    private static string ResolveDirectoryAliases(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var currentPath = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"Expected an absolute directory path, received '{path}'.");

        foreach (var segment in fullPath[currentPath.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = TestPathUtils.PathUnder(currentPath, segment);
            var linkTarget = new DirectoryInfo(currentPath).ResolveLinkTarget(returnFinalTarget: true);
            if (linkTarget is not null)
            {
                currentPath = linkTarget.FullName;
            }
        }

        return Path.GetFullPath(currentPath);
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
}
