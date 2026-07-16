using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

public sealed class PackedPackageConsumerTests
{
    private const string PackageVersion = "0.0.0-consumer-proof";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PackedPackage_PinsAspireAndCompilesDocumentedConsumer()
    {
        var repositoryRoot = GetRepositoryRoot();
        var workDirectory = Path.Join(Path.GetTempPath(), $"appsurface-aspire-testing-consumer-&-{Guid.NewGuid():N}");
        var feedDirectory = Path.Join(workDirectory, "feed");
        var consumerDirectory = Path.Join(workDirectory, "consumer");
        Directory.CreateDirectory(feedDirectory);
        Directory.CreateDirectory(consumerDirectory);

        try
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            var projects = new[]
            {
                "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                "Deployment/ForgeTrust.AppSurface.Deployment/ForgeTrust.AppSurface.Deployment.csproj",
                "Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj",
                "Aspire/ForgeTrust.AppSurface.Aspire/ForgeTrust.AppSurface.Aspire.csproj",
                "Aspire/ForgeTrust.AppSurface.Aspire.Testing/ForgeTrust.AppSurface.Aspire.Testing.csproj"
            };

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
                    ]);
            }

            var packagePath = Assert.Single(
                Directory.GetFiles(feedDirectory, "ForgeTrust.AppSurface.Aspire.Testing.*.nupkg"));
            var packedPackageVersion = AssertExactAspireDependencies(packagePath);

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
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="ForgeTrust.AppSurface.Aspire.Testing" Version="{{packedPackageVersion}}" />
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
                }
                """);

            await RunDotNetAsync(
                consumerDirectory,
                ["build", "Consumer.csproj", "--configfile", nugetConfigPath, "--nologo"]);
        }
        finally
        {
            Directory.Delete(workDirectory, recursive: true);
        }
    }

    private static string AssertExactAspireDependencies(string packagePath)
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

        Assert.Equal("[13.4.4]", dependencies["Aspire.Hosting"]);
        Assert.Equal("[13.4.4]", dependencies["Aspire.Hosting.AppHost"]);
        Assert.Equal("[13.4.4]", dependencies["Aspire.Hosting.Testing"]);
        Assert.Equal(packageVersion, dependencies["ForgeTrust.AppSurface.Aspire"]);
        return packageVersion;
    }

    private static async Task RunDotNetAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
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
            throw;
        }

        var output = string.Join(Environment.NewLine, await standardOutput, await standardError);

        Assert.True(
            process.ExitCode == 0,
            $"dotnet {string.Join(' ', arguments)} failed with exit code {process.ExitCode}:{Environment.NewLine}{output}");
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
