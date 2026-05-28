using System.Runtime.InteropServices;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using ForgeTrust.AppSurface.Web.Tailwind.Tasks;
using Microsoft.Build.Framework;

namespace ForgeTrust.AppSurface.Web.Tailwind.Tests;

public sealed class TailwindBuildTargetsTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"{nameof(TailwindBuildTargetsTests)}_{Guid.NewGuid():N}");

    public TailwindBuildTargetsTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunTailwindBuild_Fails_WhenInputAndOutputResolveToSameFile()
    {
        var projectDirectory = Path.Combine(_tempRoot, "sample-app");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "tools"));

        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var markerPath = Path.Combine(projectDirectory, "tailwind-cli-executed.marker");
        var cliRelativePath = await CreateTailwindCliStubAsync(projectDirectory, markerPath);
        var projectPath = Path.Combine(projectDirectory, "Sample.csproj");
        var targetsPath = GetTailwindTargetsPath();

        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <TailwindInputPath>wwwroot/css/app.css</TailwindInputPath>
                <TailwindOutputPath>./wwwroot/css/../css/app.css</TailwindOutputPath>
                <TailwindCliPath>{{cliRelativePath}}</TailwindCliPath>
              </PropertyGroup>

              <Import Project="{{EscapeForXml(targetsPath)}}" />
            </Project>
            """);

        var result = await RunDotNetBuildAsync(projectPath, projectDirectory);
        var combinedOutput = result.Stdout + Environment.NewLine + result.Stderr;

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "TailwindInputPath and TailwindOutputPath must point to different files.",
            combinedOutput,
            StringComparison.Ordinal);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task RunTailwindBuild_IncludesGeneratedCssInStaticWebAssetsManifest_OnCleanBuild()
    {
        var projectDirectory = Path.Combine(_tempRoot, "sample-rcl");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "tools"));

        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var markerPath = Path.Combine(projectDirectory, "tailwind-cli-executed.marker");
        var cliRelativePath = await CreateTailwindCliStubAsync(projectDirectory, markerPath);
        var projectPath = Path.Combine(projectDirectory, "Sample.csproj");
        var targetsPath = GetTailwindTargetsPath();

        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk.Razor">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <StaticWebAssetBasePath>_content/Sample</StaticWebAssetBasePath>
                <TailwindInputPath>wwwroot/css/app.css</TailwindInputPath>
                <TailwindOutputPath>wwwroot/css/site.gen.css</TailwindOutputPath>
                <TailwindCliPath>{{cliRelativePath}}</TailwindCliPath>
              </PropertyGroup>

              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
              </ItemGroup>

              <Import Project="{{EscapeForXml(targetsPath)}}" />
            </Project>
            """);

        var result = await RunDotNetBuildAsync(projectPath, projectDirectory);
        var combinedOutput = result.Stdout + Environment.NewLine + result.Stderr;

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(markerPath), combinedOutput);

        var generatedCssPath = Path.Combine(projectDirectory, "wwwroot", "css", "site.gen.css");
        Assert.True(File.Exists(generatedCssPath), "Expected the Tailwind stub to emit the generated stylesheet.");

        var manifestPath = Path.Combine(projectDirectory, "obj", "Debug", "net10.0", "staticwebassets.build.json");
        Assert.True(File.Exists(manifestPath), "Expected a static web assets build manifest.");

        await AssertBuildManifestContainsGeneratedCssAsync(manifestPath);
    }

    [Fact]
    public async Task RunTailwindBuild_PreservesGeneratedCssInStaticWebAssetsManifest_WhenDefaultContentItemsAreDisabled()
    {
        var projectDirectory = Path.Combine(_tempRoot, "sample-rcl-no-default-content");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "tools"));

        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var markerPath = Path.Combine(projectDirectory, "tailwind-cli-executed.marker");
        var cliRelativePath = await CreateTailwindCliStubAsync(projectDirectory, markerPath);
        var projectPath = Path.Combine(projectDirectory, "Sample.csproj");
        var targetsPath = GetTailwindTargetsPath();

        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk.Razor">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <EnableDefaultContentItems>false</EnableDefaultContentItems>
                <StaticWebAssetBasePath>_content/Sample</StaticWebAssetBasePath>
                <TailwindInputPath>wwwroot/css/app.css</TailwindInputPath>
                <TailwindOutputPath>wwwroot/css/site.gen.css</TailwindOutputPath>
                <TailwindCliPath>{{cliRelativePath}}</TailwindCliPath>
              </PropertyGroup>

              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
              </ItemGroup>

              <Import Project="{{EscapeForXml(targetsPath)}}" />
            </Project>
            """);

        var firstBuildResult = await RunDotNetBuildAsync(projectPath, projectDirectory);
        var firstBuildOutput = firstBuildResult.Stdout + Environment.NewLine + firstBuildResult.Stderr;

        Assert.Equal(0, firstBuildResult.ExitCode);
        Assert.True(File.Exists(markerPath), firstBuildOutput);

        var manifestPath = Path.Combine(projectDirectory, "obj", "Debug", "net10.0", "staticwebassets.build.json");
        Assert.True(File.Exists(manifestPath), "Expected a static web assets build manifest after the first build.");
        await AssertBuildManifestContainsGeneratedCssAsync(manifestPath);

        var secondBuildResult = await RunDotNetBuildAsync(projectPath, projectDirectory);
        var secondBuildOutput = secondBuildResult.Stdout + Environment.NewLine + secondBuildResult.Stderr;

        Assert.Equal(0, secondBuildResult.ExitCode);
        Assert.True(File.Exists(manifestPath), "Expected a static web assets build manifest after the second build.");
        await AssertBuildManifestContainsGeneratedCssAsync(manifestPath);

        Assert.DoesNotContain(
            "Duplicate 'Content' items were included.",
            secondBuildOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTailwindBuild_FailsWithDiagnostic_WhenExplicitCliPathIsMissing()
    {
        var projectDirectory = Path.Combine(_tempRoot, "sample-missing-cli");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "wwwroot", "css"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var projectPath = Path.Combine(projectDirectory, "Sample.csproj");
        var targetsPath = await CreatePackagedTargetsCopyAsync(projectDirectory);

        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <TailwindInputPath>wwwroot/css/app.css</TailwindInputPath>
                <TailwindOutputPath>wwwroot/css/site.gen.css</TailwindOutputPath>
                <TailwindCliPath>tools/missing-tailwind</TailwindCliPath>
              </PropertyGroup>

              <Import Project="{{EscapeForXml(targetsPath)}}" />
            </Project>
            """);

        var result = await RunDotNetBuildAsync(projectPath, projectDirectory);
        var combinedOutput = result.Stdout + Environment.NewLine + result.Stderr;

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ASTW003", combinedOutput, StringComparison.Ordinal);
        Assert.Contains("TailwindCliPath", combinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTailwindBuild_DoesNotUsePathFallback_WhenBuildCliIsMissing()
    {
        var projectDirectory = Path.Combine(_tempRoot, "sample-no-path-fallback");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "path-bin"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var markerPath = Path.Combine(projectDirectory, "path-cli-executed.marker");
        await CreateTailwindCliStubAsync(projectDirectory, markerPath, toolDirectoryName: "path-bin");
        var projectPath = Path.Combine(projectDirectory, "Sample.csproj");
        var targetsPath = await CreatePackagedTargetsCopyAsync(projectDirectory);

        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <TailwindInputPath>wwwroot/css/app.css</TailwindInputPath>
                <TailwindOutputPath>wwwroot/css/site.gen.css</TailwindOutputPath>
              </PropertyGroup>

              <Import Project="{{EscapeForXml(targetsPath)}}" />
            </Project>
            """);

        var result = await RunDotNetBuildAsync(
            projectPath,
            projectDirectory,
            ("PATH", Path.Combine(projectDirectory, "path-bin")));
        var combinedOutput = result.Stdout + Environment.NewLine + result.Stderr;

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ASTW004", combinedOutput, StringComparison.Ordinal);
        Assert.Contains("Build mode does not search PATH", combinedOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task RunTailwindBuild_Succeeds_WhenCliWritesUnknownStderrAndExitsZero()
    {
        var projectDirectory = Path.Combine(_tempRoot, "sample-stderr-zero");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "tools"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var markerPath = Path.Combine(projectDirectory, "tailwind-cli-executed.marker");
        var cliRelativePath = await CreateTailwindCliStubAsync(
            projectDirectory,
            markerPath,
            stderrLine: "tailwind warning on stderr",
            exitCode: 0);
        var projectPath = Path.Combine(projectDirectory, "Sample.csproj");
        var targetsPath = await CreatePackagedTargetsCopyAsync(projectDirectory);

        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <TailwindInputPath>wwwroot/css/app.css</TailwindInputPath>
                <TailwindOutputPath>wwwroot/css/site.gen.css</TailwindOutputPath>
                <TailwindCliPath>{{cliRelativePath}}</TailwindCliPath>
              </PropertyGroup>

              <Import Project="{{EscapeForXml(targetsPath)}}" />
            </Project>
            """);

        var result = await RunDotNetBuildAsync(projectPath, projectDirectory);
        var combinedOutput = result.Stdout + Environment.NewLine + result.Stderr;

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(markerPath), combinedOutput);
        Assert.Contains("tailwind warning on stderr", combinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("ASTW006", combinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTailwindBuild_FailsWithDiagnostic_WhenCliExitsNonZero()
    {
        var projectDirectory = Path.Combine(_tempRoot, "sample-nonzero");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "tools"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var markerPath = Path.Combine(projectDirectory, "tailwind-cli-executed.marker");
        var cliRelativePath = await CreateTailwindCliStubAsync(
            projectDirectory,
            markerPath,
            stderrLine: "tailwind failed",
            exitCode: 7);
        var projectPath = Path.Combine(projectDirectory, "Sample.csproj");
        var targetsPath = await CreatePackagedTargetsCopyAsync(projectDirectory);

        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <TailwindInputPath>wwwroot/css/app.css</TailwindInputPath>
                <TailwindOutputPath>wwwroot/css/site.gen.css</TailwindOutputPath>
                <TailwindCliPath>{{cliRelativePath}}</TailwindCliPath>
              </PropertyGroup>

              <Import Project="{{EscapeForXml(targetsPath)}}" />
            </Project>
            """);

        var result = await RunDotNetBuildAsync(projectPath, projectDirectory);
        var combinedOutput = result.Stdout + Environment.NewLine + result.Stderr;

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(File.Exists(markerPath), combinedOutput);
        Assert.Contains("ASTW006", combinedOutput, StringComparison.Ordinal);
        Assert.Contains("tailwind failed", combinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTailwindBuildTask_Cancel_StopsLongRunningTailwindProcess()
    {
        var projectDirectory = Path.Combine(_tempRoot, "sample-cancel");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "tools"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var startedPath = Path.Combine(projectDirectory, "started.marker");
        var finishedPath = Path.Combine(projectDirectory, "finished.marker");
        var cliPath = await CreateLongRunningTailwindCliStubAsync(projectDirectory, startedPath, finishedPath);
        var buildEngine = new RecordingBuildEngine();
        var task = new RunTailwindBuildTask
        {
            BuildEngine = buildEngine,
            ProjectDirectory = projectDirectory,
            InputPath = "wwwroot/css/app.css",
            OutputPath = "wwwroot/css/site.gen.css",
            TailwindCliPath = cliPath,
            TailwindVersion = "4.1.18",
            TargetsDirectory = projectDirectory,
            Configuration = "Debug",
            TargetFramework = "net10.0"
        };

        var executeTask = Task.Run(task.Execute);
        await WaitForFileAsync(startedPath);
        task.Cancel();
        var result = await executeTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result);
        Assert.False(File.Exists(finishedPath));
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW007", StringComparison.Ordinal) == true);
        Assert.Empty(buildEngine.Warnings);
    }

    [Fact]
    public async Task PackedPackageConsumption_BuildAndPublish_LoadsTaskAndGeneratesStaticWebAsset()
    {
        var feedDirectory = Path.Combine(_tempRoot, "feed");
        Directory.CreateDirectory(feedDirectory);
        var repoRoot = GetRepositoryRoot();
        var packageVersion = CreateSmokePackageVersion();

        foreach (var packageProjectPath in GetPackageProjectsForSmoke(repoRoot))
        {
            var packResult = await RunDotNetAsync(
                ["pack", packageProjectPath, "-c", "Release", "--no-restore", "-o", feedDirectory, "-v:minimal", $"-p:Version={packageVersion}", $"-p:PackageVersion={packageVersion}"],
                repoRoot);
            Assert.Equal(0, packResult.ExitCode);
        }

        var tailwindPackagePath = Path.Combine(feedDirectory, $"ForgeTrust.AppSurface.Web.Tailwind.{packageVersion}.nupkg");
        Assert.True(File.Exists(tailwindPackagePath), "Expected the Tailwind package to be created.");
        using (var archive = System.IO.Compression.ZipFile.OpenRead(tailwindPackagePath))
        {
            Assert.Contains(archive.Entries, entry => entry.FullName == "build/tasks/ForgeTrust.AppSurface.Web.Tailwind.Tasks.dll");
            Assert.Contains(archive.Entries, entry => entry.FullName == "build/tasks/CliWrap.dll");
            Assert.Contains(archive.Entries, entry => entry.FullName == "build/ForgeTrust.AppSurface.Web.Tailwind.targets");
        }

        var projectDirectory = Path.Combine(_tempRoot, "packed-consumer");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "tools"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var markerPath = Path.Combine(projectDirectory, "tailwind-cli-executed.marker");
        var cliRelativePath = await CreateTailwindCliStubAsync(projectDirectory, markerPath);
        var projectPath = Path.Combine(projectDirectory, "PackedConsumer.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk.Razor">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <RestoreSources>{{EscapeForXml(feedDirectory)}};$(RestoreSources)</RestoreSources>
                <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
                <StaticWebAssetBasePath>_content/PackedConsumer</StaticWebAssetBasePath>
                <TailwindInputPath>wwwroot/css/app.css</TailwindInputPath>
                <TailwindOutputPath>wwwroot/css/site.gen.css</TailwindOutputPath>
                <TailwindCliPath>{{cliRelativePath}}</TailwindCliPath>
              </PropertyGroup>

              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
                <PackageReference Include="ForgeTrust.AppSurface.Web.Tailwind" Version="{{packageVersion}}" />
              </ItemGroup>
            </Project>
            """);

        var buildResult = await RunDotNetAsync(["build", projectPath, "-nologo", "-v:minimal"], projectDirectory);
        var buildOutput = buildResult.Stdout + Environment.NewLine + buildResult.Stderr;
        Assert.Equal(0, buildResult.ExitCode);
        Assert.True(File.Exists(markerPath), buildOutput);
        Assert.Contains("Tailwind CSS: Running build", buildOutput, StringComparison.Ordinal);

        var manifestPath = Path.Combine(projectDirectory, "obj", "Debug", "net10.0", "staticwebassets.build.json");
        Assert.True(File.Exists(manifestPath), "Expected a static web assets build manifest.");
        await AssertBuildManifestContainsGeneratedCssAsync(manifestPath);

        var publishResult = await RunDotNetAsync(["publish", projectPath, "-nologo", "-v:minimal", "--no-restore"], projectDirectory);
        var publishOutput = publishResult.Stdout + Environment.NewLine + publishResult.Stderr;
        Assert.Equal(0, publishResult.ExitCode);
        Assert.True(
            File.Exists(Path.Combine(projectDirectory, "bin", "Release", "net10.0", "publish", "wwwroot", "css", "site.gen.css")) ||
            File.Exists(Path.Combine(projectDirectory, "bin", "Debug", "net10.0", "publish", "wwwroot", "css", "site.gen.css")),
            publishOutput);
    }

    [Fact]
    public async Task PackedPackageConsumption_DefaultRuntimePackageBuild_LoadsRuntimeCliWithoutExplicitPath()
    {
        var feedDirectory = Path.Combine(_tempRoot, "default-runtime-feed");
        Directory.CreateDirectory(feedDirectory);
        var repoRoot = GetRepositoryRoot();
        var packageVersion = CreateSmokePackageVersion();

        foreach (var packageProjectPath in GetPackageProjectsForSmoke(repoRoot))
        {
            var packResult = await RunDotNetAsync(
                ["pack", packageProjectPath, "-c", "Release", "--no-restore", "-o", feedDirectory, "-v:minimal", $"-p:Version={packageVersion}", $"-p:PackageVersion={packageVersion}"],
                repoRoot);
            Assert.Equal(0, packResult.ExitCode);
        }

        var projectDirectory = Path.Combine(_tempRoot, "packed-default-runtime-consumer");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "wwwroot", "css"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var projectPath = Path.Combine(projectDirectory, "PackedDefaultRuntimeConsumer.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk.Razor">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <RestoreSources>{{EscapeForXml(feedDirectory)}};$(RestoreSources)</RestoreSources>
                <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
                <StaticWebAssetBasePath>_content/PackedDefaultRuntimeConsumer</StaticWebAssetBasePath>
                <TailwindInputPath>wwwroot/css/app.css</TailwindInputPath>
                <TailwindOutputPath>wwwroot/css/site.gen.css</TailwindOutputPath>
              </PropertyGroup>

              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
                <PackageReference Include="ForgeTrust.AppSurface.Web.Tailwind" Version="{{packageVersion}}" />
              </ItemGroup>
            </Project>
            """);

        var buildResult = await RunDotNetAsync(["build", projectPath, "-nologo", "-v:minimal"], projectDirectory);
        var buildOutput = buildResult.Stdout + Environment.NewLine + buildResult.Stderr;

        Assert.True(buildResult.ExitCode == 0, buildOutput);
        Assert.Contains("Tailwind CSS: Running build", buildOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("ASTW004", buildOutput, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(projectDirectory, "wwwroot", "css", "site.gen.css")), buildOutput);
    }

    private static string GetTailwindTargetsPath()
    {
        return Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "Web",
                "ForgeTrust.AppSurface.Web.Tailwind",
                "build",
                "ForgeTrust.AppSurface.Web.Tailwind.targets"));
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                ".."));
    }

    private static IEnumerable<string> GetPackageProjectsForSmoke(string repoRoot)
    {
        yield return Path.Combine(repoRoot, "ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Core.csproj");
        yield return Path.Combine(repoRoot, "Web", "ForgeTrust.AppSurface.Web.Tailwind", "runtimes", "ForgeTrust.AppSurface.Web.Tailwind.Runtime.win-x64.csproj");
        yield return Path.Combine(repoRoot, "Web", "ForgeTrust.AppSurface.Web.Tailwind", "runtimes", "ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj");
        yield return Path.Combine(repoRoot, "Web", "ForgeTrust.AppSurface.Web.Tailwind", "runtimes", "ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-x64.csproj");
        yield return Path.Combine(repoRoot, "Web", "ForgeTrust.AppSurface.Web.Tailwind", "runtimes", "ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-arm64.csproj");
        yield return Path.Combine(repoRoot, "Web", "ForgeTrust.AppSurface.Web.Tailwind", "runtimes", "ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64.csproj");
        yield return Path.Combine(repoRoot, "Web", "ForgeTrust.AppSurface.Web.Tailwind", "ForgeTrust.AppSurface.Web.Tailwind.csproj");
    }

    private static string CreateSmokePackageVersion()
    {
        return "0.1.0-smoke." + Guid.NewGuid().ToString("N");
    }

    private static async Task<string> CreatePackagedTargetsCopyAsync(string projectDirectory)
    {
        var packageBuildDirectory = Path.Combine(projectDirectory, "package", "build");
        var taskDirectory = Path.Combine(packageBuildDirectory, "tasks");
        Directory.CreateDirectory(taskDirectory);

        var targetsPath = Path.Combine(packageBuildDirectory, "ForgeTrust.AppSurface.Web.Tailwind.targets");
        File.Copy(GetTailwindTargetsPath(), targetsPath);
        File.Copy(
            Path.Combine(Path.GetDirectoryName(GetTailwindTargetsPath())!, "..", "tailwind.version"),
            Path.Combine(packageBuildDirectory, "tailwind.version"));

        var taskAssemblyDirectory = Path.GetDirectoryName(typeof(RunTailwindBuildTask).Assembly.Location)!;
        foreach (var file in Directory.EnumerateFiles(taskAssemblyDirectory, "*.dll"))
        {
            File.Copy(file, Path.Combine(taskDirectory, Path.GetFileName(file)));
        }

        foreach (var file in Directory.EnumerateFiles(taskAssemblyDirectory, "*.deps.json"))
        {
            File.Copy(file, Path.Combine(taskDirectory, Path.GetFileName(file)));
        }

        await Task.CompletedTask;
        return targetsPath;
    }

    private static string EscapeForXml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private static async Task<string> CreateTailwindCliStubAsync(
        string projectDirectory,
        string markerPath,
        string outputRelativePath = "wwwroot/css/site.gen.css",
        string toolDirectoryName = "tools",
        string? stderrLine = null,
        int exitCode = 0)
    {
        var toolsDirectory = Path.Combine(projectDirectory, toolDirectoryName);
        var outputPath = Path.Combine(projectDirectory, outputRelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var relativePath = Path.Combine(toolDirectoryName, "tailwindcss.cmd");
            var fullPath = Path.Combine(projectDirectory, relativePath);
            await File.WriteAllTextAsync(
                fullPath,
                $@"@echo off
if not exist ""{Path.GetDirectoryName(outputPath)}"" mkdir ""{Path.GetDirectoryName(outputPath)}""
>""{outputPath}"" echo .generated{{color:red;}}
echo invoked>""{markerPath}""
{(stderrLine == null ? string.Empty : $@"echo {stderrLine} 1>&2
")}exit /b {exitCode}
");

            return relativePath;
        }

        const UnixFileMode executableMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        var unixRelativePath = Path.Combine(toolDirectoryName, "tailwindcss");
        var unixFullPath = Path.Combine(projectDirectory, unixRelativePath);
        await File.WriteAllTextAsync(
            unixFullPath,
            $@"#!/bin/sh
mkdir -p ""{Path.GetDirectoryName(outputPath)}""
cat <<'EOF' > ""{outputPath}""
.generated{{color:red;}}
EOF
printf 'invoked\n' > ""{markerPath}""
{(stderrLine == null ? string.Empty : $"printf '{stderrLine}\\n' >&2")}
exit {exitCode}
");
        File.SetUnixFileMode(unixFullPath, executableMode);
        return unixRelativePath;
    }

    private static async Task<string> CreateLongRunningTailwindCliStubAsync(
        string projectDirectory,
        string startedPath,
        string finishedPath)
    {
        var toolsDirectory = Path.Combine(projectDirectory, "tools");
        Directory.CreateDirectory(toolsDirectory);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var relativePath = Path.Combine("tools", "tailwindcss.cmd");
            var fullPath = Path.Combine(projectDirectory, relativePath);
            await File.WriteAllTextAsync(
                fullPath,
                $@"@echo off
echo started>""{startedPath}""
ping 127.0.0.1 -n 30 > nul
echo finished>""{finishedPath}""
exit /b 0
");
            return relativePath;
        }

        const UnixFileMode executableMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        var unixRelativePath = Path.Combine("tools", "tailwindcss");
        var unixFullPath = Path.Combine(projectDirectory, unixRelativePath);
        await File.WriteAllTextAsync(
            unixFullPath,
            $@"#!/bin/sh
printf 'started\n' > ""{startedPath}""
sleep 30
printf 'finished\n' > ""{finishedPath}""
exit 0
");
        File.SetUnixFileMode(unixFullPath, executableMode);
        return unixRelativePath;
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(path))
        {
            await Task.Delay(50, cancellationTokenSource.Token);
        }
    }

    private static async Task<DotNetCommandResult> RunDotNetBuildAsync(
        string projectPath,
        string workingDirectory,
        params (string Name, string Value)[] environmentVariables)
    {
        return await RunDotNetAsync(["build", projectPath, "-nologo", "-v:minimal"], workingDirectory, environmentVariables);
    }

    private static async Task<DotNetCommandResult> RunDotNetAsync(
        IReadOnlyList<string> args,
        string workingDirectory,
        params (string Name, string Value)[] environmentVariables)
    {
        var command = Cli.Wrap("dotnet")
            .WithArguments(args)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None);

        var commandEnvironment = new Dictionary<string, string?>
        {
            ["MSBUILDDISABLENODEREUSE"] = "1"
        };

        foreach (var (name, value) in environmentVariables)
        {
            commandEnvironment[name] = value;
        }

        command = command.WithEnvironmentVariables(commandEnvironment);

        var result = await command.ExecuteBufferedAsync();

        return new DotNetCommandResult(
            result.ExitCode,
            result.StandardOutput,
            result.StandardError);
    }

    private static async Task AssertBuildManifestContainsGeneratedCssAsync(string manifestPath)
    {
        await using var manifestStream = File.OpenRead(manifestPath);
        using var document = await JsonDocument.ParseAsync(manifestStream);

        var assets = document.RootElement.GetProperty("Assets");
        Assert.Contains(
            assets.EnumerateArray(),
            asset => asset.TryGetProperty("RelativePath", out var relativePath)
                && relativePath.GetString() is string value
                && value.StartsWith("css/site.gen", StringComparison.Ordinal)
                && value.EndsWith(".css", StringComparison.Ordinal));
    }

    private sealed record DotNetCommandResult(int ExitCode, string Stdout, string Stderr);

    private sealed class RecordingBuildEngine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = [];

        public List<BuildWarningEventArgs> Warnings { get; } = [];

        public bool ContinueOnError => false;

        public int LineNumberOfTaskNode => 0;

        public int ColumnNumberOfTaskNode => 0;

        public string ProjectFileOfTaskNode => string.Empty;

        public void LogErrorEvent(BuildErrorEventArgs e)
        {
            Errors.Add(e);
        }

        public void LogWarningEvent(BuildWarningEventArgs e)
        {
            Warnings.Add(e);
        }

        public void LogMessageEvent(BuildMessageEventArgs e)
        {
        }

        public void LogCustomEvent(CustomBuildEventArgs e)
        {
        }

        public bool BuildProjectFile(
            string projectFileName,
            string[] targetNames,
            System.Collections.IDictionary globalProperties,
            System.Collections.IDictionary targetOutputs)
        {
            return true;
        }
    }
}
