using ForgeTrust.AppSurface.Web;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class AppSurfaceWebDevelopmentPortDefaultsTests
{
    [Fact]
    public void Resolve_AppendsDeterministicPort_WhenNoExplicitUrlConfigurationExists()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var appBaseDirectory = environment.CreateApplicationBaseDirectory("workspace");

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            environment.WorkspaceRoot,
            appBaseDirectory,
            ReadDevelopmentEnvironment);

        Assert.NotNull(resolution.AppliedPort);
        Assert.Equal(environment.WorkspaceRoot, resolution.SeedPath);
        Assert.Equal(["--urls", $"http://localhost:{resolution.AppliedPort.Value}"], resolution.Args);
        Assert.InRange(resolution.AppliedPort.Value, 5600, 6599);
    }

    [Fact]
    public void Resolve_DoesNotAppendDeterministicPort_WhenEnvironmentIsNotDevelopment()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var appBaseDirectory = environment.CreateApplicationBaseDirectory("workspace");

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            environment.WorkspaceRoot,
            appBaseDirectory,
            key => key == "ASPNETCORE_ENVIRONMENT" ? Environments.Production : null);

        Assert.Null(resolution.AppliedPort);
        Assert.Empty(resolution.Args);
        Assert.Null(resolution.SeedPath);
    }

    [Fact]
    public void Resolve_DoesNotAppendDeterministicPort_WhenEnvironmentIsUnset()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var appBaseDirectory = environment.CreateApplicationBaseDirectory("workspace");

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            environment.WorkspaceRoot,
            appBaseDirectory,
            _ => null);

        Assert.Null(resolution.AppliedPort);
        Assert.Empty(resolution.Args);
        Assert.Null(resolution.SeedPath);
    }

    [Fact]
    public void Resolve_AppendsDeterministicPort_WhenCommandLineEnvironmentIsDevelopment()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var appBaseDirectory = environment.CreateApplicationBaseDirectory("workspace");
        var args = new[] { "--environment", Environments.Development };

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            args,
            environment.WorkspaceRoot,
            appBaseDirectory,
            _ => null);

        Assert.NotNull(resolution.AppliedPort);
        Assert.Equal(
            ["--environment", Environments.Development, "--urls", $"http://localhost:{resolution.AppliedPort.Value}"],
            resolution.Args);
    }

    [Fact]
    public void Resolve_AppendsDeterministicPort_WhenCommandLineEnvironmentIsDevelopmentInline()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var appBaseDirectory = environment.CreateApplicationBaseDirectory("workspace");
        var args = new[] { $"--environment={Environments.Development}" };

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            args,
            environment.WorkspaceRoot,
            appBaseDirectory,
            _ => null);

        Assert.NotNull(resolution.AppliedPort);
        Assert.Equal(
            [$"--environment={Environments.Development}", "--urls", $"http://localhost:{resolution.AppliedPort.Value}"],
            resolution.Args);
    }

    [Fact]
    public void Resolve_DoesNotAppendDeterministicPort_WhenCommandLineEnvironmentIsProduction()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var appBaseDirectory = environment.CreateApplicationBaseDirectory("workspace");
        var args = new[] { "--environment", Environments.Production };

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            args,
            environment.WorkspaceRoot,
            appBaseDirectory,
            ReadDevelopmentEnvironment);

        Assert.Null(resolution.AppliedPort);
        Assert.Same(args, resolution.Args);
        Assert.Null(resolution.SeedPath);
    }

    [Fact]
    public void Resolve_AppendsDeterministicPort_WhenDotnetEnvironmentIsDevelopment()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var appBaseDirectory = environment.CreateApplicationBaseDirectory("workspace");

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            environment.WorkspaceRoot,
            appBaseDirectory,
            key => key == "DOTNET_ENVIRONMENT" ? Environments.Development : null);

        Assert.NotNull(resolution.AppliedPort);
        Assert.Equal(["--urls", $"http://localhost:{resolution.AppliedPort.Value}"], resolution.Args);
    }

    [Theory]
    [InlineData("Production", "Development", false)]
    [InlineData("Development", "Production", true)]
    public void Resolve_UsesAspNetCoreEnvironment_WhenEnvironmentVariablesConflict(
        string aspNetCoreEnvironment,
        string dotnetEnvironment,
        bool shouldApplyPort)
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var appBaseDirectory = environment.CreateApplicationBaseDirectory("workspace");

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            environment.WorkspaceRoot,
            appBaseDirectory,
            key => key switch
            {
                "ASPNETCORE_ENVIRONMENT" => aspNetCoreEnvironment,
                "DOTNET_ENVIRONMENT" => dotnetEnvironment,
                _ => null
            });

        if (shouldApplyPort)
        {
            Assert.NotNull(resolution.AppliedPort);
            Assert.Equal(["--urls", $"http://localhost:{resolution.AppliedPort.Value}"], resolution.Args);
        }
        else
        {
            Assert.Null(resolution.AppliedPort);
            Assert.Empty(resolution.Args);
            Assert.Null(resolution.SeedPath);
        }
    }

    [Fact]
    public void Resolve_UsesSamePort_ForDifferentDirectoriesInsideTheSameRepository()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var appBaseDirectory = environment.CreateApplicationBaseDirectory("workspace");
        var nestedDirectory = Path.Combine(environment.WorkspaceRoot, "Web", "Docs");
        Directory.CreateDirectory(nestedDirectory);

        var rootResolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            environment.WorkspaceRoot,
            appBaseDirectory,
            ReadDevelopmentEnvironment);
        var nestedResolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            nestedDirectory,
            appBaseDirectory,
            ReadDevelopmentEnvironment);

        Assert.Equal(rootResolution.AppliedPort, nestedResolution.AppliedPort);
        Assert.Equal(rootResolution.SeedPath, nestedResolution.SeedPath);
    }

    [Fact]
    public void Resolve_UsesRepositoryRootSeed_ForDifferentRepositoryRoots()
    {
        using var first = new TemporaryEnvironment();
        using var second = new TemporaryEnvironment();
        first.CreateGitRepo("saturn");
        second.CreateGitRepo("jupiter");

        var firstResolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            first.WorkspaceRoot,
            first.CreateApplicationBaseDirectory("saturn"),
            ReadDevelopmentEnvironment);
        var secondResolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            second.WorkspaceRoot,
            second.CreateApplicationBaseDirectory("jupiter"),
            ReadDevelopmentEnvironment);

        Assert.Equal(first.WorkspaceRoot, firstResolution.SeedPath);
        Assert.Equal(second.WorkspaceRoot, secondResolution.SeedPath);
        Assert.NotEqual(firstResolution.SeedPath, secondResolution.SeedPath);
        Assert.NotNull(firstResolution.AppliedPort);
        Assert.NotNull(secondResolution.AppliedPort);
    }

    [Fact]
    public void Resolve_UsesRepositoryRootSeed_WhenGitMarkerIsDirectory()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitDirectoryRepo("workspace");
        var appBaseDirectory = environment.CreateApplicationBaseDirectory("workspace");

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            environment.WorkspaceRoot,
            appBaseDirectory,
            ReadDevelopmentEnvironment);

        Assert.Equal(environment.WorkspaceRoot, resolution.SeedPath);
        Assert.NotNull(resolution.AppliedPort);
    }

    [Fact]
    public void Resolve_DoesNotOverrideExplicitPortArgument()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var args = new[] { "--port", "5005" };

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            args,
            environment.WorkspaceRoot,
            environment.CreateApplicationBaseDirectory("workspace"),
            ReadDevelopmentEnvironment);

        Assert.Null(resolution.AppliedPort);
        Assert.Same(args, resolution.Args);
    }

    [Fact]
    public void Resolve_DoesNotOverrideExplicitPortArgument_WhenSpecifiedInline()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var args = new[] { "--port=5005" };

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            args,
            environment.WorkspaceRoot,
            environment.CreateApplicationBaseDirectory("workspace"),
            ReadDevelopmentEnvironment);

        Assert.Null(resolution.AppliedPort);
        Assert.Same(args, resolution.Args);
    }

    [Fact]
    public void Resolve_DoesNotOverrideExplicitUrlsArgument()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var args = new[] { "--urls", "http://127.0.0.1:5005" };

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            args,
            environment.WorkspaceRoot,
            environment.CreateApplicationBaseDirectory("workspace"),
            ReadDevelopmentEnvironment);

        Assert.Null(resolution.AppliedPort);
        Assert.Same(args, resolution.Args);
    }

    [Fact]
    public void Resolve_DoesNotOverrideExplicitUrlsArgument_WhenSpecifiedInline()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var args = new[] { "--urls=http://127.0.0.1:5005" };

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            args,
            environment.WorkspaceRoot,
            environment.CreateApplicationBaseDirectory("workspace"),
            ReadDevelopmentEnvironment);

        Assert.Null(resolution.AppliedPort);
        Assert.Same(args, resolution.Args);
    }

    [Theory]
    [InlineData("http_ports")]
    [InlineData("https_ports")]
    public void Resolve_DoesNotOverrideEndpointCommandLineConfiguration(string key)
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var args = new[] { $"--{key}", "5005" };

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            args,
            environment.WorkspaceRoot,
            environment.CreateApplicationBaseDirectory("workspace"),
            ReadDevelopmentEnvironment);

        Assert.Null(resolution.AppliedPort);
        Assert.Same(args, resolution.Args);
    }

    [Theory]
    [InlineData("http_ports")]
    [InlineData("https_ports")]
    public void Resolve_DoesNotOverrideEndpointCommandLineConfiguration_WhenSpecifiedInline(string key)
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var args = new[] { $"--{key}=5005" };

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            args,
            environment.WorkspaceRoot,
            environment.CreateApplicationBaseDirectory("workspace"),
            ReadDevelopmentEnvironment);

        Assert.Null(resolution.AppliedPort);
        Assert.Same(args, resolution.Args);
    }

    [Theory]
    [InlineData("ASPNETCORE_URLS")]
    [InlineData("URLS")]
    [InlineData("ASPNETCORE_HTTP_PORTS")]
    [InlineData("DOTNET_HTTP_PORTS")]
    [InlineData("HTTP_PORTS")]
    [InlineData("ASPNETCORE_HTTPS_PORTS")]
    [InlineData("DOTNET_HTTPS_PORTS")]
    [InlineData("HTTPS_PORTS")]
    public void Resolve_DoesNotOverrideEndpointEnvironmentVariable(string variableName)
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            environment.WorkspaceRoot,
            environment.CreateApplicationBaseDirectory("workspace"),
            key => key switch
            {
                "ASPNETCORE_ENVIRONMENT" => Environments.Development,
                _ when key == variableName => variableName.Contains("PORTS", StringComparison.OrdinalIgnoreCase)
                    ? "5005"
                    : "http://127.0.0.1:5005",
                _ => null
            });

        Assert.Null(resolution.AppliedPort);
        Assert.Empty(resolution.Args);
    }

    [Fact]
    public void Resolve_DoesNotOverrideNamedKestrelEndpointEnvironmentVariable()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        const string endpointVariableName = "Kestrel__Endpoints__Https__Url";

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            environment.WorkspaceRoot,
            environment.CreateApplicationBaseDirectory("workspace"),
            key => key switch
            {
                "ASPNETCORE_ENVIRONMENT" => Environments.Development,
                endpointVariableName => "https://localhost:5006",
                _ => null
            },
            [endpointVariableName]);

        Assert.Null(resolution.AppliedPort);
        Assert.Empty(resolution.Args);
    }

    [Fact]
    public void Resolve_AppendsDeterministicPort_WhenKestrelEndpointEnvironmentVariablesDoNotConfigureUrl()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        const string emptyEndpointVariableName = "Kestrel__Endpoints__Http__Url";

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            environment.WorkspaceRoot,
            environment.CreateApplicationBaseDirectory("workspace"),
            key => key switch
            {
                "ASPNETCORE_ENVIRONMENT" => Environments.Development,
                emptyEndpointVariableName => " ",
                _ => null
            },
            [
                "Other__Endpoints__Http__Url",
                "Kestrel__Endpoints__Http__Port",
                emptyEndpointVariableName
            ]);

        Assert.NotNull(resolution.AppliedPort);
        Assert.Equal(["--urls", $"http://localhost:{resolution.AppliedPort.Value}"], resolution.Args);
    }

    [Theory]
    [InlineData("urls", "http://127.0.0.1:5005")]
    [InlineData("http_ports", "5005")]
    [InlineData("https_ports", "5006")]
    public void Resolve_DoesNotOverrideEndpointConfigurationInAppSettings(
        string key,
        string value)
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var appBaseDirectory = environment.CreateApplicationBaseDirectory("workspace");
        environment.WriteAppSettings($$"""
            {
              "{{key}}": "{{value}}"
            }
            """);

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            environment.WorkspaceRoot,
            appBaseDirectory,
            ReadDevelopmentEnvironment);

        Assert.Null(resolution.AppliedPort);
        Assert.Empty(resolution.Args);
    }

    [Fact]
    public void Resolve_DoesNotOverrideEndpointConfigurationInEnvironmentAppSettings()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var appBaseDirectory = environment.CreateApplicationBaseDirectory("workspace");
        environment.WriteAppSettings(
            Environments.Development,
            """
            {
              "urls": "http://127.0.0.1:5005"
            }
            """);

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            environment.WorkspaceRoot,
            appBaseDirectory,
            ReadDevelopmentEnvironment);

        Assert.Null(resolution.AppliedPort);
        Assert.Empty(resolution.Args);
    }

    [Fact]
    public void Resolve_DoesNotOverrideKestrelEndpointConfigurationInAppSettings()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var appBaseDirectory = environment.CreateApplicationBaseDirectory("workspace");
        environment.WriteAppSettings(
            """
            {
              "Kestrel": {
                "Endpoints": {
                  "Http": {
                    "Url": "http://localhost:5005"
                  }
                }
              }
            }
            """);

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            environment.WorkspaceRoot,
            appBaseDirectory,
            ReadDevelopmentEnvironment);

        Assert.Null(resolution.AppliedPort);
        Assert.Empty(resolution.Args);
    }

    [Fact]
    public void Resolve_AppendsDeterministicPort_WhenAppSettingsEndpointConfigurationIsWhitespace()
    {
        using var environment = new TemporaryEnvironment();
        environment.CreateGitRepo("workspace");
        var appBaseDirectory = environment.CreateApplicationBaseDirectory("workspace");
        environment.WriteAppSettings(
            """
            {
              "urls": " ",
              "http_ports": " ",
              "https_ports": " ",
              "Kestrel": {
                "Endpoints": {
                  "Http": {
                    "Url": " "
                  }
                }
              }
            }
            """);

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            environment.WorkspaceRoot,
            appBaseDirectory,
            ReadDevelopmentEnvironment);

        Assert.NotNull(resolution.AppliedPort);
        Assert.Equal(["--urls", $"http://localhost:{resolution.AppliedPort.Value}"], resolution.Args);
    }

    [Fact]
    public void Resolve_FallsBackToProjectRoot_WhenNoRepositoryRootExists()
    {
        using var environment = new TemporaryEnvironment();
        var appBaseDirectory = environment.CreateApplicationBaseDirectory("standalone");
        var workingDirectory = Path.Combine(environment.RootDirectory, "work");
        Directory.CreateDirectory(workingDirectory);

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            workingDirectory,
            appBaseDirectory,
            ReadDevelopmentEnvironment);

        Assert.Equal(environment.ProjectRoot, resolution.SeedPath);
        Assert.NotNull(resolution.AppliedPort);
    }

    [Fact]
    public void Resolve_FallsBackToCurrentDirectory_WhenNoRepositoryOrProjectRootExists()
    {
        using var environment = new TemporaryEnvironment();
        var workingDirectory = Path.Combine(environment.RootDirectory, "work");
        var appBaseDirectory = Path.Combine(environment.RootDirectory, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(appBaseDirectory);

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [],
            workingDirectory,
            appBaseDirectory,
            ReadDevelopmentEnvironment);

        Assert.Equal(NormalizePathForAssertion(workingDirectory), resolution.SeedPath);
        Assert.NotNull(resolution.AppliedPort);
    }

    private static string? ReadDevelopmentEnvironment(string key)
    {
        return key == "ASPNETCORE_ENVIRONMENT" ? Environments.Development : null;
    }

    private static string NormalizePathForAssertion(string path)
    {
        var normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        return normalized.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private sealed class TemporaryEnvironment : IDisposable
    {
        public TemporaryEnvironment()
        {
            RootDirectory = NormalizePathForAssertion(
                Path.Combine(
                    Path.GetTempPath(),
                    "appsurface-web-port-defaults-tests",
                    Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(RootDirectory);
        }

        public string RootDirectory { get; }

        public string WorkspaceRoot { get; private set; } = string.Empty;

        public string ProjectRoot { get; private set; } = string.Empty;

        public void CreateGitRepo(string workspaceName)
        {
            WorkspaceRoot = NormalizePathForAssertion(Path.Combine(RootDirectory, workspaceName));
            Directory.CreateDirectory(WorkspaceRoot);
            File.WriteAllText(Path.Combine(WorkspaceRoot, ".git"), "gitdir: test");
        }

        public void CreateGitDirectoryRepo(string workspaceName)
        {
            WorkspaceRoot = NormalizePathForAssertion(Path.Combine(RootDirectory, workspaceName));
            Directory.CreateDirectory(Path.Combine(WorkspaceRoot, ".git"));
        }

        public string CreateApplicationBaseDirectory(string projectName)
        {
            var containerRoot = string.IsNullOrEmpty(WorkspaceRoot) ? RootDirectory : WorkspaceRoot;
            ProjectRoot = NormalizePathForAssertion(Path.Combine(containerRoot, projectName));
            Directory.CreateDirectory(ProjectRoot);
            File.WriteAllText(Path.Combine(ProjectRoot, $"{projectName}.csproj"), "<Project />");

            var baseDirectory = Path.Combine(ProjectRoot, "bin", "Debug", "net10.0");
            Directory.CreateDirectory(baseDirectory);
            return baseDirectory;
        }

        public void WriteAppSettings(string content)
        {
            File.WriteAllText(Path.Combine(WorkspaceRoot, "appsettings.json"), content);
        }

        public void WriteAppSettings(string environmentName, string content)
        {
            File.WriteAllText(Path.Combine(WorkspaceRoot, $"appsettings.{environmentName}.json"), content);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
    }
}
