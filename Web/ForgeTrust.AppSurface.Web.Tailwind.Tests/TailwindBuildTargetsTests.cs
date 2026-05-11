using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

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
        string outputRelativePath = "wwwroot/css/site.gen.css")
    {
        var toolsDirectory = Path.Combine(projectDirectory, "tools");
        var outputPath = Path.Combine(projectDirectory, outputRelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var relativePath = Path.Combine("tools", "tailwindcss.cmd");
            var fullPath = Path.Combine(projectDirectory, relativePath);
            await File.WriteAllTextAsync(
                fullPath,
                $@"@echo off
if not exist ""{Path.GetDirectoryName(outputPath)}"" mkdir ""{Path.GetDirectoryName(outputPath)}""
>""{outputPath}"" echo .generated{{color:red;}}
echo invoked>""{markerPath}""
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
mkdir -p ""{Path.GetDirectoryName(outputPath)}""
cat <<'EOF' > ""{outputPath}""
.generated{{color:red;}}
EOF
printf 'invoked\n' > ""{markerPath}""
exit 0
");
        File.SetUnixFileMode(unixFullPath, executableMode);
        return unixRelativePath;
    }

    private static async Task<DotNetCommandResult> RunDotNetBuildAsync(string projectPath, string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.StartInfo.ArgumentList.Add("build");
        process.StartInfo.ArgumentList.Add(projectPath);
        process.StartInfo.ArgumentList.Add("-nologo");
        process.StartInfo.ArgumentList.Add("-v:minimal");

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return new DotNetCommandResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
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
}
