using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CliWrap;
using CliWrap.Buffered;
using ForgeTrust.AppSurface.Web.Tailwind.Internal;
using ForgeTrust.AppSurface.Web.Tailwind.Tasks;
using Microsoft.Build.Framework;

namespace ForgeTrust.AppSurface.Web.Tailwind.Tests;

public sealed class TailwindBuildTargetsTests : IDisposable
{
#if DEBUG
    private const string CurrentConfiguration = "Debug";
#else
    private const string CurrentConfiguration = "Release";
#endif

    private readonly string _tempRoot = Path.Join(
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
        var projectDirectory = Path.Join(_tempRoot, "sample-app");
        Directory.CreateDirectory(Path.Join(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Join(projectDirectory, "tools"));

        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var markerPath = Path.Join(projectDirectory, "tailwind-cli-executed.marker");
        var cliRelativePath = await CreateTailwindCliStubAsync(projectDirectory, markerPath);
        var projectPath = Path.Join(projectDirectory, "Sample.csproj");
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

        var result = await RunDotNetBuildForCurrentConfigurationAsync(projectPath, projectDirectory);
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
        var projectDirectory = Path.Join(_tempRoot, "sample-rcl");
        Directory.CreateDirectory(Path.Join(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Join(projectDirectory, "tools"));

        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var markerPath = Path.Join(projectDirectory, "tailwind-cli-executed.marker");
        var cliRelativePath = await CreateTailwindCliStubAsync(projectDirectory, markerPath);
        var projectPath = Path.Join(projectDirectory, "Sample.csproj");
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

        var result = await RunDotNetBuildForCurrentConfigurationAsync(projectPath, projectDirectory);
        var combinedOutput = result.Stdout + Environment.NewLine + result.Stderr;

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(markerPath), combinedOutput);

        var generatedCssPath = Path.Join(projectDirectory, "wwwroot", "css", "site.gen.css");
        Assert.True(File.Exists(generatedCssPath), "Expected the Tailwind stub to emit the generated stylesheet.");

        var manifestPath = GetStaticWebAssetsBuildManifestPath(projectDirectory, combinedOutput);

        await AssertBuildManifestContainsGeneratedCssAsync(manifestPath);
    }

    [Fact]
    public async Task RunTailwindBuild_PreservesGeneratedCssInStaticWebAssetsManifest_WhenDefaultContentItemsAreDisabled()
    {
        var projectDirectory = Path.Join(_tempRoot, "sample-rcl-no-default-content");
        Directory.CreateDirectory(Path.Join(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Join(projectDirectory, "tools"));

        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var markerPath = Path.Join(projectDirectory, "tailwind-cli-executed.marker");
        var cliRelativePath = await CreateTailwindCliStubAsync(projectDirectory, markerPath);
        var projectPath = Path.Join(projectDirectory, "Sample.csproj");
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

        var firstBuildResult = await RunDotNetBuildForCurrentConfigurationAsync(projectPath, projectDirectory);
        var firstBuildOutput = firstBuildResult.Stdout + Environment.NewLine + firstBuildResult.Stderr;

        Assert.Equal(0, firstBuildResult.ExitCode);
        Assert.True(File.Exists(markerPath), firstBuildOutput);

        var manifestPath = GetStaticWebAssetsBuildManifestPath(projectDirectory, firstBuildOutput);
        await AssertBuildManifestContainsGeneratedCssAsync(manifestPath);

        var secondBuildResult = await RunDotNetBuildForCurrentConfigurationAsync(projectPath, projectDirectory);
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
        var projectDirectory = Path.Join(_tempRoot, "sample-missing-cli");
        Directory.CreateDirectory(Path.Join(projectDirectory, "wwwroot", "css"));
        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var projectPath = Path.Join(projectDirectory, "Sample.csproj");
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
    public void TailwindTargets_GatesUsingTaskOnExistingTaskAssembly()
    {
        var document = XDocument.Load(GetTailwindTargetsPath());
        var usingTask = Assert.Single(
            document.Descendants("UsingTask"),
            element =>
                string.Equals(
                    element.Attribute("TaskName")?.Value,
                    "ForgeTrust.AppSurface.Web.Tailwind.Tasks.RunTailwindBuildTask",
                    StringComparison.Ordinal));

        Assert.Equal(
            "'$(_TailwindTaskAssembly)' != '' and (Exists('$(_TailwindTaskAssembly)') or '$(_TailwindTaskAssembly)' == '$(_TailwindTaskAssemblySource)')",
            usingTask.Attribute("Condition")?.Value);
    }

    [Fact]
    public void TailwindTargets_RetainsSourceTaskAssemblyPathForBootstrap()
    {
        var document = XDocument.Load(GetTailwindTargetsPath());
        var sourceTaskAssembly = Assert.Single(
            document.Descendants("_TailwindTaskAssembly"),
            element => string.Equals(
                element.Value,
                "$(_TailwindTaskAssemblySource)",
                StringComparison.Ordinal));

        Assert.Equal(
            "'$(_TailwindTaskAssembly)' == '' and Exists('$(_TailwindTaskAssemblySourceProject)')",
            sourceTaskAssembly.Attribute("Condition")?.Value);
    }

    [Fact]
    public void TailwindTargets_BuildsSourceTaskAssemblyBeforeRunTailwindBuild()
    {
        var document = XDocument.Load(GetTailwindTargetsPath());
        var bootstrapTarget = Assert.Single(
            document.Descendants("Target"),
            element => string.Equals(
                element.Attribute("Name")?.Value,
                "BuildTailwindSourceTaskAssembly",
                StringComparison.Ordinal));
        var buildTask = Assert.Single(bootstrapTarget.Descendants("MSBuild"));

        Assert.Equal("RunTailwindBuild", bootstrapTarget.Attribute("BeforeTargets")?.Value);
        Assert.Equal("$(_TailwindTaskAssemblySourceProject)", buildTask.Attribute("Projects")?.Value);
        Assert.Equal("Build", buildTask.Attribute("Targets")?.Value);
        Assert.Contains(
            "!Exists('$(_TailwindTaskAssemblySource)')",
            bootstrapTarget.Attribute("Condition")?.Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TailwindTargets_PassesDownloadCacheRootToBuildTask()
    {
        var document = XDocument.Load(GetTailwindTargetsPath());
        var task = Assert.Single(
            document.Descendants("ForgeTrust.AppSurface.Web.Tailwind.Tasks.RunTailwindBuildTask"));

        Assert.Equal("$(TailwindDownloadCacheRoot)", task.Attribute("TailwindDownloadCacheRoot")?.Value);
    }

    [Fact]
    public void TailwindTargets_DefaultsDownloadCacheRootFromUserCacheEnvironment()
    {
        var document = XDocument.Load(GetTailwindTargetsPath());
        var defaultElements = document
            .Descendants("TailwindDownloadCacheRoot")
            .Where(element => element.Attribute("Condition")?.Value.Contains("$(_TailwindIsSourceTreeImport)", StringComparison.Ordinal) is true)
            .ToArray();
        var defaults = defaultElements
            .Select(element => element.Value)
            .ToArray();

        Assert.Contains("$(XDG_CACHE_HOME)/forgetrust/appsurface/tailwind", defaults);
        Assert.Contains("$(HOME)/.cache/forgetrust/appsurface/tailwind", defaults);
        Assert.All(
            defaultElements,
            element => Assert.Contains(
                "'$(_TailwindIsSourceTreeImport)' == 'true'",
                element.Attribute("Condition")?.Value,
                StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeTargets_NormalizesDownloadCacheRootWithMsbuildEnsureTrailingSlash()
    {
        var document = XDocument.Load(GetTailwindRuntimeTargetsPath());
        var normalizedRoot = Assert.Single(document.Descendants("_TailwindDownloadCacheRoot"));

        Assert.Equal("'$(TailwindDownloadCacheRoot)' != ''", normalizedRoot.Attribute("Condition")?.Value);
        Assert.Equal(
            "$([MSBuild]::EnsureTrailingSlash('$(TailwindDownloadCacheRoot)'))",
            normalizedRoot.Value);
    }

    [Fact]
    public void TailwindProject_KeepsRuntimeProjectReferencesUnconditional()
    {
        var document = XDocument.Load(GetTailwindProjectPath());
        var runtimeReferences = document
            .Descendants("ProjectReference")
            .Where(element => element.Attribute("Include")?.Value.Contains("/runtimes/", StringComparison.OrdinalIgnoreCase) is true ||
                element.Attribute("Include")?.Value.Contains("\\runtimes\\", StringComparison.OrdinalIgnoreCase) is true ||
                element.Attribute("Include")?.Value.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase) is true)
            .ToArray();

        Assert.Equal(5, runtimeReferences.Length);
        Assert.All(runtimeReferences, reference => Assert.Null(reference.Attribute("Condition")));
        Assert.DoesNotContain(
            "TailwindRuntimeBinaryResolutionEnabled",
            document.ToString(SaveOptions.DisableFormatting),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeTargets_DefaultsBinaryResolutionEnabledAndGatesOnlyResolveTarget()
    {
        var document = XDocument.Load(GetTailwindRuntimeTargetsPath());
        var defaultProperty = Assert.Single(
            document.Descendants("TailwindRuntimeBinaryResolutionEnabled"),
            element => element.Attribute("Condition")?.Value == "'$(TailwindRuntimeBinaryResolutionEnabled)' == ''");
        var resolveTarget = Assert.Single(
            document.Descendants("Target"),
            element => element.Attribute("Name")?.Value == "ResolveTailwindBinary");
        var packageTarget = Assert.Single(
            document.Descendants("Target"),
            element => element.Attribute("Name")?.Value == "AddTailwindRuntimeBinaryToPackage");

        Assert.Equal("true", defaultProperty.Value);
        Assert.Equal("'$(_TailwindRuntimeBinaryResolutionEnabledNormalized)' == 'true'", resolveTarget.Attribute("Condition")?.Value);
        Assert.Null(packageTarget.Attribute("Condition"));
        Assert.Contains(
            "ErrorIfTailwindRuntimePackageCreatedWithoutBinaryResolution",
            packageTarget.Attribute("DependsOnTargets")?.Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeBinaryResolution_FailsWithDiagnostic_WhenPropertyValueIsInvalid()
    {
        var projectDirectory = Path.Join(_tempRoot, "runtime-invalid-property");
        var projectPath = await CreateRuntimeProjectAsync(projectDirectory);

        var result = await RunDotNetBuildAsync(
            projectPath,
            projectDirectory,
            ("TailwindRuntimeBinaryResolutionEnabled", "treu"));
        var combinedOutput = result.Stdout + Environment.NewLine + result.Stderr;

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ASTW009", combinedOutput, StringComparison.Ordinal);
        Assert.Contains("TailwindRuntimeBinaryResolutionEnabled", combinedOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(GetRuntimeBinaryPath(projectDirectory, CurrentConfiguration)));
        Assert.False(File.Exists(Path.Join(Path.GetDirectoryName(GetRuntimeBinaryPath(projectDirectory, CurrentConfiguration))!, "sha256sums.txt")));
    }

    [Fact]
    public async Task RuntimeBinaryResolution_TreatsWhitespacePropertyValueAsUnset()
    {
        var projectDirectory = Path.Join(_tempRoot, "runtime-whitespace-property");
        var projectPath = await CreateRuntimeProjectAsync(projectDirectory);
        await WriteRuntimeBinaryCacheAsync(projectDirectory, CurrentConfiguration);

        var result = await RunDotNetBuildAsync(
            projectPath,
            projectDirectory,
            ("TailwindRuntimeBinaryResolutionEnabled", "   "));
        var combinedOutput = result.Stdout + Environment.NewLine + result.Stderr;

        Assert.True(result.ExitCode == 0, combinedOutput);
        Assert.DoesNotContain("ASTW009", combinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Tailwind runtime binary resolution is disabled for this build", combinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeBinaryResolution_SkipsDownloadWork_WhenDisabledForBuild()
    {
        var projectDirectory = Path.Join(_tempRoot, "runtime-disabled-build");
        var projectPath = await CreateRuntimeProjectAsync(projectDirectory);

        var result = await RunDotNetBuildAsync(
            projectPath,
            projectDirectory,
            ("TailwindRuntimeBinaryResolutionEnabled", "false"));
        var combinedOutput = result.Stdout + Environment.NewLine + result.Stderr;

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Tailwind runtime binary resolution is disabled for this build", combinedOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(GetRuntimeBinaryPath(projectDirectory, CurrentConfiguration)));
        Assert.False(File.Exists(Path.Join(Path.GetDirectoryName(GetRuntimeBinaryPath(projectDirectory, CurrentConfiguration))!, "sha256sums.txt")));
    }

    [Fact]
    public async Task RuntimePackageCreation_Fails_WhenBinaryResolutionIsDisabled()
    {
        var projectDirectory = Path.Join(_tempRoot, "runtime-disabled-pack");
        var projectPath = await CreateRuntimeProjectAsync(projectDirectory);
        var restoreResult = await RunDotNetAsync(["restore", projectPath, "-v:minimal"], projectDirectory);
        Assert.Equal(0, restoreResult.ExitCode);

        var result = await RunDotNetAsync(
            ["pack", projectPath, "--no-build", "-c", "Release", "-v:minimal", "-p:TailwindRuntimeBinaryResolutionEnabled=false"],
            projectDirectory);
        var combinedOutput = result.Stdout + Environment.NewLine + result.Stderr;

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ASTW010", combinedOutput, StringComparison.Ordinal);
        Assert.Contains("cannot be packed without its native Tailwind binary", combinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimePackageCreation_Fails_WhenBinaryResolutionIsDisabledEvenWithCachedBinary()
    {
        var projectDirectory = Path.Join(_tempRoot, "runtime-disabled-pack-cached");
        var projectPath = await CreateRuntimeProjectAsync(projectDirectory);
        var runtimeBinaryPath = GetRuntimeBinaryPath(projectDirectory, "Release");
        Directory.CreateDirectory(Path.GetDirectoryName(runtimeBinaryPath)!);
        await File.WriteAllTextAsync(runtimeBinaryPath, "cached", Encoding.UTF8);
        var restoreResult = await RunDotNetAsync(["restore", projectPath, "-v:minimal"], projectDirectory);
        Assert.Equal(0, restoreResult.ExitCode);

        var result = await RunDotNetAsync(
            ["pack", projectPath, "--no-build", "-c", "Release", "-v:minimal", "-p:TailwindRuntimeBinaryResolutionEnabled=false"],
            projectDirectory);
        var combinedOutput = result.Stdout + Environment.NewLine + result.Stderr;

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ASTW010", combinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimePackageCreation_WithNoBuild_IncludesRuntimeBinary_WhenBinaryResolutionIsEnabled()
    {
        var projectDirectory = Path.Join(_tempRoot, "runtime-enabled-pack-no-build");
        var projectPath = await CreateRuntimeProjectAsync(projectDirectory);
        var packageDirectory = Path.Join(projectDirectory, "packages");
        Directory.CreateDirectory(packageDirectory);
        await WriteRuntimeBinaryCacheAsync(projectDirectory, "Release");
        var restoreResult = await RunDotNetAsync(["restore", projectPath, "-v:minimal"], projectDirectory);
        Assert.Equal(0, restoreResult.ExitCode);

        var result = await RunDotNetAsync(
            [
                "pack",
                projectPath,
                "--no-build",
                "-c",
                "Release",
                "-v:minimal",
                "--output",
                packageDirectory,
                "-p:PackageVersion=0.0.0-test.1",
                "-p:TailwindRuntimeBinaryResolutionEnabled=true"
            ],
            projectDirectory);
        var combinedOutput = result.Stdout + Environment.NewLine + result.Stderr;

        Assert.Equal(0, result.ExitCode);
        var packagePath = Path.Join(packageDirectory, "ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64.0.0.0-test.1.nupkg");
        Assert.True(File.Exists(packagePath), combinedOutput);
        using var archive = System.IO.Compression.ZipFile.OpenRead(packagePath);
        Assert.Contains(
            archive.Entries,
            entry => entry.FullName == "runtimes/linux-x64/native/tailwindcss-linux-x64");
    }

    [Fact]
    public void RuntimeTargets_DefinesMissingPayloadDiagnostic()
    {
        var document = XDocument.Load(GetTailwindRuntimeTargetsPath());
        var packageTarget = Assert.Single(
            document.Descendants("Target"),
            element => element.Attribute("Name")?.Value == "AddTailwindRuntimeBinaryToPackage");
        var missingPayloadError = Assert.Single(
            packageTarget.Descendants("Error"),
            element => element.Attribute("Code")?.Value == "ASTW011");

        Assert.Contains("Resolved Tailwind runtime binary is missing", missingPayloadError.Attribute("Text")?.Value, StringComparison.Ordinal);
        Assert.Equal("!Exists('$(_TailwindBinaryPath)')", missingPayloadError.Attribute("Condition")?.Value);
    }

    [Fact]
    public async Task RunTailwindBuild_DoesNotUseImplicitUserCache_WhenPackagedRuntimeIsMissing()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var projectDirectory = Path.Join(_tempRoot, "sample-packaged-target-no-implicit-cache");
        Directory.CreateDirectory(Path.Join(projectDirectory, "wwwroot", "css"));
        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var rid = TailwindRuntimeMap.GetCurrentRid();
        var runtimeBinaryName = TailwindRuntimeMap.GetRuntimeBinaryName(rid)
            ?? throw new InvalidOperationException($"No Tailwind runtime binary name exists for '{rid}'.");
        var fakeHomeDirectory = Path.Join(projectDirectory, "fake-home");
        var fakeCacheRoot = Path.Join(fakeHomeDirectory, ".cache", "forgetrust", "appsurface", "tailwind");
        var ambientCacheRuntimePath = TailwindDownloadCache.GetRuntimeBinaryPath(fakeCacheRoot, "4.1.18", rid, runtimeBinaryName);
        await CreateRuntimeTailwindCliStubAsync(projectDirectory, ambientCacheRuntimePath);

        var projectPath = Path.Join(projectDirectory, "Sample.csproj");
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
            ("HOME", fakeHomeDirectory),
            ("XDG_CACHE_HOME", string.Empty),
            ("LOCALAPPDATA", string.Empty),
            ("USERPROFILE", string.Empty));
        var combinedOutput = result.Stdout + Environment.NewLine + result.Stderr;

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ASTW004", combinedOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(GetRuntimeSelectionMarkerPath(ambientCacheRuntimePath)));
    }

    [Fact]
    public async Task RunTailwindBuild_DoesNotUsePathFallback_WhenBuildCliIsMissing()
    {
        var projectDirectory = Path.Join(Path.GetFullPath(_tempRoot), "sample-no-path-fallback");
        Directory.CreateDirectory(Path.Join(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Join(projectDirectory, "path-bin"));
        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var markerPath = Path.Join(projectDirectory, "path-cli-executed.marker");
        await CreateTailwindCliStubAsync(projectDirectory, markerPath, toolDirectoryName: "path-bin");
        var projectPath = Path.Join(projectDirectory, "Sample.csproj");
        var targetsPath = await CreatePackagedTargetsCopyAsync(projectDirectory);

        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <TailwindInputPath>wwwroot/css/app.css</TailwindInputPath>
                <TailwindOutputPath>wwwroot/css/site.gen.css</TailwindOutputPath>
                <TailwindDownloadCacheRoot>{{EscapeForXml(Path.Join(projectDirectory, "empty-download-cache"))}}</TailwindDownloadCacheRoot>
              </PropertyGroup>

              <Import Project="{{EscapeForXml(targetsPath)}}" />
            </Project>
            """);

        var result = await RunDotNetBuildAsync(
            projectPath,
            projectDirectory,
            ("PATH", Path.Join(projectDirectory, "path-bin")));
        var combinedOutput = result.Stdout + Environment.NewLine + result.Stderr;

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ASTW004", combinedOutput, StringComparison.Ordinal);
        Assert.Contains("Build mode does not search PATH", combinedOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task RunTailwindBuild_Succeeds_WhenCliWritesUnknownStderrAndExitsZero()
    {
        var projectDirectory = Path.Join(_tempRoot, "sample-stderr-zero");
        Directory.CreateDirectory(Path.Join(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Join(projectDirectory, "tools"));
        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var markerPath = Path.Join(projectDirectory, "tailwind-cli-executed.marker");
        var cliRelativePath = await CreateTailwindCliStubAsync(
            projectDirectory,
            markerPath,
            stderrLine: "tailwind warning on stderr",
            exitCode: 0);
        var projectPath = Path.Join(projectDirectory, "Sample.csproj");
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
        var projectDirectory = Path.Join(_tempRoot, "sample-nonzero");
        Directory.CreateDirectory(Path.Join(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Join(projectDirectory, "tools"));
        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var markerPath = Path.Join(projectDirectory, "tailwind-cli-executed.marker");
        var cliRelativePath = await CreateTailwindCliStubAsync(
            projectDirectory,
            markerPath,
            stderrLine: "tailwind failed",
            exitCode: 7);
        var projectPath = Path.Join(projectDirectory, "Sample.csproj");
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
        var projectDirectory = Path.Join(_tempRoot, "sample-cancel");
        Directory.CreateDirectory(Path.Join(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Join(projectDirectory, "tools"));
        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var startedPath = Path.Join(projectDirectory, "started.marker");
        var finishedPath = Path.Join(projectDirectory, "finished.marker");
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
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW007", StringComparison.Ordinal) is true);
        Assert.Empty(buildEngine.Warnings);
    }

    [Fact]
    public void RunTailwindBuildTask_CancelBeforeExecute_DoesNotThrow()
    {
        var task = new RunTailwindBuildTask();

        task.Cancel();
    }

    [Fact]
    public void RunTailwindBuildTask_FailsWithDiagnostic_WhenRidCannotBeDetermined()
    {
        var task = CreateBuildTask("sample-unknown-rid", task =>
        {
            task.TailwindTargetRid = "unknown";
            task.TailwindVersion = "4.1.18";
        });

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW001", StringComparison.Ordinal) is true);
    }

    [Fact]
    public void RunTailwindBuildTask_FailsWithDiagnostic_WhenRidIsUnsupported()
    {
        var task = CreateBuildTask("sample-unsupported-rid", task =>
        {
            task.TailwindTargetRid = "plan9-x64";
            task.TailwindVersion = "4.1.18";
        });

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.False(result);
        Assert.Contains(
            buildEngine.Errors,
            error => error.Message?.Contains("Tailwind RID 'plan9-x64' is not supported", StringComparison.Ordinal) is true);
    }

    [Fact]
    public void RunTailwindBuildTask_FailsWithDiagnostic_WhenRidContainsPathSeparator()
    {
        var task = CreateBuildTask("sample-rid-path-component", task =>
        {
            task.TailwindTargetRid = "linux-x64/nested";
            task.TailwindVersion = "4.1.18";
        });

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.False(result);
        Assert.Contains(
            buildEngine.Errors,
            error => error.Message?.Contains("single relative path component", StringComparison.Ordinal) is true);
    }

    [Fact]
    public void RunTailwindBuildTask_FailsWithDiagnostic_WhenTailwindVersionIsMissing()
    {
        var task = CreateBuildTask("sample-missing-version", task =>
        {
            task.TailwindTargetRid = TailwindRuntimeMap.GetCurrentRid();
            task.TailwindVersion = null;
        });

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW002", StringComparison.Ordinal) is true);
    }

    [Fact]
    public void RunTailwindBuildTask_FailsWithDiagnostic_WhenPackagedRuntimeIsMissing()
    {
        var task = CreateBuildTask("sample-missing-runtime", task =>
        {
            task.TailwindTargetRid = TailwindRuntimeMap.GetCurrentRid();
            task.TailwindVersion = "4.1.18";
        });

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW004", StringComparison.Ordinal) is true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RunTailwindBuildTask_FailsWithDiagnostic_WhenTargetsDirectoryIsEmpty(string targetsDirectory)
    {
        var task = CreateBuildTask("sample-empty-targets-directory", task =>
        {
            task.TailwindTargetRid = TailwindRuntimeMap.GetCurrentRid();
            task.TailwindVersion = "4.1.18";
            task.TargetsDirectory = targetsDirectory;
        });

        Assert.Throws<ArgumentException>(() => task.Execute());
    }

    [Fact]
    public void RunTailwindBuildTask_FailsWithDiagnostic_WhenSiblingPackageLayoutCannotBeDerived()
    {
        var task = CreateBuildTask("sample-root-targets-directory", task =>
        {
            task.TailwindTargetRid = TailwindRuntimeMap.GetCurrentRid();
            task.TailwindVersion = "4.1.18";
            task.TargetsDirectory = Path.GetPathRoot(task.ProjectDirectory)!;
        });

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW004", StringComparison.Ordinal) is true);
    }

    [Fact]
    public void RunTailwindBuildTask_FailsWithDiagnostic_WhenSiblingPackageVersionIsInvalid()
    {
        var invalidTargetsDirectory = Path.Join(
            _tempRoot,
            "packages",
            "forgetrust.appsurface.web.tailwind",
            " ",
            "build",
            "net10.0");
        Directory.CreateDirectory(invalidTargetsDirectory);
        var task = CreateBuildTask("sample-invalid-package-version", task =>
        {
            task.TailwindTargetRid = TailwindRuntimeMap.GetCurrentRid();
            task.TailwindVersion = "4.1.18";
            task.TargetsDirectory = invalidTargetsDirectory;
        });

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW004", StringComparison.Ordinal) is true);
    }

    [Fact]
    public async Task RunTailwindBuildTask_UsesProjectRuntimeCandidate_WhenPresent()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var task = CreateBuildTask("sample-project-runtime", task =>
        {
            task.TailwindTargetRid = TailwindRuntimeMap.GetCurrentRid();
            task.TailwindVersion = "4.1.18";
        });
        var runtimePath = await CreateProjectRuntimeTailwindCliStubAsync(task.ProjectDirectory);

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.True(result);
        Assert.True(File.Exists(Path.Join(task.ProjectDirectory, "wwwroot", "css", "site.gen.css")));
        Assert.Empty(buildEngine.Errors);
        Assert.Empty(buildEngine.Warnings);
        Assert.Contains(buildEngine.Messages, message => message.Importance == MessageImportance.Normal && message.Message == "≈ tailwindcss v4.1.18");
        Assert.Equal(runtimePath, await File.ReadAllTextAsync(GetRuntimeSelectionMarkerPath(runtimePath)));
    }

    [Fact]
    public async Task RunTailwindBuildTask_UsesSharedDownloadCacheCandidate_WhenPresent()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var rid = TailwindRuntimeMap.GetCurrentRid();
        var runtimeBinaryName = TailwindRuntimeMap.GetRuntimeBinaryName(rid)
            ?? throw new InvalidOperationException($"No Tailwind runtime binary name exists for '{rid}'.");
        var cacheRoot = Path.Join(_tempRoot, "tailwind-download-cache");
        var runtimePath = TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, "4.1.18", rid, runtimeBinaryName);
        var task = CreateBuildTask("sample-shared-runtime-cache", task =>
        {
            task.TailwindTargetRid = rid;
            task.TailwindVersion = "4.1.18";
            task.TailwindDownloadCacheRoot = cacheRoot;
        });
        await CreateRuntimeTailwindCliStubAsync(task.ProjectDirectory, runtimePath);
        await WriteRuntimeChecksumAsync(runtimePath, runtimeBinaryName);

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.True(result);
        Assert.True(File.Exists(Path.Join(task.ProjectDirectory, "wwwroot", "css", "site.gen.css")));
        Assert.Empty(buildEngine.Errors);
        Assert.Empty(buildEngine.Warnings);
        Assert.Equal(runtimePath, await File.ReadAllTextAsync(GetRuntimeSelectionMarkerPath(runtimePath)));
    }

    [Fact]
    public async Task RunTailwindBuildTask_UsesSharedDownloadCacheCandidate_WhenChecksumUsesRelativeFileToken()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var rid = TailwindRuntimeMap.GetCurrentRid();
        var runtimeBinaryName = TailwindRuntimeMap.GetRuntimeBinaryName(rid)
            ?? throw new InvalidOperationException($"No Tailwind runtime binary name exists for '{rid}'.");
        var cacheRoot = Path.Join(_tempRoot, "tailwind-download-cache-relative-checksum");
        var runtimePath = TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, "4.1.18", rid, runtimeBinaryName);
        var task = CreateBuildTask("sample-shared-runtime-cache-relative-checksum", task =>
        {
            task.TailwindTargetRid = rid;
            task.TailwindVersion = "4.1.18";
            task.TailwindDownloadCacheRoot = cacheRoot;
        });
        await CreateRuntimeTailwindCliStubAsync(task.ProjectDirectory, runtimePath);
        await WriteRuntimeChecksumAsync(runtimePath, runtimeBinaryName, $"./{runtimeBinaryName}");

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.True(result);
        Assert.True(File.Exists(Path.Join(task.ProjectDirectory, "wwwroot", "css", "site.gen.css")));
        Assert.Empty(buildEngine.Errors);
        Assert.Empty(buildEngine.Warnings);
        Assert.Equal(runtimePath, await File.ReadAllTextAsync(GetRuntimeSelectionMarkerPath(runtimePath)));
    }

    [Fact]
    public async Task RunTailwindBuildTask_SkipsSharedDownloadCacheCandidate_WhenChecksumIsMissing()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var rid = TailwindRuntimeMap.GetCurrentRid();
        var runtimeBinaryName = TailwindRuntimeMap.GetRuntimeBinaryName(rid)
            ?? throw new InvalidOperationException($"No Tailwind runtime binary name exists for '{rid}'.");
        var cacheRoot = Path.Join(_tempRoot, "tailwind-download-cache-missing-checksum");
        var runtimePath = TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, "4.1.18", rid, runtimeBinaryName);
        var task = CreateBuildTask("sample-shared-runtime-cache-missing-checksum", task =>
        {
            task.TailwindTargetRid = rid;
            task.TailwindVersion = "4.1.18";
            task.TailwindDownloadCacheRoot = cacheRoot;
        });
        await CreateRuntimeTailwindCliStubAsync(task.ProjectDirectory, runtimePath);

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.False(result);
        Assert.False(File.Exists(Path.Join(task.ProjectDirectory, "wwwroot", "css", "site.gen.css")));
        Assert.Contains(buildEngine.Warnings, warning => warning.Message?.Contains("does not contain a checksum", StringComparison.Ordinal) is true);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW004", StringComparison.Ordinal) is true);
        Assert.False(File.Exists(GetRuntimeSelectionMarkerPath(runtimePath)));
    }

    [Fact]
    public async Task RunTailwindBuildTask_SkipsSharedDownloadCacheCandidate_WhenChecksumHasNoRuntimeEntry()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var rid = TailwindRuntimeMap.GetCurrentRid();
        var runtimeBinaryName = TailwindRuntimeMap.GetRuntimeBinaryName(rid)
            ?? throw new InvalidOperationException($"No Tailwind runtime binary name exists for '{rid}'.");
        var cacheRoot = Path.Join(_tempRoot, "tailwind-download-cache-no-runtime-entry");
        var runtimePath = TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, "4.1.18", rid, runtimeBinaryName);
        var task = CreateBuildTask("sample-shared-runtime-cache-no-runtime-entry", task =>
        {
            task.TailwindTargetRid = rid;
            task.TailwindVersion = "4.1.18";
            task.TailwindDownloadCacheRoot = cacheRoot;
        });
        await CreateRuntimeTailwindCliStubAsync(task.ProjectDirectory, runtimePath);
        await File.WriteAllTextAsync(
            Path.Join(Path.GetDirectoryName(runtimePath)!, "sha256sums.txt"),
            "malformed-checksum-line" + Environment.NewLine +
            "0000000000000000000000000000000000000000000000000000000000000000  ./not-the-runtime" + Environment.NewLine,
            Encoding.UTF8);

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.False(result);
        Assert.False(File.Exists(Path.Join(task.ProjectDirectory, "wwwroot", "css", "site.gen.css")));
        Assert.Contains(buildEngine.Warnings, warning => warning.Message?.Contains("does not contain a checksum", StringComparison.Ordinal) is true);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW004", StringComparison.Ordinal) is true);
        Assert.False(File.Exists(GetRuntimeSelectionMarkerPath(runtimePath)));
    }

    [Fact]
    public async Task RunTailwindBuildTask_SkipsSharedDownloadCacheCandidate_WhenChecksumDoesNotMatch()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var rid = TailwindRuntimeMap.GetCurrentRid();
        var runtimeBinaryName = TailwindRuntimeMap.GetRuntimeBinaryName(rid)
            ?? throw new InvalidOperationException($"No Tailwind runtime binary name exists for '{rid}'.");
        var cacheRoot = Path.Join(_tempRoot, "tailwind-download-cache-bad-checksum");
        var runtimePath = TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, "4.1.18", rid, runtimeBinaryName);
        var task = CreateBuildTask("sample-shared-runtime-cache-bad-checksum", task =>
        {
            task.TailwindTargetRid = rid;
            task.TailwindVersion = "4.1.18";
            task.TailwindDownloadCacheRoot = cacheRoot;
        });
        await CreateRuntimeTailwindCliStubAsync(task.ProjectDirectory, runtimePath);
        await File.WriteAllTextAsync(
            Path.Join(Path.GetDirectoryName(runtimePath)!, "sha256sums.txt"),
            $"0000000000000000000000000000000000000000000000000000000000000000  {runtimeBinaryName}{Environment.NewLine}",
            Encoding.UTF8);

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.False(result);
        Assert.False(File.Exists(Path.Join(task.ProjectDirectory, "wwwroot", "css", "site.gen.css")));
        Assert.Contains(buildEngine.Warnings, warning => warning.Message?.Contains("checksum does not match", StringComparison.Ordinal) is true);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW004", StringComparison.Ordinal) is true);
        Assert.False(File.Exists(GetRuntimeSelectionMarkerPath(runtimePath)));
    }

    [Fact]
    public async Task RunTailwindBuildTask_UsesSiblingRuntimePackageCandidate_WhenPresent()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var rid = TailwindRuntimeMap.GetCurrentRid();
        var runtimeBinaryName = TailwindRuntimeMap.GetRuntimeBinaryName(rid)
            ?? throw new InvalidOperationException($"No Tailwind runtime binary name exists for '{rid}'.");
        const string packageVersion = "4.1.18";
        var targetsDirectory = Path.Join(
            _tempRoot,
            "packages",
            "forgetrust.appsurface.web.tailwind",
            packageVersion,
            "build");
        var runtimePath = Path.Join(
            _tempRoot,
            "packages",
            $"forgetrust.appsurface.web.tailwind.runtime.{rid}",
            packageVersion,
            "runtimes",
            rid,
            "native",
            runtimeBinaryName);
        Directory.CreateDirectory(targetsDirectory);
        var task = CreateBuildTask("sample-sibling-runtime", task =>
        {
            task.TailwindTargetRid = rid;
            task.TailwindVersion = packageVersion;
            task.TargetsDirectory = targetsDirectory;
        });
        await CreateRuntimeTailwindCliStubAsync(task.ProjectDirectory, runtimePath);

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.True(result);
        Assert.True(File.Exists(Path.Join(task.ProjectDirectory, "wwwroot", "css", "site.gen.css")));
        Assert.Empty(buildEngine.Errors);
        Assert.Empty(buildEngine.Warnings);
        Assert.Equal(runtimePath, await File.ReadAllTextAsync(GetRuntimeSelectionMarkerPath(runtimePath)));
    }

    [Fact]
    public async Task RunTailwindBuildTask_FailsWithDiagnostic_WhenCliExitsNonZeroWithoutCapturedOutput()
    {
        var projectDirectory = Path.Join(_tempRoot, "sample-nonzero-empty-output");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(Path.Join(projectDirectory, "tools"));
        var markerPath = Path.Join(projectDirectory, "tailwind-cli-executed.marker");
        var cliRelativePath = await CreateTailwindCliStubAsync(projectDirectory, markerPath, exitCode: 7);
        var task = CreateBuildTask("sample-nonzero-empty-output", task =>
        {
            task.TailwindCliPath = cliRelativePath;
            task.TailwindVersion = "4.1.18";
        });

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.False(result);
        var error = Assert.Single(buildEngine.Errors);
        Assert.Contains("ASTW006", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Captured stdout tail:", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public async Task RunTailwindBuildTask_FailsWithDiagnostic_WhenCliProcessCannotStart()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        const string relativeCliPath = "tools/tailwindcss";
        var task = CreateBuildTask("sample-start-failure", task =>
        {
            task.TailwindCliPath = relativeCliPath;
            task.TailwindVersion = "4.1.18";
        });
        var cliPath = TestPathUtils.PathUnder(task.ProjectDirectory, relativeCliPath);
        Directory.CreateDirectory(Path.GetDirectoryName(cliPath)!);
        await File.WriteAllTextAsync(cliPath, "#!/bin/sh\nexit 0\n");

        var result = task.Execute();
        var buildEngine = Assert.IsType<RecordingBuildEngine>(task.BuildEngine);

        Assert.False(result);
        var error = Assert.Single(buildEngine.Errors);
        Assert.Contains("ASTW005", error.Message, StringComparison.Ordinal);
        Assert.Contains(cliPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackedPackageConsumption_BuildAndPublish_LoadsTaskAndGeneratesStaticWebAsset()
    {
        var feedDirectory = Path.Join(_tempRoot, "feed");
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

        var tailwindPackagePath = Path.Join(feedDirectory, $"ForgeTrust.AppSurface.Web.Tailwind.{packageVersion}.nupkg");
        Assert.True(File.Exists(tailwindPackagePath), "Expected the Tailwind package to be created.");
        using (var archive = System.IO.Compression.ZipFile.OpenRead(tailwindPackagePath))
        {
            Assert.Contains(archive.Entries, entry => entry.FullName == "build/tasks/ForgeTrust.AppSurface.Web.Tailwind.Tasks.dll");
            Assert.Contains(archive.Entries, entry => entry.FullName == "build/tasks/CliWrap.dll");
            Assert.Contains(archive.Entries, entry => entry.FullName == "build/ForgeTrust.AppSurface.Web.Tailwind.targets");
        }

        var projectDirectory = Path.Join(_tempRoot, "packed-consumer");
        Directory.CreateDirectory(Path.Join(projectDirectory, "wwwroot", "css"));
        Directory.CreateDirectory(Path.Join(projectDirectory, "tools"));
        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var markerPath = Path.Join(projectDirectory, "tailwind-cli-executed.marker");
        var cliRelativePath = await CreateTailwindCliStubAsync(projectDirectory, markerPath);
        var projectPath = Path.Join(projectDirectory, "PackedConsumer.csproj");
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

        var manifestPath = GetStaticWebAssetsBuildManifestPath(projectDirectory, buildOutput);
        await AssertBuildManifestContainsGeneratedCssAsync(manifestPath);

        var publishResult = await RunDotNetAsync(["publish", projectPath, "-nologo", "-v:minimal", "--no-restore"], projectDirectory);
        var publishOutput = publishResult.Stdout + Environment.NewLine + publishResult.Stderr;
        Assert.Equal(0, publishResult.ExitCode);
        Assert.True(
            File.Exists(Path.Join(projectDirectory, "bin", "Release", "net10.0", "publish", "wwwroot", "css", "site.gen.css")) ||
            File.Exists(Path.Join(projectDirectory, "bin", "Debug", "net10.0", "publish", "wwwroot", "css", "site.gen.css")),
            publishOutput);
    }

    [Fact]
    public async Task PackedPackageConsumption_DefaultRuntimePackageBuild_LoadsRuntimeCliWithoutExplicitPath()
    {
        var feedDirectory = Path.Join(_tempRoot, "default-runtime-feed");
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

        var projectDirectory = Path.Join(_tempRoot, "packed-default-runtime-consumer");
        Directory.CreateDirectory(Path.Join(projectDirectory, "wwwroot", "css"));
        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, "wwwroot", "css", "app.css"),
            "@import \"tailwindcss\";" + Environment.NewLine);

        var projectPath = Path.Join(projectDirectory, "PackedDefaultRuntimeConsumer.csproj");
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
        Assert.True(File.Exists(Path.Join(projectDirectory, "wwwroot", "css", "site.gen.css")), buildOutput);
    }

    private static string GetTailwindTargetsPath()
    {
        return Path.GetFullPath(
            Path.Join(
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

    private static string GetTailwindRuntimeTargetsPath()
    {
        return Path.GetFullPath(
            Path.Join(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "Web",
                "ForgeTrust.AppSurface.Web.Tailwind",
                "runtimes",
                "Tailwind.Runtime.Common.targets"));
    }

    private static string GetTailwindProjectPath()
    {
        return Path.GetFullPath(
            Path.Join(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "Web",
                "ForgeTrust.AppSurface.Web.Tailwind",
                "ForgeTrust.AppSurface.Web.Tailwind.csproj"));
    }

    private static string GetTailwindVersion()
    {
        return File.ReadAllText(Path.Join(Path.GetDirectoryName(GetTailwindProjectPath())!, "tailwind.version")).Trim();
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(
            Path.Join(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                ".."));
    }

    private static IEnumerable<string> GetPackageProjectsForSmoke(string repoRoot)
    {
        yield return Path.Join(repoRoot, "ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Core.csproj");
        yield return Path.Join(repoRoot, "Web", "ForgeTrust.AppSurface.Web.Tailwind", "runtimes", "ForgeTrust.AppSurface.Web.Tailwind.Runtime.win-x64.csproj");
        yield return Path.Join(repoRoot, "Web", "ForgeTrust.AppSurface.Web.Tailwind", "runtimes", "ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj");
        yield return Path.Join(repoRoot, "Web", "ForgeTrust.AppSurface.Web.Tailwind", "runtimes", "ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-x64.csproj");
        yield return Path.Join(repoRoot, "Web", "ForgeTrust.AppSurface.Web.Tailwind", "runtimes", "ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-arm64.csproj");
        yield return Path.Join(repoRoot, "Web", "ForgeTrust.AppSurface.Web.Tailwind", "runtimes", "ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64.csproj");
        yield return Path.Join(repoRoot, "Web", "ForgeTrust.AppSurface.Web.Tailwind", "ForgeTrust.AppSurface.Web.Tailwind.csproj");
    }

    private static string CreateSmokePackageVersion()
    {
        return "0.1.0-smoke." + Guid.NewGuid().ToString("N");
    }

    private static async Task<string> CreatePackagedTargetsCopyAsync(string projectDirectory)
    {
        var packageBuildDirectory = Path.Join(projectDirectory, "package", "build");
        var taskDirectory = Path.Join(packageBuildDirectory, "tasks");
        Directory.CreateDirectory(taskDirectory);

        var targetsPath = Path.Join(packageBuildDirectory, "ForgeTrust.AppSurface.Web.Tailwind.targets");
        File.Copy(GetTailwindTargetsPath(), targetsPath);
        File.Copy(
            Path.Join(Path.GetDirectoryName(GetTailwindTargetsPath())!, "..", "tailwind.version"),
            Path.Join(packageBuildDirectory, "tailwind.version"));

        var taskAssemblyDirectory = Path.GetDirectoryName(typeof(RunTailwindBuildTask).Assembly.Location)!;
        foreach (var file in Directory.EnumerateFiles(taskAssemblyDirectory, "*.dll"))
        {
            File.Copy(file, TestPathUtils.PathUnder(taskDirectory, Path.GetFileName(file)));
        }

        foreach (var file in Directory.EnumerateFiles(taskAssemblyDirectory, "*.deps.json"))
        {
            File.Copy(file, TestPathUtils.PathUnder(taskDirectory, Path.GetFileName(file)));
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

    private static async Task<string> CreateRuntimeProjectAsync(string projectDirectory)
    {
        Directory.CreateDirectory(projectDirectory);
        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <BaseIntermediateOutputPath>obj/Runtime/</BaseIntermediateOutputPath>
              </PropertyGroup>
            </Project>
            """,
            Encoding.UTF8);
        var projectPath = Path.Join(projectDirectory, "Runtime.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TailwindRid>linux-x64</TailwindRid>
                <PackageId>ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64</PackageId>
              </PropertyGroup>

              <Import Project="{{EscapeForXml(GetTailwindRuntimeTargetsPath())}}" />
            </Project>
            """,
            Encoding.UTF8);

        return projectPath;
    }

    private static string GetRuntimeBinaryPath(string projectDirectory, string configuration)
    {
        return Path.Join(
            projectDirectory,
            "obj",
            "Runtime",
            configuration,
            "net10.0",
            $"tailwind-{GetTailwindVersion()}",
            "tailwindcss-linux-x64");
    }

    private static async Task WriteRuntimeBinaryCacheAsync(string projectDirectory, string configuration)
    {
        var runtimeBinaryPath = GetRuntimeBinaryPath(projectDirectory, configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(runtimeBinaryPath)!);
        var binaryBytes = Encoding.UTF8.GetBytes("cached-tailwind-binary");
        await File.WriteAllBytesAsync(runtimeBinaryPath, binaryBytes);
        var hash = Convert.ToHexString(SHA256.HashData(binaryBytes)).ToLowerInvariant();
        await File.WriteAllTextAsync(
            Path.Join(Path.GetDirectoryName(runtimeBinaryPath)!, "sha256sums.txt"),
            $"{hash}  tailwindcss-linux-x64{Environment.NewLine}",
            Encoding.UTF8);
    }

    private RunTailwindBuildTask CreateBuildTask(string directoryName, Action<RunTailwindBuildTask>? configure = null)
    {
        var projectDirectory = Path.Join(_tempRoot, directoryName);
        Directory.CreateDirectory(projectDirectory);
        var task = new RunTailwindBuildTask
        {
            BuildEngine = new RecordingBuildEngine(),
            ProjectDirectory = projectDirectory,
            InputPath = "wwwroot/css/app.css",
            OutputPath = "wwwroot/css/site.gen.css",
            TargetsDirectory = projectDirectory,
            Configuration = CurrentConfiguration,
            TargetFramework = "net10.0"
        };
        configure?.Invoke(task);
        return task;
    }

    private static async Task<string> CreateProjectRuntimeTailwindCliStubAsync(string projectDirectory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("The project-runtime stub test writes a Unix executable script.");
        }

        var rid = TailwindRuntimeMap.GetCurrentRid();
        var runtimeBinaryName = TailwindRuntimeMap.GetRuntimeBinaryName(rid)
            ?? throw new InvalidOperationException($"No Tailwind runtime binary name exists for '{rid}'.");
        var runtimePath = Path.Join(projectDirectory, "runtimes", rid, "native", runtimeBinaryName);
        return await CreateRuntimeTailwindCliStubAsync(projectDirectory, runtimePath);
    }

    private static async Task<string> CreateRuntimeTailwindCliStubAsync(string projectDirectory, string runtimePath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("The runtime stub test writes a Unix executable script.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(runtimePath)!);
        var markerPath = GetRuntimeSelectionMarkerPath(runtimePath);
        await File.WriteAllTextAsync(
            runtimePath,
            $@"#!/bin/sh
mkdir -p ""{Path.Join(projectDirectory, "wwwroot", "css")}""
cat <<'EOF' > ""{Path.Join(projectDirectory, "wwwroot", "css", "site.gen.css")}""
.generated{{color:red;}}
EOF
printf '%s' ""{runtimePath}"" > ""{markerPath}""
printf ' \n' >&2
printf '≈ tailwindcss v4.1.18\n' >&2
printf '%s\n' ""{runtimePath}""
exit 0
		");

        const UnixFileMode executableMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(runtimePath, executableMode);
        return runtimePath;
    }

    private static async Task WriteRuntimeChecksumAsync(
        string runtimePath,
        string runtimeBinaryName,
        string? checksumFileToken = null)
    {
        var binaryBytes = await File.ReadAllBytesAsync(runtimePath);
        var hash = Convert.ToHexString(SHA256.HashData(binaryBytes)).ToLowerInvariant();
        await File.WriteAllTextAsync(
            Path.Join(Path.GetDirectoryName(runtimePath)!, "sha256sums.txt"),
            $"{hash}  {checksumFileToken ?? runtimeBinaryName}{Environment.NewLine}",
            Encoding.UTF8);
    }

    private static string GetRuntimeSelectionMarkerPath(string runtimePath)
    {
        return Path.ChangeExtension(runtimePath, ".selected");
    }

    private static async Task<string> CreateTailwindCliStubAsync(
        string projectDirectory,
        string markerPath,
        string outputRelativePath = "wwwroot/css/site.gen.css",
        string toolDirectoryName = "tools",
        string? stderrLine = null,
        int exitCode = 0)
    {
        var outputPath = TestPathUtils.PathUnder(projectDirectory, outputRelativePath);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var relativePath = Path.Join(toolDirectoryName, "tailwindcss.cmd");
            var fullPath = TestPathUtils.PathUnder(projectDirectory, relativePath);
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

        var unixRelativePath = Path.Join(toolDirectoryName, "tailwindcss");
        var unixFullPath = TestPathUtils.PathUnder(projectDirectory, unixRelativePath);
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
        var toolsDirectory = Path.Join(projectDirectory, "tools");
        Directory.CreateDirectory(toolsDirectory);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var relativePath = Path.Join("tools", "tailwindcss.cmd");
            var fullPath = TestPathUtils.PathUnder(projectDirectory, relativePath);
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

        var unixRelativePath = Path.Join("tools", "tailwindcss");
        var unixFullPath = TestPathUtils.PathUnder(projectDirectory, unixRelativePath);
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

    private static async Task<DotNetCommandResult> RunDotNetBuildForCurrentConfigurationAsync(
        string projectPath,
        string workingDirectory,
        params (string Name, string Value)[] environmentVariables)
    {
        return await RunDotNetAsync(
            ["build", projectPath, "-nologo", "-v:minimal", $"-p:Configuration={CurrentConfiguration}"],
            workingDirectory,
            environmentVariables);
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

    private static string GetStaticWebAssetsBuildManifestPath(string projectDirectory, string buildOutput)
    {
        var objDirectory = Path.Join(projectDirectory, "obj");
        var manifestPaths = Directory.Exists(objDirectory)
            ? Directory.EnumerateFiles(objDirectory, "staticwebassets.build.json", SearchOption.AllDirectories).ToArray()
            : [];

        var manifestPath = Assert.Single(manifestPaths);
        Assert.True(File.Exists(manifestPath), buildOutput);
        return manifestPath;
    }

    private sealed record DotNetCommandResult(int ExitCode, string Stdout, string Stderr);

    private sealed class RecordingBuildEngine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = [];

        public List<BuildWarningEventArgs> Warnings { get; } = [];

        public List<BuildMessageEventArgs> Messages { get; } = [];

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
            Messages.Add(e);
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
