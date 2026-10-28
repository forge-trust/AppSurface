using System.Diagnostics;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class PreflightConsumerTemplateTests : IDisposable
{
    private const string BeginMarker = "# BEGIN preflight consumer materialization helper";
    private const string EndMarker = "# END preflight consumer materialization helper";
    private readonly string _root = TestPathUtils.PathUnder(Path.GetTempPath(), "preflight-consumer-template", Guid.NewGuid().ToString("N"));

    public PreflightConsumerTemplateTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void CanonicalConsumerIsAnExplicitPackageVersionTemplate()
    {
        var source = TestPathUtils.PathUnder(TestPathUtils.FindRepoRoot(AppContext.BaseDirectory), "Durable", "consumers", "PostgreSqlPreflightConsumer");
        var templatePath = TestPathUtils.PathUnder(source, "PostgreSqlPreflightConsumer.csproj.template");
        var projectPath = TestPathUtils.PathUnder(source, "PostgreSqlPreflightConsumer.csproj");
        var template = File.ReadAllText(templatePath);

        // protects=source consumers cannot restore as projects before package version materialization; fails_when=an ordinary csproj or source reference reappears; no_claim=the downstream package is published
        Assert.False(File.Exists(projectPath));
        Assert.Contains("<PackageReference Include=\"ForgeTrust.AppSurface.Durable.PostgreSql\" Version=\"$(AppSurfacePackageVersion)\" />", template, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppSurfacePackageVersion", template, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", template, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaterializesTemplateAsAByteIdenticalCompleteProjectTree()
    {
        if (OperatingSystem.IsWindows()) return;
        var sourceDirectory = TestPathUtils.PathUnder(_root, "source tree with spaces");
        var targetDirectory = TestPathUtils.PathUnder(_root, "isolated target with spaces");
        Directory.CreateDirectory(sourceDirectory);
        var templatePath = TestPathUtils.PathUnder(sourceDirectory, "Consumer App.csproj.template");
        var templateBytes = "<Project />\n"u8.ToArray();
        var programPath = TestPathUtils.PathUnder(sourceDirectory, "Program.cs");
        var programBytes = "Console.WriteLine(\"staged sibling\");\n"u8.ToArray();
        var resourcePath = TestPathUtils.PathUnder(sourceDirectory, "Resources", "nested", "settings.json");
        var resourceBytes = "{\"mode\":\"offline\"}\n"u8.ToArray();
        var sourceBinFile = TestPathUtils.PathUnder(sourceDirectory, "bin", "stale.bin");
        var sourceObjFile = TestPathUtils.PathUnder(sourceDirectory, "obj", "stale.obj");
        Directory.CreateDirectory(Path.GetDirectoryName(resourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceBinFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceObjFile)!);
        await File.WriteAllBytesAsync(templatePath, templateBytes);
        await File.WriteAllBytesAsync(programPath, programBytes);
        await File.WriteAllBytesAsync(resourcePath, resourceBytes);
        await File.WriteAllTextAsync(sourceBinFile, "ignored binary output");
        await File.WriteAllTextAsync(sourceObjFile, "ignored intermediate output");
        var targetProject = TestPathUtils.PathUnder(targetDirectory, "Consumer App.csproj");

        var result = await RunHelperAsync(templatePath, targetDirectory);

        // protects=the real script helper stages the full sibling tree and renames only the template suffix; fails_when=resource bytes drift, outputs copy, or source changes; no_claim=restore can access external feeds
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(targetProject, result.StandardOutput.TrimEnd('\r', '\n'));
        Assert.Equal(templateBytes, await File.ReadAllBytesAsync(templatePath));
        Assert.Equal(programBytes, await File.ReadAllBytesAsync(programPath));
        Assert.Equal(resourceBytes, await File.ReadAllBytesAsync(resourcePath));
        Assert.Equal(templateBytes, await File.ReadAllBytesAsync(targetProject));
        Assert.Equal(programBytes, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(targetDirectory, "Program.cs")));
        Assert.Equal(resourceBytes, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(targetDirectory, "Resources", "nested", "settings.json")));
        Assert.False(File.Exists(TestPathUtils.PathUnder(targetDirectory, "Consumer App.csproj.template")));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(targetDirectory, "bin")));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(targetDirectory, "obj")));
        Assert.True(File.Exists(sourceBinFile));
        Assert.True(File.Exists(sourceObjFile));
    }

    [Fact]
    public async Task ExistingProjectPathIsReturnedWithoutCreatingTarget()
    {
        if (OperatingSystem.IsWindows()) return;
        var sourceDirectory = TestPathUtils.PathUnder(_root, "ordinary source");
        Directory.CreateDirectory(sourceDirectory);
        var projectPath = TestPathUtils.PathUnder(sourceDirectory, "Consumer.csproj");
        var targetDirectory = TestPathUtils.PathUnder(_root, "unused destination");
        await File.WriteAllTextAsync(projectPath, "<Project />");

        var result = await RunHelperAsync(projectPath, targetDirectory);

        // protects=legacy ordinary projects bypass staging; fails_when=the existing path changes or target is created; no_claim=ordinary project contents are validated
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(projectPath, result.StandardOutput.TrimEnd('\r', '\n'));
        Assert.False(Directory.Exists(targetDirectory));
        Assert.Equal("<Project />", await File.ReadAllTextAsync(projectPath));
    }

    [Theory]
    [InlineData("missing-template")]
    [InlineData("destination-collision")]
    [InlineData("destination-within-source")]
    public async Task TemplateMaterializationFailuresRemainNonzero(string failure)
    {
        if (OperatingSystem.IsWindows()) return;
        var sourceDirectory = TestPathUtils.PathUnder(_root, failure, "source");
        Directory.CreateDirectory(sourceDirectory);
        var templatePath = TestPathUtils.PathUnder(sourceDirectory, "Consumer.csproj.template");
        var targetDirectory = TestPathUtils.PathUnder(_root, failure, "target");
        if (failure == "destination-collision")
        {
            await File.WriteAllTextAsync(templatePath, "<Project />");
            Directory.CreateDirectory(targetDirectory);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(targetDirectory, "caller.txt"), "preserve destination");
        }
        else if (failure == "destination-within-source")
        {
            await File.WriteAllTextAsync(templatePath, "<Project />");
            targetDirectory = TestPathUtils.PathUnder(sourceDirectory, "nested destination");
        }

        var result = await RunHelperAsync(templatePath, targetDirectory);

        // protects=source or destination failures reach the caller; fails_when=the helper reports successful materialization or overwrites an occupied destination; no_claim=all filesystem errors have identical diagnostics
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(result.StandardError);
        if (failure != "destination-collision")
        {
            Assert.False(Directory.Exists(targetDirectory));
        }
        else
        {
            Assert.Equal("preserve destination", await File.ReadAllTextAsync(TestPathUtils.PathUnder(targetDirectory, "caller.txt")));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<ProcessResult> RunHelperAsync(string source, string target)
    {
        var helper = ExtractHelperBlock();
        var startInfo = new ProcessStartInfo("bash")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(helper + "\nmaterialize_consumer_project \"$PREFLIGHT_CONSUMER_SOURCE\" \"$PREFLIGHT_CONSUMER_TARGET\"");
        startInfo.Environment["PREFLIGHT_CONSUMER_SOURCE"] = source;
        startInfo.Environment["PREFLIGHT_CONSUMER_TARGET"] = target;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Bash for the materialization helper.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            // gstack-timing-ok: existing process guard bounds a hung Bash helper; it is not a throughput assertion.
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Extracted materialization helper exceeded the existing 10 second process guard.");
        }

        return new(process.ExitCode, await stdout, await stderr);
    }

    private static string ExtractHelperBlock()
    {
        var script = File.ReadAllText(TestPathUtils.PathUnder(TestPathUtils.FindRepoRoot(AppContext.BaseDirectory), "Durable", "verify-preflight-artifacts.sh"));
        var begin = script.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0) throw new InvalidDataException("The preflight consumer materialization helper begin marker is missing.");
        var end = script.IndexOf(EndMarker, begin + BeginMarker.Length, StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException("The preflight consumer materialization helper end marker is missing.");
        var blockStart = script.IndexOf('\n', begin);
        if (blockStart < 0 || blockStart >= end) throw new InvalidDataException("The preflight consumer materialization helper block is malformed.");
        return script[(blockStart + 1)..end];
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
