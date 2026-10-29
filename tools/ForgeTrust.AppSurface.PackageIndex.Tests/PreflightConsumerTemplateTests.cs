using System.Diagnostics;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class PreflightConsumerTemplateTests : IDisposable
{
    private readonly string _root = TestPathUtils.PathUnder(Path.GetTempPath(), "preflight-consumer-template", Guid.NewGuid().ToString("N"));

    public PreflightConsumerTemplateTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void CanonicalConsumerKeepsPackageAndProjectReferenceModesSeparate()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var source = TestPathUtils.PathUnder(repository, "Durable", "consumers", "PostgreSqlPreflightConsumer");
        var templatePath = TestPathUtils.PathUnder(source, "PostgreSqlPreflightConsumer.csproj.template");
        var projectPath = TestPathUtils.PathUnder(source, "PostgreSqlPreflightConsumer.csproj");
        var template = File.ReadAllText(templatePath);
        var project = XDocument.Load(projectPath);
        var templateDocument = XDocument.Load(templatePath);
        var projectReferences = project.Descendants("ProjectReference").ToArray();
        var projectReference = Assert.Single(projectReferences);
        var packageReferences = templateDocument.Descendants("PackageReference").ToArray();
        var providerReference = Assert.Single(packageReferences, reference =>
            string.Equals((string?)reference.Attribute("Include"), "ForgeTrust.AppSurface.Durable.PostgreSql", StringComparison.Ordinal));
        var expectedProviderProject = TestPathUtils.PathUnder(repository, "Durable", "ForgeTrust.AppSurface.Durable.PostgreSql", "ForgeTrust.AppSurface.Durable.PostgreSql.csproj");
        var referencedProviderProject = Path.GetFullPath(Path.Join(source, (string?)projectReference.Attribute("Include")));

        // protects=source project consumes local code while artifact template consumes exactly the manifest version; fails_when=default/fallback versions or package/project reference modes blur; no_claim=the package is published
        Assert.Equal(projectPath, Assert.Single(Directory.EnumerateFiles(source, "*.csproj", SearchOption.TopDirectoryOnly)));
        Assert.Equal(templatePath, Assert.Single(Directory.EnumerateFiles(source, "*.csproj.template", SearchOption.TopDirectoryOnly)));
        Assert.Equal(expectedProviderProject, referencedProviderProject);
        Assert.Equal("false", project.Descendants("IsPackable").Single().Value);
        Assert.Null(project.Descendants("AppSurfacePackageVersion").SingleOrDefault());
        Assert.DoesNotContain(project.Descendants("PackageReference"), reference => ((string?)reference.Attribute("Include")) == "ForgeTrust.AppSurface.Durable.PostgreSql");
        Assert.Equal("$(AppSurfacePackageVersion)", (string?)providerReference.Attribute("Version"));
        Assert.Empty(templateDocument.Descendants("ProjectReference"));
        Assert.Contains("<PackageReference Include=\"ForgeTrust.AppSurface.Durable.PostgreSql\" Version=\"$(AppSurfacePackageVersion)\" />", template, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppSurfacePackageVersion", template, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", template, StringComparison.Ordinal);
        var explicitPackageReferences = project.Descendants("PackageReference").ToDictionary(
            reference => (string)reference.Attribute("Include")!,
            reference => (string)reference.Attribute("Version")!,
            StringComparer.Ordinal);
        Assert.Equal("10.0.3", explicitPackageReferences["Npgsql"]);
        Assert.Equal("4.13.0", explicitPackageReferences["Testcontainers.PostgreSql"]);
        Assert.Equal("10.0.8", explicitPackageReferences["Microsoft.Extensions.DependencyInjection"]);
        Assert.Equal("2026.0.0", explicitPackageReferences["SSH.NET"]);
        Assert.Equal(4, explicitPackageReferences.Count);
    }

    [Fact]
    public async Task MaterializesTemplateAsAByteIdenticalCompleteProjectTree()
    {
        if (OperatingSystem.IsWindows()) return;
        var sourceDirectory = TestPathUtils.PathUnder(_root, "source tree with spaces");
        var repositoryDirectory = TestPathUtils.PathUnder(_root, "repository root with spaces");
        var targetDirectory = TestPathUtils.PathUnder(_root, "isolated target with spaces");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(repositoryDirectory);
        var templatePath = TestPathUtils.PathUnder(sourceDirectory, "PostgreSqlPreflightConsumer.csproj.template");
        var templateBytes = "<Project />\n"u8.ToArray();
        var ordinaryProjectPath = TestPathUtils.PathUnder(sourceDirectory, "PostgreSqlPreflightConsumer.csproj");
        var ordinaryProjectBytes = "<Project><ItemGroup><ProjectReference Include=\"../Provider/Provider.csproj\" /></ItemGroup></Project>\n"u8.ToArray();
        var programPath = TestPathUtils.PathUnder(sourceDirectory, "Program.cs");
        var programBytes = "Console.WriteLine(\"staged sibling\");\n"u8.ToArray();
        var resourcePath = TestPathUtils.PathUnder(sourceDirectory, "Resources", "nested", "settings.json");
        var resourceBytes = "{\"mode\":\"offline\"}\n"u8.ToArray();
        var nestedProjectResourcePath = TestPathUtils.PathUnder(sourceDirectory, "Resources", "nested", "PostgreSqlPreflightConsumer.csproj");
        var nestedProjectResourceBytes = "nested project metadata\n"u8.ToArray();
        var sourceBinFile = TestPathUtils.PathUnder(sourceDirectory, "bin", "stale.bin");
        var sourceObjFile = TestPathUtils.PathUnder(sourceDirectory, "obj", "stale.obj");
        var propsBytes = "<Project><PropertyGroup><FixturePolicy>props</FixturePolicy></PropertyGroup></Project>\n"u8.ToArray();
        var targetsBytes = "<Project><PropertyGroup><FixturePolicy>targets</FixturePolicy></PropertyGroup></Project>\n"u8.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(resourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceBinFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceObjFile)!);
        await File.WriteAllBytesAsync(templatePath, templateBytes);
        await File.WriteAllBytesAsync(ordinaryProjectPath, ordinaryProjectBytes);
        await File.WriteAllBytesAsync(programPath, programBytes);
        await File.WriteAllBytesAsync(resourcePath, resourceBytes);
        await File.WriteAllBytesAsync(nestedProjectResourcePath, nestedProjectResourceBytes);
        await File.WriteAllTextAsync(sourceBinFile, "ignored binary output");
        await File.WriteAllTextAsync(sourceObjFile, "ignored intermediate output");
        var sourceFilesBefore = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(sourceDirectory, path), File.ReadAllBytes, StringComparer.Ordinal);
        var expectedStagedFiles = new Dictionary<string, byte[]>(sourceFilesBefore, StringComparer.Ordinal);
        expectedStagedFiles.Remove("PostgreSqlPreflightConsumer.csproj.template");
        expectedStagedFiles.Remove("PostgreSqlPreflightConsumer.csproj");
        foreach (var generatedPath in expectedStagedFiles.Keys.Where(path =>
                     path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj")).ToArray())
            expectedStagedFiles.Remove(generatedPath);
        expectedStagedFiles["PostgreSqlPreflightConsumer.csproj"] = templateBytes;
        var propsPath = TestPathUtils.PathUnder(repositoryDirectory, "Directory.Build.props");
        var targetsPath = TestPathUtils.PathUnder(repositoryDirectory, "Directory.Build.targets");
        await File.WriteAllBytesAsync(propsPath, propsBytes);
        await File.WriteAllBytesAsync(targetsPath, targetsBytes);
        expectedStagedFiles["Directory.Build.props"] = propsBytes;
        expectedStagedFiles["Directory.Build.targets"] = targetsBytes;
        var targetProject = TestPathUtils.PathUnder(targetDirectory, "PostgreSqlPreflightConsumer.csproj");

        var result = await RunHelperAsync(templatePath, repositoryDirectory, targetDirectory);

        // protects=the real script helper stages source bytes, omits the top-level restore wrapper and renames only the template suffix; fails_when=nested same-name resources are omitted, source changes, or outputs copy; no_claim=restore can access external feeds
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(targetProject, result.StandardOutput.TrimEnd('\r', '\n'));
        Assert.Equal(templateBytes, await File.ReadAllBytesAsync(templatePath));
        Assert.Equal(ordinaryProjectBytes, await File.ReadAllBytesAsync(ordinaryProjectPath));
        Assert.Equal(programBytes, await File.ReadAllBytesAsync(programPath));
        Assert.Equal(resourceBytes, await File.ReadAllBytesAsync(resourcePath));
        Assert.Equal(nestedProjectResourceBytes, await File.ReadAllBytesAsync(nestedProjectResourcePath));
        var stagedFiles = Directory.EnumerateFiles(targetDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(targetDirectory, path), File.ReadAllBytes, StringComparer.Ordinal);
        Assert.Equal(expectedStagedFiles.Keys.Order(StringComparer.Ordinal), stagedFiles.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in expectedStagedFiles) Assert.Equal(bytes, stagedFiles[path]);
        Assert.False(File.Exists(TestPathUtils.PathUnder(targetDirectory, "PostgreSqlPreflightConsumer.csproj.template")));
        Assert.Equal(targetProject, Assert.Single(Directory.EnumerateFiles(targetDirectory, "*.csproj", SearchOption.TopDirectoryOnly)));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(targetDirectory, "bin")));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(targetDirectory, "obj")));
        Assert.Equal(propsBytes, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(targetDirectory, "Directory.Build.props")));
        Assert.Equal(targetsBytes, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(targetDirectory, "Directory.Build.targets")));
        Assert.Equal(sourceFilesBefore.Keys.Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(sourceDirectory, path))
                .Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in sourceFilesBefore) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(sourceDirectory, path)));
        Assert.True(File.Exists(sourceBinFile));
        Assert.True(File.Exists(sourceObjFile));
    }

    [Theory]
    [InlineData("missing-template")]
    [InlineData("noncanonical-name")]
    [InlineData("destination-collision")]
    [InlineData("destination-within-source")]
    [InlineData("missing-build-policy")]
    [InlineData("linked-build-policy")]
    [InlineData("linked-authored-file")]
    public async Task TemplateMaterializationFailuresRemainNonzero(string failure)
    {
        if (OperatingSystem.IsWindows()) return;
        var sourceDirectory = TestPathUtils.PathUnder(_root, failure, "source");
        var repositoryDirectory = TestPathUtils.PathUnder(_root, failure, "repository");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(repositoryDirectory);
        var templatePath = TestPathUtils.PathUnder(sourceDirectory,
            failure == "noncanonical-name" ? "PreflightConsumer.csproj.template" : "PostgreSqlPreflightConsumer.csproj.template");
        var targetDirectory = TestPathUtils.PathUnder(_root, failure, "target");
        var props = TestPathUtils.PathUnder(repositoryDirectory, "Directory.Build.props");
        var targets = TestPathUtils.PathUnder(repositoryDirectory, "Directory.Build.targets");
        await File.WriteAllTextAsync(props, "<Project />");
        await File.WriteAllTextAsync(targets, "<Project />");
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(sourceDirectory, "Program.cs"), "Console.WriteLine(\"source\");\n");
        if (failure != "missing-template") await File.WriteAllTextAsync(templatePath, "<Project />");
        if (failure == "destination-collision")
        {
            Directory.CreateDirectory(targetDirectory);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(targetDirectory, "caller.txt"), "preserve destination");
        }
        else if (failure == "destination-within-source")
        {
            targetDirectory = TestPathUtils.PathUnder(sourceDirectory, "nested destination");
        }
        else if (failure == "missing-build-policy")
        {
            File.Delete(targets);
        }
        else if (failure == "linked-build-policy")
        {
            File.Delete(props);
            File.CreateSymbolicLink(props, targets);
        }
        else if (failure == "linked-authored-file")
        {
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(_root, failure, "outside.cs"), "linked");
            File.CreateSymbolicLink(TestPathUtils.PathUnder(sourceDirectory, "Linked.cs"),
                TestPathUtils.PathUnder(_root, failure, "outside.cs"));
        }

        var result = await RunHelperAsync(templatePath, repositoryDirectory, targetDirectory);

        // protects=canonical source and build-policy constraints, overlap, or destination failures reach the caller; fails_when=the helper reports successful materialization or overwrites an occupied destination; no_claim=all filesystem errors have identical diagnostics
        Assert.NotEqual(0, result.ExitCode);
        var diagnostic = failure switch
        {
            "missing-template" => "consumer-template-invalid",
            "noncanonical-name" => "consumer-template-name-invalid",
            "destination-collision" => "consumer-destination-exists",
            "destination-within-source" => "consumer-template-destination-overlaps-source",
            "missing-build-policy" or "linked-build-policy" => "consumer-build-policy-missing",
            "linked-authored-file" => "consumer-source-invalid",
            _ => throw new InvalidOperationException($"Unknown materialization failure '{failure}'.")
        };
        Assert.Contains(diagnostic, result.StandardError, StringComparison.Ordinal);
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

    private async Task<ProcessResult> RunHelperAsync(string template, string repository, string target)
    {
        var helper = TestPathUtils.PathUnder(TestPathUtils.FindRepoRoot(AppContext.BaseDirectory),
            "Durable", "materialize-preflight-consumer.sh");
        var startInfo = new ProcessStartInfo("bash")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(helper);
        startInfo.ArgumentList.Add(template);
        startInfo.ArgumentList.Add(repository);
        startInfo.ArgumentList.Add(target);

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
            throw new TimeoutException("Shared materialization helper exceeded the existing 10 second process guard.");
        }

        return new(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
