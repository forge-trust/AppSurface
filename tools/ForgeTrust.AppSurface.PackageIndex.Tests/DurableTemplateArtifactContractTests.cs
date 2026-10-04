using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed partial class DurableTemplateArtifactContractTests : IDisposable
{
    private const string Version = "0.2.0-preview.13";
    private const string GeneratedName = "FirstDurableWorker";
    private readonly string _root;
    private readonly string _templateRoot;

    public DurableTemplateArtifactContractTests()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        _templateRoot = TestPathUtils.PathUnder(
            repositoryRoot,
            "Durable",
            "ForgeTrust.AppSurface.Durable.Templates",
            "content",
            "durable-worker");
        _root = TestPathUtils.PathUnder(TailwindTestPaths.TemporaryRoot, "durable-template-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void GeneratedOutputHasExactNativeShapeAndExcludesManifestAndBuildOutput()
    {
        var generated = CreateGeneratedRoot();
        Assert.False(File.Exists(TestPathUtils.PathUnder(generated, ".template.config", "template.json")));
        WriteFileUnder(generated, "src", GeneratedName, "bin", "build-output.dll");
        WriteFileUnder(generated, "tests", GeneratedName + ".Tests", "obj", "restore.cache");
        WriteFileUnder(generated, "tests", GeneratedName + ".Tests", "TestResults", "result.trx");

        DurableTemplateArtifactContract.ValidateGenerated(generated, GeneratedName, Version);
    }

    [Fact]
    public void GeneratedOutputRejectsUnknownFilesAndNonemptyCredentials()
    {
        var generated = CreateGeneratedRoot();
        File.WriteAllText(TestPathUtils.PathUnder(generated, ".env"), "TOKEN=do-not-ship");
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateGenerated(generated, GeneratedName, Version));

        File.Delete(TestPathUtils.PathUnder(generated, ".env"));
        File.WriteAllText(TestPathUtils.PathUnder(generated, "appsettings.json"), "{\"ActivationToken\":\"nonempty\"}");
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateGenerated(generated, GeneratedName, Version));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../outside")]
    [InlineData("/rooted/file")]
    [InlineData("folder\\file")]
    [InlineData("folder//file")]
    [InlineData("folder/./file")]
    [InlineData("folder/../file")]
    [InlineData("C:/outside/file")]
    [InlineData("NUL.txt")]
    [InlineData("folder/trailing.")]
    [InlineData("folder/trailing ")]
    [InlineData("bad:name")]
    [InlineData("cafe\u0301.txt")]
    public void ArchivePathRejectsRootTraversalAndPortableAliases(string path)
        => Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.NormalizeArchivePath(path));

    [Fact]
    public void ArchivePathAcceptsNormalizedNamesAtTheByteLimitAndRejectsLongerNames()
    {
        var atLimit = string.Join('/', new string('a', 255), new string('b', 255), new string('c', 255), new string('d', 254), "e");
        Assert.Equal(atLimit, DurableTemplateArtifactContract.NormalizeArchivePath(atLimit));
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.NormalizeArchivePath(atLimit + "f"));
    }

    [Fact]
    public void ProjectGraphRequiresCentralPinsAndContainedProjectReferences()
    {
        var graph = CopyAuthoredTree();
        DurableTemplateArtifactContract.ValidateProjectVersions(graph, Version);

        var packageProps = TestPathUtils.PathUnder(graph, "Directory.Packages.props");
        var propsDocument = XDocument.Load(packageProps);
        var durablePin = propsDocument.Descendants().Single(element =>
            element.Name.LocalName == "PackageVersion"
            && (string?)element.Attribute("Include") == "ForgeTrust.AppSurface.Durable");
        durablePin.SetAttributeValue("Version", "0.2.0-preview.12");
        propsDocument.Save(packageProps);
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateProjectVersions(graph, Version));
    }

    [Fact]
    public void ProjectGraphRequiresExactVersionForCaseInsensitiveAppSurfaceIdentity()
    {
        var graph = CopyAuthoredTree();
        var packageProps = TestPathUtils.PathUnder(graph, "Directory.Packages.props");
        var propsDocument = XDocument.Load(packageProps);
        var durablePin = propsDocument.Descendants().Single(element =>
            element.Name.LocalName == "PackageVersion"
            && (string?)element.Attribute("Include") == "ForgeTrust.AppSurface.Durable");
        durablePin.SetAttributeValue("Include", "forgetrust.appsurface.durable");
        durablePin.SetAttributeValue("Version", "0.2.0-preview.12");
        propsDocument.Save(packageProps);

        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateProjectVersions(graph, Version));
    }

    [Fact]
    public void ProjectGraphRejectsProjectLevelVersionsAndEscapingReferences()
    {
        var graph = CopyAuthoredTree();
        var appProject = TestPathUtils.PathUnder(graph, "src", "AppSurfaceDurableWorker", "AppSurfaceDurableWorker.csproj");
        var appDocument = XDocument.Load(appProject);
        appDocument.Descendants().Single(element =>
            element.Name.LocalName == "PackageReference"
            && (string?)element.Attribute("Include") == "ForgeTrust.AppSurface.Durable")
            .SetAttributeValue("Version", Version);
        appDocument.Save(appProject);
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateProjectVersions(graph, Version));

        appDocument = XDocument.Load(appProject);
        appDocument.Descendants().Single(element =>
            element.Name.LocalName == "PackageReference"
            && (string?)element.Attribute("Include") == "ForgeTrust.AppSurface.Durable")
            .Attribute("Version")!.Remove();
        appDocument.Save(appProject);
        var testProject = TestPathUtils.PathUnder(graph, "tests", "AppSurfaceDurableWorker.Tests", "AppSurfaceDurableWorker.Tests.csproj");
        var testDocument = XDocument.Load(testProject);
        testDocument.Descendants().Single(element => element.Name.LocalName == "ProjectReference")
            .SetAttributeValue("Include", "../../../../outside/Other.csproj");
        testDocument.Save(testProject);
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateProjectVersions(graph, Version));
    }

    [Fact]
    public void ExactTemplateArchivePassesAndDoesNotTreatManifestAsGeneratedContent()
    {
        var archive = NewArchivePath();
        WriteArchive(archive);

        DurableTemplateArtifactContract.ValidateArchive(archive, Version);
    }

    [Fact]
    public void TemplateArchiveAcceptsOnlyEmptyAncestorDirectoryRecords()
    {
        var valid = NewArchivePath();
        WriteArchive(valid, extraDirectories:
        [
            "content/",
            "content/durable-worker/",
            "package/",
            "package/services/",
            "package/services/metadata/",
            "package/services/metadata/core-properties/",
            "_rels/"
        ]);
        DurableTemplateArtifactContract.ValidateArchive(valid, Version);

        var unrelated = NewArchivePath();
        WriteArchive(unrelated, extraDirectories: ["content/durable-worker/not-reviewed/"]);
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(unrelated, Version));

        var nonempty = NewArchivePath();
        WriteArchive(nonempty, extraEntries: [("content/", [1])]);
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(nonempty, Version));
    }

    [Theory]
    [InlineData("unix-link")]
    [InlineData("windows-reparse-point")]
    public void TemplateArchiveRejectsLinkAndReparseEntryMetadata(string kind)
    {
        var archive = NewArchivePath();
        var attributes = kind == "unix-link"
            ? unchecked((int)(0xA000u << 16))
            : (int)FileAttributes.ReparsePoint;
        WriteArchive(archive, specialEntries:
        [
            ("content/durable-worker/untrusted-entry.txt", [1], attributes)
        ]);

        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(archive, Version));
    }

    [Theory]
    [InlineData("disableAuthorization")]
    [InlineData("skipMigration")]
    [InlineData("allowPrivilegeEscalation")]
    [InlineData("disableSqlSetup")]
    public void TemplateManifestRejectsUnsafeOptOutSymbols(string symbol)
    {
        var manifest = File.ReadAllText(TestPathUtils.PathUnder(_templateRoot, ".template.config", "template.json"));
        var withUnsafeSymbol = manifest.TrimEnd()[..^1]
            + $",\"symbols\":{{\"{symbol}\":{{\"type\":\"parameter\"}}}}}}";
        var archive = NewArchivePath();
        WriteArchive(archive, contentOverrides: new Dictionary<string, byte[]>
        {
            [".template.config/template.json"] = Encoding.UTF8.GetBytes(withUnsafeSymbol)
        });

        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(archive, Version));
    }

    [Theory]
    [InlineData("[]", false)]
    [InlineData("[{}]", true)]
    [InlineData("null", true)]
    public void TemplateManifestAcceptsOnlyAnEmptyPostActionList(string postActions, bool rejected)
    {
        var manifest = File.ReadAllText(TestPathUtils.PathUnder(_templateRoot, ".template.config", "template.json"));
        var withPostActions = manifest.TrimEnd()[..^1] + $",\"postActions\":{postActions}}}";
        var archive = NewArchivePath();
        WriteArchive(archive, contentOverrides: new Dictionary<string, byte[]>
        {
            [".template.config/template.json"] = Encoding.UTF8.GetBytes(withPostActions)
        });

        if (rejected)
        {
            Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(archive, Version));
        }
        else
        {
            DurableTemplateArtifactContract.ValidateArchive(archive, Version);
        }
    }

    [Theory]
    [InlineData("package-id")]
    [InlineData("version")]
    [InlineData("package-type")]
    [InlineData("dependency")]
    [InlineData("malformed-xml")]
    public void TemplateArchiveRejectsInvalidNuspecIdentityTypeAndRuntimeGraph(string fault)
    {
        var nuspec = fault switch
        {
            "package-id" => $"<package><metadata><id>Other.Package</id><version>{Version}</version><packageTypes><packageType name=\"Template\" /></packageTypes></metadata></package>",
            "version" => "<package><metadata><id>" + DurableTemplateArtifactContract.PackageId + "</id><version>0.2.0-preview.12</version><packageTypes><packageType name=\"Template\" /></packageTypes></metadata></package>",
            "package-type" => $"<package><metadata><id>{DurableTemplateArtifactContract.PackageId}</id><version>{Version}</version><packageTypes><packageType name=\"Dependency\" /></packageTypes></metadata></package>",
            "dependency" => $"<package><metadata><id>{DurableTemplateArtifactContract.PackageId}</id><version>{Version}</version><packageTypes><packageType name=\"Template\" /></packageTypes><dependencies><dependency id=\"Unexpected.Runtime\" version=\"1.0.0\" /></dependencies></metadata></package>",
            _ => "<package"
        };
        var archive = NewArchivePath();
        WriteArchive(archive, nuspecContent: nuspec);

        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(archive, Version));
    }

    [Theory]
    [InlineData("First-Durable-Worker")]
    [InlineData("1FirstDurableWorker")]
    [InlineData("FirstDurableWorker;extra")]
    public void GeneratedContentHashRequiresAPlainProjectIdentifier(string projectName)
    {
        var archive = NewArchivePath();
        WriteArchive(archive);

        Assert.Throws<PackageIndexException>(() =>
            DurableTemplateArtifactContract.ComputeGeneratedContentSha256(archive, projectName));
    }

    [Fact]
    public async Task GeneratedContentHashMatchesNativeTemplateExpansionByteForByte()
    {
        var archive = NewArchivePath();
        WriteArchive(archive, contentOverrides: new Dictionary<string, byte[]>
        {
            [".gitignore"] = new UTF8Encoding(false, true).GetBytes("\uFEFFAppSurfaceDurableWorker\r\n")
        });
        var generated = await GenerateWithNativeTemplateEngine(archive);
        DurableTemplateArtifactContract.ValidateGenerated(generated, GeneratedName, Version);

        var expected = DurableTemplateConsumerProof.HashGeneratedContent(generated);
        var actual = DurableTemplateArtifactContract.ComputeGeneratedContentSha256(archive, GeneratedName);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CandidateRejectsSigningEnvelopeAndReplayAllowsOnlyItsBoundedEnvelope()
    {
        var signedArchive = NewArchivePath();
        WriteArchive(signedArchive, extraEntries: [(".signature.p7s", new byte[256])]);
        var unsignedArchive = NewArchivePath();
        WriteArchive(unsignedArchive);

        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(signedArchive, Version));
        DurableTemplateArtifactContract.ValidateArchive(signedArchive, Version, allowSigningEnvelope: true);

        var unsignedContentHash = DurableTemplateArtifactContract.ComputeGeneratedContentSha256(unsignedArchive, GeneratedName);
        var signedContentHash = DurableTemplateArtifactContract.ComputeGeneratedContentSha256(
            signedArchive,
            GeneratedName,
            allowSigningEnvelope: true);
        Assert.Throws<PackageIndexException>(() =>
            DurableTemplateArtifactContract.ComputeGeneratedContentSha256(signedArchive, GeneratedName));
        Assert.Equal(unsignedContentHash, signedContentHash);
        var unsignedArchiveHash = System.Security.Cryptography.SHA512.HashData(File.ReadAllBytes(unsignedArchive));
        var signedArchiveHash = System.Security.Cryptography.SHA512.HashData(File.ReadAllBytes(signedArchive));
        Assert.False(unsignedArchiveHash.AsSpan().SequenceEqual(signedArchiveHash));
    }

    [Fact]
    public void TemplateArchiveRejectsCaseCollisionsUnexpectedFilesAndUnsafePaths()
    {
        var collision = NewArchivePath();
        WriteArchive(collision, extraEntries: [("content/durable-worker/.GITIGNORE", Encoding.UTF8.GetBytes("collision"))]);
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(collision, Version));

        var debris = NewArchivePath();
        WriteArchive(debris, extraEntries: [("content/durable-worker/extra.dll", [1, 2, 3])]);
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(debris, Version));

        var traversal = NewArchivePath();
        WriteArchive(traversal, extraEntries: [("content/durable-worker/../escape.txt", Encoding.UTF8.GetBytes("escape"))]);
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(traversal, Version));
    }

    [Fact]
    public void TemplateArchiveRejectsDuplicateManifestPropertiesAndSecretSettings()
    {
        var manifest = File.ReadAllText(TestPathUtils.PathUnder(_templateRoot, ".template.config", "template.json"));
        manifest = manifest.Replace(
            "\"shortName\": \"appsurface-durable-worker\"",
            "\"shortName\": \"appsurface-durable-worker\", \"ShortName\": \"other\"",
            StringComparison.Ordinal);
        var duplicate = NewArchivePath();
        WriteArchive(duplicate, contentOverrides: new Dictionary<string, byte[]>
        {
            [".template.config/template.json"] = Encoding.UTF8.GetBytes(manifest)
        });
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(duplicate, Version));

        var secret = NewArchivePath();
        WriteArchive(secret, contentOverrides: new Dictionary<string, byte[]>
        {
            ["appsettings.json"] = Encoding.UTF8.GetBytes("{\"Durable\":{\"RuntimeConnectionString\":\"Host=db;Password=secret\"}}")
        });
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(secret, Version));

        var nestedConnectionSecret = NewArchivePath();
        WriteArchive(nestedConnectionSecret, contentOverrides: new Dictionary<string, byte[]>
        {
            ["appsettings.json"] = Encoding.UTF8.GetBytes("{\"ConnectionStrings\":{\"Default\":\"Host=db;Password=secret\"}}")
        });
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(nestedConnectionSecret, Version));
    }

    [Fact]
    public void TemplateManifestAndSettingsEnforceJsonDepthAndByteLimits()
    {
        var manifest = File.ReadAllText(TestPathUtils.PathUnder(_templateRoot, ".template.config", "template.json"));
        var depth = manifest.TrimEnd()[..^1]
            + ",\"depth\":" + new string('[', 17) + "0" + new string(']', 17) + "}";
        var tooDeep = NewArchivePath();
        WriteArchive(tooDeep, contentOverrides: new Dictionary<string, byte[]>
        {
            [".template.config/template.json"] = Encoding.UTF8.GetBytes(depth)
        });
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(tooDeep, Version));

        var tooLarge = NewArchivePath();
        WriteArchive(tooLarge, contentOverrides: new Dictionary<string, byte[]>
        {
            ["appsettings.json"] = Encoding.UTF8.GetBytes("{\"padding\":\"" + new string('a', 1024 * 1024) + "\"}")
        });
        var exception = Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(tooLarge, Version));
        Assert.Contains("JSON size limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateArchiveRejectsPerEntryTotalInflationEntryCountAndCompressedSizeLimits()
    {
        var tooLargeEntry = NewArchivePath();
        WriteArchive(tooLargeEntry, extraEntries: [("content/durable-worker/oversized.txt", new byte[2 * 1024 * 1024 + 1])]);
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(tooLargeEntry, Version));

        var tooMuchInflation = NewArchivePath();
        var inflatedEntries = Enumerable.Range(0, 9)
            .Select(index => ($"noise/{index:D2}.txt", new byte[2 * 1024 * 1024]))
            .ToArray();
        WriteArchive(tooMuchInflation, extraEntries: inflatedEntries);
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(tooMuchInflation, Version));

        var tooManyEntries = NewArchivePath();
        using (var zip = ZipFile.Open(tooManyEntries, ZipArchiveMode.Create))
        {
            foreach (var index in Enumerable.Range(0, 257)) zip.CreateEntry($"noise/{index:D3}.txt");
        }
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(tooManyEntries, Version));

        var tooLargeArchive = NewArchivePath();
        using (var stream = new FileStream(tooLargeArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(32L * 1024 * 1024 + 1);
        }
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(tooLargeArchive, Version));
    }

    private string CreateGeneratedRoot()
    {
        var generated = TestPathUtils.PathUnder(_root, "generated", Guid.NewGuid().ToString("N"));
        CopyTree(_templateRoot, generated, generatedOutput: true);
        return generated;
    }

    private string CopyAuthoredTree()
    {
        var copy = TestPathUtils.PathUnder(_root, "authored", Guid.NewGuid().ToString("N"));
        CopyTree(_templateRoot, copy, generatedOutput: false);
        return copy;
    }

    private void CopyTree(string source, string destination, bool generatedOutput)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source).Order(StringComparer.Ordinal))
        {
            DurableTemplateStaging.RequireRegularPath(entry);
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("TestResults", StringComparison.OrdinalIgnoreCase)
                    || (generatedOutput && name.Equals(".template.config", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                var childName = generatedOutput
                    ? name.Replace(DurableTemplateArtifactContract.SourceName, GeneratedName, StringComparison.Ordinal)
                    : name;
                CopyTree(entry, TestPathUtils.PathUnder(destination, childName), generatedOutput);
                continue;
            }

            var outputName = generatedOutput
                ? name.Replace(DurableTemplateArtifactContract.SourceName, GeneratedName, StringComparison.Ordinal)
                : name;
            var outputPath = TestPathUtils.PathUnder(destination, outputName);
            var text = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(entry));
            if (generatedOutput) text = text.Replace(DurableTemplateArtifactContract.SourceName, GeneratedName, StringComparison.Ordinal);
            File.WriteAllText(outputPath, text, new UTF8Encoding(false));
        }
    }

    private string NewArchivePath() => TestPathUtils.PathUnder(_root, Guid.NewGuid().ToString("N") + ".nupkg");

    private async Task<string> GenerateWithNativeTemplateEngine(string archive)
    {
        var cliHome = TestPathUtils.PathUnder(_root, "dotnet-cli-home");
        var workingDirectory = TestPathUtils.PathUnder(_root, "native-output");
        Directory.CreateDirectory(cliHome);
        Directory.CreateDirectory(workingDirectory);
        await RunDotnet(cliHome, workingDirectory, "new", "install", archive);
        await RunDotnet(cliHome, workingDirectory, "new", DurableTemplateArtifactContract.ShortName, "-n", GeneratedName);
        return TestPathUtils.PathUnder(workingDirectory, GeneratedName);
    }

    private static async Task RunDotnet(string cliHome, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["DOTNET_CLI_HOME"] = cliHome;
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the .NET template-engine process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The .NET template-engine command exceeded its two-minute test bound.");
        }
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {output}\n{error}");
        }
    }

    private static void WriteFileUnder(string root, params string[] relativeSegments)
    {
        var path = TestPathUtils.PathUnder(root, relativeSegments);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "ignored");
    }

    private void WriteArchive(
        string path,
        IReadOnlyDictionary<string, byte[]>? contentOverrides = null,
        IReadOnlyList<(string Path, byte[] Content)>? extraEntries = null,
        IReadOnlyList<string>? extraDirectories = null,
        IReadOnlyList<(string Path, byte[] Content, int ExternalAttributes)>? specialEntries = null,
        string? nuspecContent = null)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddEntry(archive, "[Content_Types].xml", Encoding.UTF8.GetBytes("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />"));
        AddEntry(archive, "_rels/.rels", Encoding.UTF8.GetBytes("<Relationships />"));
        AddEntry(archive, DurableTemplateArtifactContract.PackageId + ".nuspec", Encoding.UTF8.GetBytes(nuspecContent ??
            $"<package><metadata><id>{DurableTemplateArtifactContract.PackageId}</id><version>{Version}</version><packageTypes><packageType name=\"Template\" /></packageTypes></metadata></package>"));
        AddEntry(archive, "README.md", Encoding.UTF8.GetBytes("Package readme"));
        AddEntry(archive, "LICENSE", Encoding.UTF8.GetBytes("Package license"));
        AddEntry(archive, $"package/services/metadata/core-properties/{Guid.NewGuid():N}.psmdcp", Encoding.UTF8.GetBytes("core properties"));

        foreach (var file in Directory.EnumerateFiles(_templateRoot, "*", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(_templateRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            var bytes = contentOverrides is not null && contentOverrides.TryGetValue(relative, out var replacement)
                ? replacement
                : File.ReadAllBytes(file);
            AddEntry(archive, "content/durable-worker/" + relative, bytes);
        }
        if (extraEntries is not null)
        {
            foreach (var (entryPath, content) in extraEntries) AddEntry(archive, entryPath, content);
        }
        if (extraDirectories is not null)
        {
            foreach (var directory in extraDirectories) archive.CreateEntry(directory);
        }
        if (specialEntries is not null)
        {
            foreach (var (entryPath, content, attributes) in specialEntries)
            {
                var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
                entry.ExternalAttributes = attributes;
                using var output = entry.Open();
                output.Write(content);
            }
        }
    }

    private static bool IsBuildOutput(string path) => path.Split(Path.DirectorySeparatorChar)
        .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("TestResults", StringComparison.OrdinalIgnoreCase));

    private static void AddEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var output = entry.Open();
        output.Write(content);
    }

    private static void RewriteDeclaredZipLength(string archivePath, string entryPath, uint declaredLength)
    {
        // The fixture writer emits classic ZIP with no archive comment. Change only the central-directory
        // declaration so the original compressed bytes exercise rejection of a mismatched inflated length.
        var bytes = File.ReadAllBytes(archivePath);
        var end = bytes.Length - 22;
        Assert.Equal(0x06054B50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end, 4)));
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end + 10, 2));
        var position = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16, 4)));
        for (var index = 0; index < entryCount; index++)
        {
            Assert.Equal(0x02014B50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position, 4)));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 28, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 30, 2));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 32, 2));
            var name = Encoding.UTF8.GetString(bytes.AsSpan(position + 46, nameLength));
            if (name == entryPath)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position + 24, 4), declaredLength);
                File.WriteAllBytes(archivePath, bytes);
                return;
            }
            position += 46 + nameLength + extraLength + commentLength;
        }
        throw new InvalidOperationException("The fixture ZIP does not contain the requested entry.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
