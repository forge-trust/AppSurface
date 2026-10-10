using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Durable.Templates.Tests;

public sealed class TemplateRepositoryContractTests
{
    private const string PackageRelativePath = "Durable/ForgeTrust.AppSurface.Durable.Templates";
    private const string TemplateRelativePath = "content/durable-worker";
    private const string TemplateManifestRelativePath = ".template.config/template.json";
    private const string TemplateProjectFileName = "ForgeTrust.AppSurface.Durable.Templates.csproj";
    private const string TemplateIdentity = "ForgeTrust.AppSurface.Durable.Templates.DurableWorker";

    private static readonly Regex ExactNuGetVersionPattern = new(
        @"\A\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?(?:\+[0-9A-Za-z][0-9A-Za-z.-]*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string PackageRoot = TestPathUtils.PathUnder(RepositoryRoot, PackageRelativePath);
    private static readonly string TemplateRoot = TestPathUtils.PathUnder(PackageRoot, TemplateRelativePath);

    private static readonly string[] ExpectedTemplateFiles =
    [
        ".editorconfig",
        ".gitignore",
        ".template.config/template.json",
        "AppSurfaceDurableWorker.slnx",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "README.md",
        "appsettings.json",
        "docs/replace-sample.md",
        "src/AppSurfaceDurableWorker/AppSurfaceDurableWorker.csproj",
        "src/AppSurfaceDurableWorker/AssemblyInfo.cs",
        "src/AppSurfaceDurableWorker/Hosting/ActivationEndpoints.cs",
        "src/AppSurfaceDurableWorker/Hosting/DevelopmentBearerHandler.cs",
        "src/AppSurfaceDurableWorker/Hosting/Telemetry.cs",
        "src/AppSurfaceDurableWorker/Program.cs",
        "src/AppSurfaceDurableWorker/Work/SampleWork.cs",
        "src/AppSurfaceDurableWorker/Work/SampleWorkExecutor.cs",
        "src/AppSurfaceDurableWorker/Work/SampleWorkProducer.cs",
        "src/AppSurfaceDurableWorker/Work/WorkRegistration.cs",
        "src/AppSurfaceDurableWorker/WorkerApplication.cs",
        "tests/AppSurfaceDurableWorker.Tests/ActivityExporter.cs",
        "tests/AppSurfaceDurableWorker.Tests/AppSurfaceDurableWorker.Tests.csproj",
        "tests/AppSurfaceDurableWorker.Tests/BoundedProcessRunner.cs",
        "tests/AppSurfaceDurableWorker.Tests/FirstDurableWorkTests.cs",
        "tests/AppSurfaceDurableWorker.Tests/FixtureBudgets.cs",
        "tests/AppSurfaceDurableWorker.Tests/HostContractTests.cs",
        "tests/AppSurfaceDurableWorker.Tests/NativePostgreSqlSmokeTests.cs",
        "tests/AppSurfaceDurableWorker.Tests/PostgreSqlFixture.cs",
        "tests/AppSurfaceDurableWorker.Tests/PostgreSqlTestContainerImage.cs",
        "tests/AppSurfaceDurableWorker.Tests/SetupOperationLifetime.cs",
        "tests/AppSurfaceDurableWorker.Tests/SetupOperationLifetimeTests.cs",
        "tests/AppSurfaceDurableWorker.Tests/TypedWorkScenarioTests.cs",
        "tests/AppSurfaceDurableWorker.Tests/xunit.runner.json"
    ];

    [Fact]
    public void AuthoredContent_HasExactlyTheReviewedTemplateInventory()
    {
        Assert.True(Directory.Exists(TemplateRoot), $"Authored template content was not found at '{TemplateRoot}'.");

        var actualFiles = Directory.EnumerateFiles(TemplateRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(TemplateRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !ContainsBuildArtifactDirectory(path))
            .ToArray();

        AssertExactInventory("authored template", ExpectedTemplateFiles, actualFiles);
    }

    [Fact]
    public void DocumentedFirstWorkFilterDoesNotSelectOtherProofsAfterProjectRename()
    {
        const string projectName = "FirstDurableWorker";
        var testRoot = TestPathUtils.PathUnder(TemplateRoot, "tests", "AppSurfaceDurableWorker.Tests");
        var selectedClasses = new List<string>();
        foreach (var path in Directory.EnumerateFiles(testRoot, "*.cs"))
        {
            var generated = File.ReadAllText(path).Replace("AppSurfaceDurableWorker", projectName, StringComparison.Ordinal);
            var testNamespace = Regex.Match(generated, @"namespace ([^;]+);").Groups[1].Value;
            Assert.Equal("DurableWorkerTemplate.Tests", testNamespace);
            foreach (Match testClass in Regex.Matches(generated, @"public sealed class (\w+Tests)"))
            {
                var qualifiedName = testNamespace + "." + testClass.Groups[1].Value;
                if (qualifiedName.Contains("FirstDurableWork", StringComparison.Ordinal))
                {
                    selectedClasses.Add(qualifiedName);
                }
            }
        }

        Assert.Equal(["DurableWorkerTemplate.Tests.FirstDurableWorkTests"], selectedClasses);
    }

    [Fact]
    public async Task TemplatePackage_UsesNativeTemplateMetadataAndExcludesAuthoredCodeFromCompilation()
    {
        var projectPath = TestPathUtils.PathUnder(PackageRoot, TemplateProjectFileName);
        var project = await LoadProjectAsync(projectPath);

        Assert.Empty(GetItems(project, "ProjectReference"));

        var contentIncludes = GetItems(project, "Content")
            .Select(item => GetItemInclude(item))
            .Where(include => include.Contains("content", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Contains(contentIncludes, include =>
        {
            var normalized = include.Replace('\\', '/');
            return normalized.Contains("TemplateContentRoot", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("**", StringComparison.Ordinal);
        });

        using var evaluated = await EvaluatePackageProjectAsync(projectPath);
        var root = evaluated.RootElement;
        var properties = root.GetProperty("Properties");
        Assert.Equal("ForgeTrust.AppSurface.Durable.Templates", properties.GetProperty("PackageId").GetString());
        Assert.Equal("Template", properties.GetProperty("PackageType").GetString());
        Assert.Equal("false", properties.GetProperty("IncludeBuildOutput").GetString()?.ToLowerInvariant());
        Assert.Equal("true", properties.GetProperty("IncludeContentInPack").GetString()?.ToLowerInvariant());
        Assert.Contains("content", properties.GetProperty("ContentTargetFolders").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("true", properties.GetProperty("IsPackable").GetString()?.ToLowerInvariant());

        var items = root.GetProperty("Items");
        var compileItems = items.GetProperty("Compile");
        Assert.Empty(compileItems.EnumerateArray());

        var packedContentItems = items.GetProperty("Content").EnumerateArray()
            .Where(item => item.TryGetProperty("Pack", out var pack) && pack.GetString() == "true")
            .ToArray();
        Assert.All(packedContentItems, item => Assert.True(
            IsPathWithin(item.GetProperty("FullPath").GetString(), TemplateRoot),
            $"Packed content must come from the authored template root: '{item.GetProperty("FullPath").GetString()}'."));
        var packedTemplateItems = packedContentItems;
        var packedTemplatePaths = packedTemplateItems
            .Select(item => Path.GetRelativePath(TemplateRoot, item.GetProperty("FullPath").GetString()!)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray();
        AssertExactInventory("packed template content", ExpectedTemplateFiles, packedTemplatePaths);

        foreach (var item in packedTemplateItems)
        {
            var relativePath = Path.GetRelativePath(TemplateRoot, item.GetProperty("FullPath").GetString()!)
                .Replace(Path.DirectorySeparatorChar, '/');
            Assert.Equal($"content/durable-worker/{relativePath}", item.GetProperty("PackagePath").GetString());
        }

        Assert.Empty(GetItems(project, "PackageReference"));
    }

    [Fact]
    public async Task TemplatePackage_PacksManifestAndDotfilesButExcludesBuildArtifacts()
    {
        var fixtureRoot = Directory.CreateTempSubdirectory("durable-template-pack-");
        var includedFiles = new[]
        {
            ".editorconfig",
            ".gitignore",
            ".template.config/template.json",
            "README.md"
        };
        var excludedFiles = new[]
        {
            "src/Host/bin/host.dll",
            "src/Host/obj/project.assets.json",
            "tests/Host.Tests/bin/tests.dll",
            "tests/Host.Tests/obj/project.assets.json",
            "TestResults/run.trx",
            "tests/Host.Tests/TestResults/run.trx",
            ".vs/state.bin",
            ".git/config"
        };

        try
        {
            foreach (var relativePath in includedFiles.Concat(excludedFiles))
            {
                var fullPath = TestPathUtils.PathUnder(fixtureRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, "fixture");
            }

            using var evaluated = await EvaluatePackageProjectAsync(
                TestPathUtils.PathUnder(PackageRoot, TemplateProjectFileName),
                fixtureRoot.FullName);
            var packedItems = evaluated.RootElement.GetProperty("Items").GetProperty("Content").EnumerateArray()
                .Where(item => item.TryGetProperty("Pack", out var pack) && pack.GetString() == "true")
                .ToArray();
            Assert.All(packedItems, item => Assert.True(
                IsPathWithin(item.GetProperty("FullPath").GetString(), fixtureRoot.FullName),
                $"Packed fixture content must come from '{fixtureRoot.FullName}'."));

            var packedPaths = packedItems
                .Select(item => Path.GetRelativePath(fixtureRoot.FullName, item.GetProperty("FullPath").GetString()!)
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .ToArray();
            AssertExactInventory("isolated packed template fixture", includedFiles, packedPaths);

            foreach (var item in packedItems)
            {
                var relativePath = Path.GetRelativePath(fixtureRoot.FullName, item.GetProperty("FullPath").GetString()!)
                    .Replace(Path.DirectorySeparatorChar, '/');
                Assert.Equal($"content/durable-worker/{relativePath}", item.GetProperty("PackagePath").GetString());
            }
        }
        finally
        {
            fixtureRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TemplateManifest_DeclaresTheStableNativeProjectTemplateContract()
    {
        var manifestPath = TestPathUtils.PathUnder(TemplateRoot, TemplateManifestRelativePath);
        Assert.True(File.Exists(manifestPath), $"Native template manifest was not found at '{manifestPath}'.");
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath));
        var root = manifest.RootElement;

        Assert.Equal("appsurface-durable-worker", root.GetProperty("shortName").GetString());
        Assert.Equal("AppSurfaceDurableWorker", root.GetProperty("sourceName").GetString());
        Assert.True(root.GetProperty("preferNameDirectory").GetBoolean());

        var identity = root.GetProperty("identity").GetString();
        Assert.Equal(TemplateIdentity, identity);

        var tags = root.GetProperty("tags");
        Assert.Equal("C#", tags.GetProperty("language").GetString());
        Assert.Equal("project", tags.GetProperty("type").GetString());

        var classifications = root.GetProperty("classifications").EnumerateArray()
            .Select(value => value.GetString())
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "AppSurface", "Durable", "PostgreSQL", "Worker" }.Order(StringComparer.Ordinal), classifications);
        Assert.False(root.TryGetProperty("symbols", out _));
        Assert.False(root.TryGetProperty("postActions", out _));

        var primaryOutput = Assert.Single(root.GetProperty("primaryOutputs").EnumerateArray());
        Assert.Equal(
            "src/AppSurfaceDurableWorker/AppSurfaceDurableWorker.csproj",
            primaryOutput.GetProperty("path").GetString());
    }

    [Fact]
    public async Task AuthoredProjects_UseCentrallyPinnedPackagesAndKeepProjectReferencesInsideTheTemplate()
    {
        var hostProjectPath = TestPathUtils.PathUnder(
            TemplateRoot,
            "src",
            "AppSurfaceDurableWorker",
            "AppSurfaceDurableWorker.csproj");
        var testProjectPath = TestPathUtils.PathUnder(
            TemplateRoot,
            "tests",
            "AppSurfaceDurableWorker.Tests",
            "AppSurfaceDurableWorker.Tests.csproj");

        var hostProject = await LoadProjectAsync(hostProjectPath);
        var testProject = await LoadProjectAsync(testProjectPath);
        var buildProps = await LoadProjectAsync(TestPathUtils.PathUnder(TemplateRoot, "Directory.Build.props"));
        var packageProps = await LoadProjectAsync(TestPathUtils.PathUnder(TemplateRoot, "Directory.Packages.props"));

        AssertProjectProperty(buildProps, "TargetFramework", "net10.0");
        AssertProjectProperty(buildProps, "Nullable", "enable");
        AssertProjectProperty(buildProps, "ImplicitUsings", "enable");
        AssertProjectProperty(buildProps, "ManagePackageVersionsCentrally", "true");
        AssertProjectProperty(buildProps, "TreatWarningsAsErrors", "true");
        AssertProjectProperty(buildProps, "Deterministic", "true");
        AssertProjectProperty(packageProps, "ManagePackageVersionsCentrally", "true");
        AssertProjectProperty(packageProps, "CentralPackageTransitivePinningEnabled", "true");

        var packageVersions = GetPackageVersions(packageProps);
        Assert.True(packageVersions.TryGetValue("coverlet.collector", out var coverletVersion),
            "coverlet.collector must be pinned in Directory.Packages.props.");
        Assert.Equal("10.0.1", coverletVersion);
        Assert.Contains(
            GetItems(testProject, "PackageReference"),
            reference => string.Equals(GetItemInclude(reference), "coverlet.collector", StringComparison.OrdinalIgnoreCase));
        AssertProjectProperty(testProject, "IsTestProject", "true");
        AssertProjectProperty(testProject, "IsPackable", "false");
        var hostVersions = GetPinnedPackageReferences(hostProject, packageVersions);
        var testVersions = GetPinnedPackageReferences(testProject, packageVersions);
        Assert.NotEmpty(hostVersions);
        Assert.NotEmpty(testVersions);
        Assert.Single(hostVersions.Concat(testVersions).Distinct(StringComparer.Ordinal));

        var hostReferences = GetItems(hostProject, "ProjectReference");
        Assert.Empty(hostReferences);

        var testReferences = GetItems(testProject, "ProjectReference");
        var testReference = Assert.Single(testReferences);
        var referencePath = GetItemInclude(testReference)
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        var referencedPath = Path.GetFullPath(referencePath, Path.GetDirectoryName(testProjectPath)!);
        Assert.Equal(Path.GetFullPath(hostProjectPath), referencedPath);
        Assert.True(IsPathWithin(referencedPath, TemplateRoot));

        var solutionPath = TestPathUtils.PathUnder(TemplateRoot, "AppSurfaceDurableWorker.slnx");
        await using var solutionStream = File.OpenRead(solutionPath);
        var solution = await XDocument.LoadAsync(solutionStream, LoadOptions.None, CancellationToken.None);
        var solutionProjects = solution.Descendants()
            .Where(element => element.Name.LocalName == "Project")
            .Select(element => element.Attribute("Path")?.Value.Replace('\\', '/'))
            .Where(path => path is not null)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "src/AppSurfaceDurableWorker/AppSurfaceDurableWorker.csproj",
                "tests/AppSurfaceDurableWorker.Tests/AppSurfaceDurableWorker.Tests.csproj"
            }.Order(StringComparer.Ordinal),
            solutionProjects);
    }

    private static async Task<XDocument> LoadProjectAsync(string path)
    {
        Assert.True(File.Exists(path), $"Expected project file was not found at '{path}'.");
        await using var stream = File.OpenRead(path);
        return await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None);
    }

    private static IEnumerable<XElement> GetItems(XDocument project, string itemName) =>
        project.Descendants().Where(element => element.Name.LocalName == itemName);

    private static void AssertProjectProperty(XDocument project, string propertyName, string expectedValue)
    {
        var actualValue = project.Descendants()
            .Where(element => element.Name.LocalName == propertyName)
            .Select(element => element.Value.Trim())
            .LastOrDefault(value => value.Length > 0);
        Assert.Equal(expectedValue, actualValue);
    }

    private static void AssertExactInventory(string subject, IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var expectedFiles = expected.ToArray();
        var actualFiles = actual.ToArray();
        var expectedSet = expectedFiles.ToHashSet(StringComparer.Ordinal);
        var actualSet = actualFiles.ToHashSet(StringComparer.Ordinal);
        var missing = expectedSet.Except(actualSet, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unexpected = actualSet.Except(expectedSet, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var duplicates = actualFiles.GroupBy(path => path, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0 && unexpected.Length == 0 && duplicates.Length == 0,
            $"The {subject} inventory differs. Missing: [{string.Join(", ", missing)}]. "
                + $"Unexpected: [{string.Join(", ", unexpected)}]. "
                + $"Duplicates: [{string.Join(", ", duplicates)}].");
    }

    private static string GetItemInclude(XElement item)
    {
        var include = item.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName is "Include" or "Update")
            ?.Value;
        Assert.False(string.IsNullOrWhiteSpace(include), $"The '{item.Name.LocalName}' item must declare Include or Update.");
        return include!;
    }

    private static IReadOnlyDictionary<string, string> GetPackageVersions(XDocument project)
    {
        var packageVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageVersion in GetItems(project, "PackageVersion"))
        {
            var packageId = GetItemInclude(packageVersion);
            var version = packageVersion.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "Version")?.Value
                ?? packageVersion.Elements().FirstOrDefault(element => element.Name.LocalName == "Version")?.Value;

            Assert.False(string.IsNullOrWhiteSpace(version), $"Central package '{packageId}' must pin a version.");
            Assert.Matches(ExactNuGetVersionPattern, version!);
            Assert.True(packageVersions.TryAdd(packageId, version!), $"Central package '{packageId}' is declared more than once.");
        }

        Assert.NotEmpty(packageVersions);
        return packageVersions;
    }

    private static IReadOnlyList<string> GetPinnedPackageReferences(
        XDocument project,
        IReadOnlyDictionary<string, string> packageVersions)
    {
        var references = GetItems(project, "PackageReference").ToArray();
        Assert.NotEmpty(references);

        var firstPartyVersions = new List<string>();
        foreach (var reference in references)
        {
            var packageId = GetItemInclude(reference);
            var versionAttribute = reference.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "Version");
            var versionElement = reference.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "Version");
            var versionOverrideAttribute = reference.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "VersionOverride");
            var versionOverrideElement = reference.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "VersionOverride");
            Assert.Null(versionAttribute);
            Assert.Null(versionElement);
            Assert.Null(versionOverrideAttribute);
            Assert.Null(versionOverrideElement);
            Assert.True(packageVersions.TryGetValue(packageId, out var version),
                $"Package '{packageId}' must be pinned in Directory.Packages.props.");

            Assert.Matches(ExactNuGetVersionPattern, version!);

            if (packageId.StartsWith("ForgeTrust.AppSurface.", StringComparison.Ordinal))
            {
                firstPartyVersions.Add(version!);
            }
        }

        Assert.NotEmpty(firstPartyVersions);
        return firstPartyVersions;
    }

    private static async Task<JsonDocument> EvaluatePackageProjectAsync(string projectPath, string? templateContentRoot = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-getItem:Compile");
        startInfo.ArgumentList.Add("-getItem:Content");
        startInfo.ArgumentList.Add("-getItem:None");
        startInfo.ArgumentList.Add("-getProperty:PackageId");
        startInfo.ArgumentList.Add("-getProperty:PackageType");
        startInfo.ArgumentList.Add("-getProperty:IncludeBuildOutput");
        startInfo.ArgumentList.Add("-getProperty:IncludeContentInPack");
        startInfo.ArgumentList.Add("-getProperty:ContentTargetFolders");
        startInfo.ArgumentList.Add("-getProperty:IsPackable");
        if (templateContentRoot is not null)
        {
            startInfo.ArgumentList.Add($"-p:TemplateContentRoot={templateContentRoot}");
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "Could not start dotnet msbuild to evaluate the template pack project.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("MSBuild evaluation of the template pack project exceeded 30 seconds.");
        }

        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, $"MSBuild evaluation failed: {error}{Environment.NewLine}{output}");
        return JsonDocument.Parse(output);
    }

    private static bool IsPathWithin(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.StartsWith(fullRoot, comparison);
    }

    private static bool ContainsBuildArtifactDirectory(string relativePath) =>
        relativePath.Split('/').Any(segment => segment is "bin" or "obj" or "TestResults" or ".vs" or ".git");

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory is not null)
        {
            if (File.Exists(TestPathUtils.PathUnder(directory.FullName, "AGENTS.md"))
                && Directory.Exists(TestPathUtils.PathUnder(directory.FullName, "Durable")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate the source repository from '{sourcePath}'.");
    }
}
