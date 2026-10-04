using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed partial class DurableTemplateArtifactContractTests
{
    [Theory]
    [InlineData(" ")]
    [InlineData("COM1.txt")]
    [InlineData("PRN.log")]
    [InlineData("AUX")]
    [InlineData("LPT9.data")]
    [InlineData("bad<name")]
    [InlineData("bad>name")]
    [InlineData("bad\"name")]
    [InlineData("bad|name")]
    [InlineData("bad?name")]
    [InlineData("bad*name")]
    [InlineData("bad\u0001name")]
    public void ArchivePathRejectsAdditionalPortableAliasesAndInvalidUnicode(string path)
        => Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.NormalizeArchivePath(path));

    [Fact]
    public void ArchivePathRejectsIllFormedUnicode()
    {
        var path = new string((char)0xD800, 1);
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.NormalizeArchivePath(path));
    }

    [Fact]
    public void MalformedZipReportsSafeTemplateContractFailure()
    {
        var archive = NewArchivePath();
        File.WriteAllText(archive, "private-invalid-archive-sentinel");

        var error = Assert.Throws<PackageIndexException>(() =>
            DurableTemplateArtifactContract.ValidateArchive(archive, Version));

        Assert.Contains("archive is malformed", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-invalid-archive-sentinel", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingArchiveAndProjectGraphReportContractFailures()
    {
        var archiveError = Assert.Throws<PackageIndexException>(() =>
            DurableTemplateArtifactContract.ValidateArchive(NewArchivePath(), Version));
        Assert.Contains("candidate archive is missing", archiveError.Message, StringComparison.Ordinal);

        var graphError = Assert.Throws<PackageIndexException>(() =>
            DurableTemplateArtifactContract.ValidateProjectVersions(TestPathUtils.PathUnder(_root, "missing-graph"), Version));
        Assert.Contains("root does not exist", graphError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateArchiveAcceptsEmptyAuthoredTextAndStillComputesGeneratedIdentity()
    {
        var archive = NewArchivePath();
        WriteArchive(archive, contentOverrides: new Dictionary<string, byte[]> { [".gitignore"] = [] });

        DurableTemplateArtifactContract.ValidateArchive(archive, Version);
        var digest = DurableTemplateArtifactContract.ComputeGeneratedContentSha256(archive, GeneratedName);

        Assert.Matches("^[0-9a-f]{64}$", digest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    public void TemplateArchiveRejectsOverstatedInflatedLengths(int actualLength)
    {
        var archive = NewArchivePath();
        var payload = Encoding.UTF8.GetBytes(new string(' ', actualLength));
        WriteArchive(archive, contentOverrides: new Dictionary<string, byte[]> { [".gitignore"] = payload });
        RewriteDeclaredZipLength(archive, "content/durable-worker/.gitignore", (uint)actualLength + 1);

        var error = Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(archive, Version));

        Assert.Contains("different from its ZIP declaration", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateArchiveEnforcesCumulativeInflationAcrossAllowedContentEntries()
    {
        var payload = Encoding.UTF8.GetBytes(new string(' ', 2 * 1024 * 1024));
        var overrides = Directory.EnumerateFiles(_templateRoot, "*", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .Order(StringComparer.Ordinal)
            .Take(9)
            .ToDictionary(file => Path.GetRelativePath(_templateRoot, file).Replace(Path.DirectorySeparatorChar, '/'),
                _ => payload);
        Assert.Equal(9, overrides.Count);
        var archive = NewArchivePath();
        WriteArchive(archive, contentOverrides: overrides);

        var error = Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(archive, Version));

        Assert.Contains("actual inflation limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateManifestHasItsOwnJsonByteLimitBelowTheArchiveEntryLimit()
    {
        var manifest = File.ReadAllText(TestPathUtils.PathUnder(_templateRoot, ".template.config", "template.json"));
        var archive = NewArchivePath();
        WriteArchive(archive, contentOverrides: new Dictionary<string, byte[]>
        {
            [".template.config/template.json"] = Encoding.UTF8.GetBytes(manifest + new string(' ', 1024 * 1024))
        });

        var error = Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(archive, Version));

        Assert.Contains("manifest exceeds the 1-MiB JSON limit", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, unchecked((int)0x80000000), false)]
    [InlineData(false, (int)FileAttributes.Directory, true)]
    [InlineData(false, 0x40000000, true)]
    [InlineData(false, 0x10000000, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, 0x40000000 | (int)FileAttributes.Directory, false)]
    [InlineData(true, unchecked((int)0x80000000), true)]
    public void TemplateArchiveEntryAttributesMustMatchTheDeclaredFileShape(bool directory, int attributes, bool rejected)
    {
        var archive = NewArchivePath();
        WriteArchive(archive, extraDirectories: directory ? ["content/"] : null);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            zip.GetEntry(directory ? "content/" : "content/durable-worker/.gitignore")!.ExternalAttributes = attributes;
        }

        if (rejected)
        {
            var error = Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(archive, Version));
            Assert.Contains("link or unsupported special file", error.Message, StringComparison.Ordinal);
        }
        else
        {
            DurableTemplateArtifactContract.ValidateArchive(archive, Version);
        }
    }

    [Theory]
    [InlineData("docs")]
    [InlineData("src/AppSurfaceDurableWorker/Work")]
    [InlineData("tests/AppSurfaceDurableWorker.Tests")]
    public void TemplateArchiveRejectsKnownDirectoryRecordsWithoutRemainingContent(string relativeDirectory)
    {
        var archive = NewArchivePath();
        var directoryPath = "content/durable-worker/" + relativeDirectory + "/";
        WriteArchive(archive, extraDirectories: [directoryPath]);
        DurableTemplateArtifactContract.ValidateArchive(archive, Version);

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            var descendants = zip.Entries.Where(entry => entry.FullName != directoryPath
                && entry.FullName.StartsWith(directoryPath, StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(descendants);
            foreach (var entry in descendants) entry.Delete();
        }

        var candidateError = Assert.Throws<PackageIndexException>(() =>
            DurableTemplateArtifactContract.ValidateArchive(archive, Version));
        var hashError = Assert.Throws<PackageIndexException>(() =>
            DurableTemplateArtifactContract.ComputeGeneratedContentSha256(archive, GeneratedName));

        var expectedDiagnostic = $"unexpected directory '{directoryPath.TrimEnd('/')}'";
        Assert.Contains(expectedDiagnostic, candidateError.Message, StringComparison.Ordinal);
        Assert.Contains(expectedDiagnostic, hashError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-nuspec")]
    [InlineData("missing-core-properties")]
    [InlineData("multiple-core-properties")]
    [InlineData("malformed-core-properties-name")]
    [InlineData("unexpected-nuspec-name")]
    [InlineData("missing-metadata")]
    [InlineData("missing-id")]
    [InlineData("missing-version")]
    [InlineData("missing-package-types")]
    [InlineData("multiple-package-types")]
    [InlineData("invalid-semver")]
    [InlineData("prohibited-dtd")]
    public void TemplateArchiveRejectsMalformedArchiveAndNuspecMetadata(string fault)
    {
        var archive = NewArchivePath();
        var nuspec = fault switch
        {
            "missing-metadata" => "<package />",
            "missing-id" => $"<package><metadata><version>{Version}</version><packageTypes><packageType name=\"Template\" /></packageTypes></metadata></package>",
            "missing-version" => $"<package><metadata><id>{DurableTemplateArtifactContract.PackageId}</id><packageTypes><packageType name=\"Template\" /></packageTypes></metadata></package>",
            "missing-package-types" => $"<package><metadata><id>{DurableTemplateArtifactContract.PackageId}</id><version>{Version}</version></metadata></package>",
            "multiple-package-types" => $"<package><metadata><id>{DurableTemplateArtifactContract.PackageId}</id><version>{Version}</version><packageTypes><packageType name=\"Template\" /><packageType name=\"Dependency\" /></packageTypes></metadata></package>",
            "invalid-semver" => $"<package><metadata><id>{DurableTemplateArtifactContract.PackageId}</id><version>latest</version><packageTypes><packageType name=\"Template\" /></packageTypes></metadata></package>",
            "prohibited-dtd" => $"<!DOCTYPE package [<!ENTITY name \"unsafe\">]><package><metadata><id>&name;</id><version>{Version}</version><packageTypes><packageType name=\"Template\" /></packageTypes></metadata></package>",
            _ => null
        };
        IReadOnlyList<(string Path, byte[] Content)>? extraEntries = fault switch
        {
            "multiple-core-properties" =>
            [
                ("package/services/metadata/core-properties/second.psmdcp", Encoding.UTF8.GetBytes("second"))
            ],
            "malformed-core-properties-name" =>
            [
                ("package/services/metadata/core-properties/metadata.txt", Encoding.UTF8.GetBytes("malformed"))
            ],
            "unexpected-nuspec-name" =>
            [
                ("package/services/metadata/core-properties/unexpected.nuspec", Encoding.UTF8.GetBytes("<package />"))
            ],
            _ => null
        };

        WriteArchive(archive, nuspecContent: nuspec, extraEntries: extraEntries);
        if (fault is "missing-nuspec" or "missing-core-properties")
        {
            using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
            var entry = fault == "missing-nuspec"
                ? zip.GetEntry(DurableTemplateArtifactContract.PackageId + ".nuspec")
                : zip.Entries.Single(candidate => candidate.FullName.StartsWith(
                    "package/services/metadata/core-properties/",
                    StringComparison.Ordinal));
            entry!.Delete();
        }

        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(archive, Version));
    }

    [Theory]
    [InlineData("root-null")]
    [InlineData("root-array")]
    [InlineData("identity-missing")]
    [InlineData("identity-not-string")]
    [InlineData("short-name-empty")]
    [InlineData("source-name-wrong")]
    [InlineData("prefer-name-directory-false")]
    [InlineData("tags-not-object")]
    [InlineData("language-missing")]
    [InlineData("language-wrong")]
    [InlineData("extra-tag")]
    [InlineData("classifications-not-array")]
    [InlineData("classification-not-string")]
    [InlineData("classification-wrong-set")]
    [InlineData("classification-duplicate")]
    [InlineData("primary-output-empty")]
    [InlineData("primary-output-not-object")]
    [InlineData("primary-output-wrong-path")]
    [InlineData("primary-output-extra")]
    [InlineData("post-actions-not-array")]
    public void TemplateManifestRejectsMalformedOrUnreviewedShape(string fault)
    {
        var archive = NewArchivePath();
        var manifest = CreateManifestVariant(fault);
        WriteArchive(archive, contentOverrides: new Dictionary<string, byte[]>
        {
            [".template.config/template.json"] = Encoding.UTF8.GetBytes(manifest)
        });

        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateArchive(archive, Version));
    }

    [Theory]
    [InlineData("symbols-null")]
    [InlineData("benign-symbol")]
    public void TemplateManifestAcceptsNonOptOutSymbols(string variant)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(TestPathUtils.PathUnder(_templateRoot, ".template.config", "template.json")))!.AsObject();
        manifest["symbols"] = variant == "symbols-null"
            ? JsonValue.Create("disabled")
            : JsonNode.Parse("{\"includeDocs\":{\"type\":\"parameter\"}}");
        var archive = NewArchivePath();
        WriteArchive(archive, contentOverrides: new Dictionary<string, byte[]>
        {
            [".template.config/template.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString())
        });

        DurableTemplateArtifactContract.ValidateArchive(archive, Version);
    }

    [Theory]
    [InlineData("$" + "{TOKEN}", false)]
    [InlineData(" <TOKEN> ", false)]
    [InlineData("$" + "{TOKEN}}", true)]
    [InlineData("<TOKEN>>", true)]
    [InlineData("$" + "{{TOKEN}}", true)]
    [InlineData("<A<B>>", true)]
    [InlineData("$" + "{TOKEN", true)]
    [InlineData("<TOKEN", true)]
    [InlineData("$" + "{TO{KEN}", true)]
    [InlineData("<TO<KEN>", true)]
    public void TemplateSettingsAcceptOnlyCompleteSecretPlaceholders(string value, bool rejected)
    {
        var settings = $"{{\"ActivationToken\":{System.Text.Json.JsonSerializer.Serialize(value)}}}";
        var archive = NewArchivePath();
        WriteArchive(archive, contentOverrides: new Dictionary<string, byte[]>
        {
            ["appsettings.json"] = Encoding.UTF8.GetBytes(settings)
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
    [InlineData("{\"Nested\":[{\"ApiKey\":\"$" + "{TOKEN}\"}]}", false)]
    [InlineData("{\"Nested\":[{\"ApiKey\":\"literal\"}]}", true)]
    [InlineData("{\"Nested\":[null,false,7,\"\"]}", false)]
    public void TemplateSettingsValidateSecretValuesInsideArrays(string settings, bool rejected)
    {
        var archive = NewArchivePath();
        WriteArchive(archive, contentOverrides: new Dictionary<string, byte[]>
        {
            ["appsettings.json"] = Encoding.UTF8.GetBytes(settings)
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

    [Fact]
    public void GeneratedValidationRejectsMissingRootsAndInvalidProjectNames()
    {
        var missing = TestPathUtils.PathUnder(_root, "missing-generated-root");
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateGenerated(missing, GeneratedName, Version));

        var generated = CreateGeneratedRoot();
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateGenerated(generated, "not-a-project", Version));
    }

    [Theory]
    [InlineData("source-name-retained")]
    [InlineData("invalid-utf8")]
    [InlineData("nul-byte")]
    [InlineData("malformed-settings")]
    public void GeneratedOutputRejectsMalformedAuthoredText(string fault)
    {
        var generated = CreateGeneratedRoot();
        var readme = TestPathUtils.PathUnder(generated, "README.md");
        switch (fault)
        {
            case "source-name-retained":
                File.AppendAllText(readme, DurableTemplateArtifactContract.SourceName);
                break;
            case "invalid-utf8":
                File.WriteAllBytes(readme, [0xFF]);
                break;
            case "nul-byte":
                File.WriteAllBytes(readme, Encoding.UTF8.GetBytes("text\0with-nul"));
                break;
            case "malformed-settings":
                File.WriteAllText(TestPathUtils.PathUnder(generated, "appsettings.json"), "{\"Durable\":");
                break;
        }

        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateGenerated(generated, GeneratedName, Version));
    }

    [Theory]
    [InlineData("file-count")]
    [InlineData("per-file-size")]
    [InlineData("total-size")]
    public void GeneratedOutputEnforcesAuthoredFileAndByteBounds(string fault)
    {
        var generated = CreateGeneratedRoot();
        var extraRoot = TestPathUtils.PathUnder(generated, "unreviewed");
        Directory.CreateDirectory(extraRoot);
        switch (fault)
        {
            case "file-count":
                foreach (var index in Enumerable.Range(0, 257))
                {
                    File.WriteAllText(TestPathUtils.PathUnder(extraRoot, $"{index:D3}.txt"), string.Empty);
                }
                break;
            case "per-file-size":
                File.WriteAllBytes(TestPathUtils.PathUnder(extraRoot, "large.txt"), new byte[2 * 1024 * 1024 + 1]);
                break;
            case "total-size":
                foreach (var index in Enumerable.Range(0, 9))
                {
                    File.WriteAllBytes(TestPathUtils.PathUnder(extraRoot, $"{index:D2}.txt"), new byte[2 * 1024 * 1024]);
                }
                break;
        }

        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateGenerated(generated, GeneratedName, Version));
    }

    [Theory]
    [InlineData("missing-central-file")]
    [InlineData("manager-missing")]
    [InlineData("manager-false")]
    [InlineData("manager-conditioned")]
    [InlineData("manager-duplicate")]
    [InlineData("missing-coordinated-pin")]
    [InlineData("extra-coordinated-pin")]
    [InlineData("duplicate-pin")]
    [InlineData("dynamic-pin")]
    [InlineData("missing-pin-id")]
    [InlineData("missing-pin-version")]
    [InlineData("pin-update")]
    [InlineData("semicolon-pin-id")]
    [InlineData("conditional-pin")]
    [InlineData("pin-child-version")]
    [InlineData("invalid-third-party-version")]
    [InlineData("pin-owned-by-other-props")]
    [InlineData("malformed-root-xml")]
    [InlineData("conditional-pin-group")]
    [InlineData("pin-version-override-element")]
    public void ProjectGraphRejectsMalformedCentralPackageConfiguration(string fault)
    {
        var graph = CopyAuthoredTree();
        var centralPath = TestPathUtils.PathUnder(graph, "Directory.Packages.props");
        if (fault == "missing-central-file")
        {
            File.Delete(centralPath);
        }
        else if (fault == "malformed-root-xml")
        {
            File.WriteAllText(centralPath, "<Project");
        }
        else if (fault == "pin-owned-by-other-props")
        {
            var buildProps = TestPathUtils.PathUnder(graph, "Directory.Build.props");
            var document = XDocument.Load(buildProps);
            var pin = LoadCentralPackageVersions(centralPath).Single(element =>
                element.Attribute("Include")?.Value == "ForgeTrust.AppSurface.Durable");
            document.Root!.Add(new XElement(pin));
            document.Save(buildProps);
        }
        else
        {
            var document = XDocument.Load(centralPath);
            var management = document.Descendants().Single(element => element.Name.LocalName == "ManagePackageVersionsCentrally");
            var pins = document.Descendants().Where(element => element.Name.LocalName == "PackageVersion").ToArray();
            var durablePin = pins.Single(element => element.Attribute("Include")?.Value == "ForgeTrust.AppSurface.Durable");
            switch (fault)
            {
                case "manager-missing":
                    management.Remove();
                    break;
                case "manager-false":
                    management.Value = "false";
                    break;
                case "manager-conditioned":
                    management.SetAttributeValue("Condition", "'$(EnableCentral)' == 'true'");
                    break;
                case "manager-duplicate":
                    management.Parent!.Add(new XElement(management));
                    break;
                case "missing-coordinated-pin":
                    durablePin.Remove();
                    break;
                case "extra-coordinated-pin":
                    durablePin.Parent!.Add(new XElement(durablePin.Name,
                        new XAttribute("Include", "ForgeTrust.AppSurface.Extra"),
                        new XAttribute("Version", Version)));
                    break;
                case "duplicate-pin":
                    durablePin.Parent!.Add(new XElement(durablePin));
                    break;
                case "dynamic-pin":
                    durablePin.SetAttributeValue("Include", "$(PackageId)");
                    break;
                case "missing-pin-id":
                    durablePin.Attribute("Include")!.Remove();
                    break;
                case "missing-pin-version":
                    durablePin.Attribute("Version")!.Remove();
                    break;
                case "pin-update":
                    durablePin.SetAttributeValue("Update", "ForgeTrust.AppSurface.Durable");
                    break;
                case "semicolon-pin-id":
                    durablePin.SetAttributeValue("Include", "ForgeTrust.AppSurface.Durable;Other.Package");
                    break;
                case "conditional-pin":
                    durablePin.SetAttributeValue("Condition", "'$(Pin)' == 'true'");
                    break;
                case "pin-child-version":
                    durablePin.Add(new XElement(durablePin.Name.Namespace + "Version", Version));
                    break;
                case "conditional-pin-group":
                    durablePin.Parent!.SetAttributeValue("Condition", "'$(PinGroup)' == 'true'");
                    break;
                case "pin-version-override-element":
                    durablePin.Add(new XElement(durablePin.Name.Namespace + "VersionOverride", Version));
                    break;
                case "invalid-third-party-version":
                    pins.First(element => element.Attribute("Include")?.Value.StartsWith("ForgeTrust.AppSurface.", StringComparison.OrdinalIgnoreCase) != true)
                        .SetAttributeValue("Version", "latest");
                    break;
            }
            document.Save(centralPath);
        }

        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateProjectVersions(graph, Version));
    }

    [Fact]
    public void ProjectGraphRejectsNoProjectsAndMalformedProjectXml()
    {
        var noProjects = CopyAuthoredTree();
        foreach (var project in Directory.EnumerateFiles(noProjects, "*.csproj", SearchOption.AllDirectories))
        {
            File.Delete(project);
        }
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateProjectVersions(noProjects, Version));

        var malformed = CopyAuthoredTree();
        File.WriteAllText(TestPathUtils.PathUnder(malformed, "src", "AppSurfaceDurableWorker", "AppSurfaceDurableWorker.csproj"), "<Project");
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateProjectVersions(malformed, Version));
    }

    [Fact]
    public void ProjectGraphRejectsImportsAndConditionalReferenceGroups()
    {
        var imported = CopyAuthoredTree();
        var appProject = TestPathUtils.PathUnder(imported, "src", "AppSurfaceDurableWorker", "AppSurfaceDurableWorker.csproj");
        var appDocument = XDocument.Load(appProject);
        appDocument.Root!.Add(new XElement(appDocument.Root.Name.Namespace + "Import", new XAttribute("Project", "../outside.props")));
        appDocument.Save(appProject);
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateProjectVersions(imported, Version));

        var conditional = CopyAuthoredTree();
        var testProject = TestPathUtils.PathUnder(conditional, "tests", "AppSurfaceDurableWorker.Tests", "AppSurfaceDurableWorker.Tests.csproj");
        var testDocument = XDocument.Load(testProject);
        var projectReference = testDocument.Descendants().Single(element => element.Name.LocalName == "ProjectReference");
        projectReference.Parent!.SetAttributeValue("Condition", "'$(IncludeProject)' == 'true'");
        testDocument.Save(testProject);
        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateProjectVersions(conditional, Version));
    }

    [Theory]
    [InlineData("missing-id")]
    [InlineData("dynamic-id")]
    [InlineData("include-and-update")]
    [InlineData("condition")]
    [InlineData("duplicate")]
    [InlineData("version-override-attribute")]
    [InlineData("version-override-element")]
    [InlineData("no-central-pin")]
    public void ProjectGraphRejectsMalformedOrUnpinnedPackageReferences(string fault)
    {
        var graph = CopyAuthoredTree();
        var projectPath = TestPathUtils.PathUnder(graph, "src", "AppSurfaceDurableWorker", "AppSurfaceDurableWorker.csproj");
        var document = XDocument.Load(projectPath);
        var reference = document.Descendants().Single(element =>
            element.Name.LocalName == "PackageReference"
            && element.Attribute("Include")?.Value == "ForgeTrust.AppSurface.Durable");
        var id = reference.Attribute("Include")!.Value;
        switch (fault)
        {
            case "missing-id":
                reference.Attribute("Include")!.Remove();
                break;
            case "dynamic-id":
                reference.SetAttributeValue("Include", "$(PackageId)");
                break;
            case "include-and-update":
                reference.SetAttributeValue("Update", id);
                break;
            case "condition":
                reference.SetAttributeValue("Condition", "'$(IncludePackage)' == 'true'");
                break;
            case "duplicate":
                reference.Parent!.Add(new XElement(reference));
                break;
            case "version-override-attribute":
                reference.SetAttributeValue("VersionOverride", Version);
                break;
            case "version-override-element":
                reference.Add(new XElement(reference.Name.Namespace + "VersionOverride", Version));
                break;
            case "no-central-pin":
                reference.SetAttributeValue("Include", "Unpinned.Package");
                break;
        }
        document.Save(projectPath);

        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateProjectVersions(graph, Version));
    }

    [Fact]
    public void ProjectGraphAcceptsCentralOnlyPackageReferenceUpdates()
    {
        var graph = CopyAuthoredTree();
        var projectPath = TestPathUtils.PathUnder(graph, "src", "AppSurfaceDurableWorker", "AppSurfaceDurableWorker.csproj");
        var document = XDocument.Load(projectPath);
        var reference = document.Descendants().Single(element =>
            element.Name.LocalName == "PackageReference"
            && element.Attribute("Include")?.Value == "ForgeTrust.AppSurface.Durable");
        var id = reference.Attribute("Include")!.Value;
        reference.Attribute("Include")!.Remove();
        reference.SetAttributeValue("Update", id);
        document.Save(projectPath);

        DurableTemplateArtifactContract.ValidateProjectVersions(graph, Version);
    }

    [Fact]
    public void ProjectGraphAcceptsNonVersionPackageMetadataAndRootLevelProjectReferences()
    {
        var graph = CopyAuthoredTree();
        var centralPath = TestPathUtils.PathUnder(graph, "Directory.Packages.props");
        var central = XDocument.Load(centralPath);
        central.Descendants().First(element => element.Name.LocalName == "PackageVersion")
            .Add(new XElement("PrivateAssets", "all"));
        central.Save(centralPath);

        var nestedProject = TestPathUtils.PathUnder(graph, "tests", "AppSurfaceDurableWorker.Tests", "AppSurfaceDurableWorker.Tests.csproj");
        var document = XDocument.Load(nestedProject);
        document.Descendants().Single(element => element.Name.LocalName == "ProjectReference")
            .SetAttributeValue("Include", "src/AppSurfaceDurableWorker/AppSurfaceDurableWorker.csproj");
        var rootProject = TestPathUtils.PathUnder(graph, "Root.Tests.csproj");
        document.Save(rootProject);
        File.Delete(nestedProject);

        DurableTemplateArtifactContract.ValidateProjectVersions(graph, Version);
    }

    [Theory]
    [InlineData("rooted")]
    [InlineData("backslash")]
    [InlineData("wrong-extension")]
    [InlineData("dynamic")]
    [InlineData("condition")]
    [InlineData("duplicate")]
    [InlineData("missing-target")]
    [InlineData("semicolon")]
    [InlineData("wildcard")]
    [InlineData("missing-include")]
    [InlineData("single-character-wildcard")]
    public void ProjectGraphRejectsMalformedOrUnresolvedProjectReferences(string fault)
    {
        var graph = CopyAuthoredTree();
        var projectPath = TestPathUtils.PathUnder(graph, "tests", "AppSurfaceDurableWorker.Tests", "AppSurfaceDurableWorker.Tests.csproj");
        var document = XDocument.Load(projectPath);
        var reference = document.Descendants().Single(element => element.Name.LocalName == "ProjectReference");
        switch (fault)
        {
            case "missing-include":
                reference.Attribute("Include")!.Remove();
                break;
            case "rooted":
                reference.SetAttributeValue("Include", "/outside/Other.csproj");
                break;
            case "backslash":
                reference.SetAttributeValue("Include", @"..\..\src\AppSurfaceDurableWorker\AppSurfaceDurableWorker.csproj");
                break;
            case "wrong-extension":
                reference.SetAttributeValue("Include", "../../src/AppSurfaceDurableWorker/Program.cs");
                break;
            case "dynamic":
                reference.SetAttributeValue("Include", "$(ProjectPath).csproj");
                break;
            case "semicolon":
                reference.SetAttributeValue("Include", "../../src/AppSurfaceDurableWorker/AppSurfaceDurableWorker.csproj;../../src/Other/Other.csproj");
                break;
            case "wildcard":
                reference.SetAttributeValue("Include", "../../src/AppSurfaceDurableWorker/*.csproj");
                break;
            case "single-character-wildcard":
                reference.SetAttributeValue("Include", "../../src/AppSurfaceDurableWorker/AppSurfaceDurableWorke?.csproj");
                break;
            case "condition":
                reference.SetAttributeValue("Condition", "'$(IncludeProject)' == 'true'");
                break;
            case "duplicate":
                reference.Parent!.Add(new XElement(reference));
                break;
            case "missing-target":
                reference.SetAttributeValue("Include", "../../src/Missing/Missing.csproj");
                break;
        }
        document.Save(projectPath);

        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateProjectVersions(graph, Version));
    }

    [Theory]
    [InlineData("file-count")]
    [InlineData("per-file-size")]
    [InlineData("total-size")]
    public void ProjectGraphEnforcesAuthoredFileAndByteBounds(string fault)
    {
        var graph = CopyAuthoredTree();
        var extraRoot = TestPathUtils.PathUnder(graph, "unreviewed");
        Directory.CreateDirectory(extraRoot);
        switch (fault)
        {
            case "file-count":
                foreach (var index in Enumerable.Range(0, 257))
                {
                    File.WriteAllText(TestPathUtils.PathUnder(extraRoot, $"{index:D3}.txt"), string.Empty);
                }
                break;
            case "per-file-size":
                File.WriteAllBytes(TestPathUtils.PathUnder(extraRoot, "large.txt"), new byte[2 * 1024 * 1024 + 1]);
                break;
            case "total-size":
                foreach (var index in Enumerable.Range(0, 9))
                {
                    File.WriteAllBytes(TestPathUtils.PathUnder(extraRoot, $"{index:D2}.txt"), new byte[2 * 1024 * 1024]);
                }
                break;
        }

        Assert.Throws<PackageIndexException>(() => DurableTemplateArtifactContract.ValidateProjectVersions(graph, Version));
    }

    private string CreateManifestVariant(string fault)
    {
        var document = JsonNode.Parse(File.ReadAllText(TestPathUtils.PathUnder(_templateRoot, ".template.config", "template.json")));
        if (fault == "root-null") return "null";
        if (fault == "root-array") return "[]";

        var root = document!.AsObject();
        switch (fault)
        {
            case "identity-missing":
                root.Remove("identity");
                break;
            case "identity-not-string":
                root["identity"] = 123;
                break;
            case "short-name-empty":
                root["shortName"] = " ";
                break;
            case "source-name-wrong":
                root["sourceName"] = "DifferentWorker";
                break;
            case "prefer-name-directory-false":
                root["preferNameDirectory"] = false;
                break;
            case "tags-not-object":
                root["tags"] = "C#";
                break;
            case "language-missing":
                root["tags"]!.AsObject().Remove("language");
                break;
            case "language-wrong":
                root["tags"]!["language"] = "F#";
                break;
            case "extra-tag":
                root["tags"]!["framework"] = "worker";
                break;
            case "classifications-not-array":
                root["classifications"] = "Durable";
                break;
            case "classification-not-string":
                root["classifications"] = JsonNode.Parse("[\"AppSurface\",\"Durable\",4,\"PostgreSQL\"]");
                break;
            case "classification-wrong-set":
                root["classifications"] = JsonNode.Parse("[\"AppSurface\",\"Durable\",\"Worker\",\"Other\"]");
                break;
            case "classification-duplicate":
                root["classifications"] = JsonNode.Parse("[\"AppSurface\",\"Durable\",\"Worker\",\"Worker\"]");
                break;
            case "primary-output-empty":
                root["primaryOutputs"] = JsonNode.Parse("[]");
                break;
            case "primary-output-not-object":
                root["primaryOutputs"] = JsonNode.Parse("[null]");
                break;
            case "primary-output-wrong-path":
                root["primaryOutputs"] = JsonNode.Parse("[{\"path\":\"README.md\"}]");
                break;
            case "primary-output-extra":
                root["primaryOutputs"] = JsonNode.Parse("[{\"path\":\"src/AppSurfaceDurableWorker/AppSurfaceDurableWorker.csproj\"},{\"path\":\"README.md\"}]");
                break;
            case "post-actions-not-array":
                root["postActions"] = false;
                break;
        }

        return document.ToJsonString();
    }

    private static XElement[] LoadCentralPackageVersions(string path)
        => XDocument.Load(path).Descendants().Where(element => element.Name.LocalName == "PackageVersion").ToArray();
}
