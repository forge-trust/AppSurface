using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using FakeItEasy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Config.Tests;

public class FileBasedConfigProviderTests
{
    [Fact]
    public void Resolve_UsesColonLogicalKeysAndPreservesLiteralDots()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.json"),
                "{\"Feature:Enabled\":true,\"Feature\":{\"Enabled\":false},\"Feature.Name\":\"literal\"}");

            var provider = CreateProvider(tempDir);
            var nested = provider.Resolve<bool>(new ConfigProviderRequest(
                Environments.Production,
                AppSurfaceConfigKey.Parse("Feature:Enabled")));
            var literal = provider.Resolve<string>(new ConfigProviderRequest(
                Environments.Production,
                AppSurfaceConfigKey.Parse("Feature.Name")));

            Assert.Equal(ConfigProviderValueStatus.Found, nested.Status);
            Assert.False(nested.Value);
            Assert.Equal(ConfigProviderValueStatus.Found, literal.Status);
            Assert.Equal("literal", literal.Value);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_TerminatesCollisionDomainButKeepsUnrelatedSiblingsAvailable()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.json"),
                "{\"Feature\":{\"Port\":1,\"port\":2,\"Name\":\"ok\"},\"Other\":3}");

            var provider = CreateProvider(tempDir);
            var collision = provider.Resolve<int>(new ConfigProviderRequest(
                Environments.Production,
                AppSurfaceConfigKey.Parse("Feature:Port")));
            var ancestor = provider.Resolve<Dictionary<string, int>>(new ConfigProviderRequest(
                Environments.Production,
                AppSurfaceConfigKey.Parse("Feature")));
            var sibling = provider.Resolve<string>(new ConfigProviderRequest(
                Environments.Production,
                AppSurfaceConfigKey.Parse("Feature:Name")));
            var unrelated = provider.Resolve<int>(new ConfigProviderRequest(
                Environments.Production,
                AppSurfaceConfigKey.Parse("Other")));

            Assert.Equal(ConfigProviderValueStatus.Terminal, collision.Status);
            Assert.Equal(ConfigProviderValueStatus.Terminal, ancestor.Status);
            Assert.Equal(ConfigProviderValueStatus.Found, sibling.Status);
            Assert.Equal("ok", sibling.Value);
            Assert.Equal(ConfigProviderValueStatus.Found, unrelated.Status);
            Assert.Equal(3, unrelated.Value);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_TreatsNullAsMissingAndSupportsReplacementShapes()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Join(tempDir, "appsettings.json"), "{\"Value\":null,\"Replace\":{\"Child\":true},\"Items\":[1,2]}");
            File.WriteAllText(Path.Join(tempDir, "config_override.json"), "{\"Replace\":7,\"Items\":{\"Child\":true}}");

            var provider = CreateProvider(tempDir);
            var missing = provider.Resolve<string>(new ConfigProviderRequest(
                Environments.Production,
                AppSurfaceConfigKey.Parse("Value")));
            var scalar = provider.Resolve<int>(new ConfigProviderRequest(
                Environments.Production,
                AppSurfaceConfigKey.Parse("Replace")));
            var objectValue = provider.Resolve<Dictionary<string, bool>>(new ConfigProviderRequest(
                Environments.Production,
                AppSurfaceConfigKey.Parse("Items")));

            Assert.Equal(ConfigProviderValueStatus.Missing, missing.Status);
            Assert.Equal(ConfigProviderValueStatus.Found, scalar.Status);
            Assert.Equal(7, scalar.Value);
            Assert.Equal(ConfigProviderValueStatus.Found, objectValue.Status);
            Assert.True(objectValue.Value!["Child"]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TokenProjection_TerminatesRepresentableAncestorsForInvalidSegments()
    {
        var projection = ConfigFileTokenProjection.Parse(
            System.Text.Encoding.UTF8.GetBytes("{\"Feature\":{\"Bad:Segment\":true,\"Name\":\"ok\"}}"));

        Assert.Contains(AppSurfaceConfigKey.Parse("Feature"), projection.TerminalKeys);
        Assert.DoesNotContain(AppSurfaceConfigKey.Parse("Feature:Name"), projection.TerminalKeys);
        Assert.Contains(AppSurfaceConfigKey.Parse("Feature:Name"), projection.Entries.Keys);
    }

    [Fact]
    public void TokenProjection_RejectsTrailingTokensAfterTheRoot()
    {
        Assert.ThrowsAny<JsonException>(() => ConfigFileTokenProjection.Parse(
            System.Text.Encoding.UTF8.GetBytes("{} {}")));
    }

    [Fact]
    public void GetValue_MergesFilesByEnvironmentAndPriority()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.json"),
                """{"Feature":{"Enabled":false,"Name":"Prod"}}""");
            File.WriteAllText(
                Path.Join(tempDir, "config_extra.json"),
                """{"Feature":{"Extra":"ProdExtra"}}""");
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.Development.json"),
                """{"Feature":{"Name":"Dev"}}""");
            File.WriteAllText(
                Path.Join(tempDir, "config_extra.Development.json"),
                """{"Feature":{"Enabled":true,"Extra":"Value"}}""");
            File.WriteAllText(Path.Join(tempDir, "config_bad.json"), "{not json}");

            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

            A.CallTo(() => locationProvider.Directory).Returns(tempDir);

            var provider = new FileBasedConfigProvider(locationProvider, logger);

            Assert.Equal("Dev", provider.Resolve<string>(new ConfigProviderRequest("Development", AppSurfaceConfigKey.Parse("Feature:Name"))).Value);
            Assert.True(provider.Resolve<bool>(new ConfigProviderRequest("Development", AppSurfaceConfigKey.Parse("Feature:Enabled"))).Value);
            Assert.Equal("Value", provider.Resolve<string>(new ConfigProviderRequest("Development", AppSurfaceConfigKey.Parse("Feature:Extra"))).Value);
            Assert.False(provider.Resolve<bool>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Feature:Enabled"))).Value);
            Assert.Equal("ProdExtra", provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Feature:Extra"))).Value);
            Assert.Null(provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Feature:Unknown"))).Value);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void GetValue_ReturnsDefaultWhenDirectoryMissing()
    {
        var locationProvider = A.Fake<IConfigFileLocationProvider>();
        var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

        A.CallTo(() => locationProvider.Directory)
            .Returns(CreateTempDirectoryPath());

        var provider = new FileBasedConfigProvider(locationProvider, logger);

        Assert.Null(provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Any.Key"))).Value);
    }

    [Fact]
    public void GetValue_ReusesCachedConfigurationAfterInitialization()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);

        try
        {
            var configPath = Path.Join(tempDir, "appsettings.json");
            File.WriteAllText(configPath, """{"Feature":{"Enabled":true}}""");

            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

            A.CallTo(() => locationProvider.Directory).Returns(tempDir);

            var provider = new FileBasedConfigProvider(locationProvider, logger);

            Assert.True(provider.Resolve<bool>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Feature:Enabled"))).Value);

            File.WriteAllText(configPath, """{"Feature":{"Enabled":false}}""");

            Assert.True(provider.Resolve<bool>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Feature:Enabled"))).Value);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void GetValue_IgnoresInvalidJsonContent()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);

        try
        {
            // Valid file
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.json"),
                """{"Feature":{"Enabled":true}}""");

            // Invalid JSON
            File.WriteAllText(
                Path.Join(tempDir, "config_broken.json"),
                """{"Feature": { "Enabled": } }"""); // Syntax error

            // Non-object root
            File.WriteAllText(
                Path.Join(tempDir, "config_array.json"),
                """[1, 2, 3]""");

            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

            A.CallTo(() => locationProvider.Directory).Returns(tempDir);

            var provider = new FileBasedConfigProvider(locationProvider, logger);

            // Should still read valid file and ignore others
            Assert.True(provider.Resolve<bool>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Feature:Enabled"))).Value);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void GetValue_IgnoresNullValuesInMerge()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.json"),
                """{"Feature":{"Enabled":true}}""");

            File.WriteAllText(
                Path.Join(tempDir, "config_override.json"),
                """{"Feature":{"Enabled":null}}""");

            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

            A.CallTo(() => locationProvider.Directory).Returns(tempDir);

            var provider = new FileBasedConfigProvider(locationProvider, logger);

            // Null in override should not trigger overwrite
            Assert.True(provider.Resolve<bool>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Feature:Enabled"))).Value);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void Resolve_ReturnsTerminalOnDeserializationFailure()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.json"),
                """{"Feature":{"Count":"NotANumber"}}""");

            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

            A.CallTo(() => locationProvider.Directory).Returns(tempDir);

            var provider = new FileBasedConfigProvider(locationProvider, logger);

            var result = provider.Resolve<int>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Feature:Count")));

            Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
            Assert.NotNull(result.Diagnostic);
            Assert.Equal(0, result.Value);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void Initialize_ParsesEnvironmentFromVariousFilePatterns()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.Staging.json"),
                """{"Env":"Staging"}""");
            File.WriteAllText(
                Path.Join(tempDir, "config_Feature.Development.json"),
                """{"Env":"Dev"}""");
            File.WriteAllText(
                Path.Join(tempDir, "config_Base.json"),
                """{"Env":"Base"}""");

            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

            A.CallTo(() => locationProvider.Directory).Returns(tempDir);

            var provider = new FileBasedConfigProvider(locationProvider, logger);

            Assert.Equal("Staging", provider.Resolve<string>(new ConfigProviderRequest("Staging", AppSurfaceConfigKey.Parse("Env"))).Value);
            Assert.Equal("Dev", provider.Resolve<string>(new ConfigProviderRequest("Development", AppSurfaceConfigKey.Parse("Env"))).Value);
            Assert.Equal("Base", provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Env"))).Value);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void GetValue_BindsNestedObjects()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.json"),
                """
                {
                  "App": {
                    "Settings": {
                      "RetryCount": 3,
                      "Endpoints": ["http://a.com", "http://b.com"]
                    }
                  }
                }
                """);

            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();
            A.CallTo(() => locationProvider.Directory).Returns(tempDir);

            var provider = new FileBasedConfigProvider(locationProvider, logger);

            Assert.Equal(3, provider.Resolve<int>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("App:Settings:RetryCount"))).Value);
            var endpoints = provider.Resolve<string[]>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("App:Settings:Endpoints"))).Value;
            Assert.NotNull(endpoints);
            Assert.Equal(2, endpoints.Length);
            Assert.Equal("http://a.com", endpoints[0]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void GetValue_ReturnsDeepClonedObjects()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.json"),
                """{"List":["a","b"]}""");

            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();
            A.CallTo(() => locationProvider.Directory).Returns(tempDir);

            var provider = new FileBasedConfigProvider(locationProvider, logger);

            var list1 = provider.Resolve<List<string>>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("List"))).Value;
            Assert.NotNull(list1);
            list1.Add("c");

            var list2 = provider.Resolve<List<string>>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("List"))).Value;
            Assert.NotNull(list2);

            // list2 should not contain "c" because list1 was a clone
            Assert.Equal(2, list2.Count);
            Assert.DoesNotContain("c", list2);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void Initialize_HandlesMissingDirectory()
    {
        var locationProvider = A.Fake<IConfigFileLocationProvider>();
        var logger = A.Fake<ILogger<FileBasedConfigProvider>>();
        A.CallTo(() => locationProvider.Directory).Returns("/non/existent/path/that/should/not/exist");

        var provider = new FileBasedConfigProvider(locationProvider, logger);

        // Should not throw, should just log and have no configs
        Assert.Null(provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Any"))).Value);
    }

    [Fact]
    public void Merge_OverwritesNonObjectWithObject()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Join(tempDir, "appsettings.json"), """{"Key": "string"}""");
            File.WriteAllText(Path.Join(tempDir, "appsettings.Production.json"), """{"Key": {"Nested": 1}}""");

            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();
            A.CallTo(() => locationProvider.Directory).Returns(tempDir);

            var provider = new FileBasedConfigProvider(locationProvider, logger);

            Assert.Equal(1, provider.Resolve<int>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Key:Nested"))).Value);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void GetValue_ReturnsNullWhenTrailingKeyInNonObject()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Join(tempDir, "appsettings.json"), """{"Key": [1, 2]}""");

            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();
            A.CallTo(() => locationProvider.Directory).Returns(tempDir);

            var provider = new FileBasedConfigProvider(locationProvider, logger);

            // "Key" is an array, asking for "Key.Sub" should return null via default switch case
            Assert.Null(provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Key:Sub"))).Value);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void Initialize_HandlesEmptyFile()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            A.CallTo(() => locationProvider.Directory).Returns(tempDir);
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

            File.WriteAllText(Path.Join(tempDir, "appsettings.json"), "");

            var provider = new FileBasedConfigProvider(locationProvider, logger);
            var result = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Key"))).Value;

            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void Initialize_HandlesWhitespaceFile()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            A.CallTo(() => locationProvider.Directory).Returns(tempDir);
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

            File.WriteAllText(Path.Join(tempDir, "appsettings.json"), "   ");

            var provider = new FileBasedConfigProvider(locationProvider, logger);
            var result = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Key"))).Value;

            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void Initialize_MergesMultipleFilesForSameEnvironment()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Join(tempDir, "appsettings.json"), "{\"Key1\": \"Value1\"}");
            File.WriteAllText(Path.Join(tempDir, "config_extra.json"), "{\"Key2\": \"Value2\"}");

            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            A.CallTo(() => locationProvider.Directory).Returns(tempDir);
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

            var provider = new FileBasedConfigProvider(locationProvider, logger);

            Assert.Equal("Value1", provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Key1"))).Value);
            Assert.Equal("Value2", provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Key2"))).Value);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Initialize_ListMerge_UsesReplaceSemantics()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Join(tempDir, "appsettings.json"), """{"Items":["a","b"]}""");
            File.WriteAllText(Path.Join(tempDir, "config_override.json"), """{"Items":["override"]}""");

            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            A.CallTo(() => locationProvider.Directory).Returns(tempDir);
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

            var provider = new FileBasedConfigProvider(locationProvider, logger);

            var values = provider.Resolve<List<string>>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Items"))).Value;

            Assert.NotNull(values);
            Assert.Equal(["override"], values);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Initialize_HandlesEmptyDirectoryProperty()
    {
        var locationProvider = A.Fake<IConfigFileLocationProvider>();
        A.CallTo(() => locationProvider.Directory).Returns("");
        var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

        var provider = new FileBasedConfigProvider(locationProvider, logger);
        var result = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Key"))).Value;

        Assert.Null(result);
    }

    [Fact]
    public void Initialize_HandlesWhitespaceDirectoryProperty()
    {
        var locationProvider = A.Fake<IConfigFileLocationProvider>();
        A.CallTo(() => locationProvider.Directory).Returns("   ");
        var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

        var provider = new FileBasedConfigProvider(locationProvider, logger);
        var result = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Key"))).Value;

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_ReportsConversionDiagnosticsForFileValues()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Join(tempDir, "appsettings.json"), """{"Feature":{"Count":"not-a-number"}}""");

            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            A.CallTo(() => locationProvider.Directory).Returns(tempDir);
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

            var provider = new FileBasedConfigProvider(locationProvider, logger);

            var resolution = ((IConfigDiagnosticProvider)provider)
                .Resolve(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Feature:Count")), typeof(int), ConfigAuditSourceRole.Base);

            Assert.Equal(ConfigAuditEntryState.Invalid, resolution.State);
            Assert.Contains(resolution.Sources, source => source.Kind == ConfigAuditSourceKind.File);
            Assert.Contains(resolution.Diagnostics, diagnostic => diagnostic.Code == "config-file-conversion-failed");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Initialize_RemovesDescendantOriginsWhenParentIsReplacedByScalar()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Join(tempDir, "appsettings.json"), """{"Shape":{"Nested":"base"}}""");
            File.WriteAllText(Path.Join(tempDir, "config_override.json"), """{"Shape":"scalar"}""");

            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            A.CallTo(() => locationProvider.Directory).Returns(tempDir);
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

            var provider = new FileBasedConfigProvider(locationProvider, logger);

            Assert.Equal("scalar", provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Shape"))).Value);
            var staleChild = ((IConfigDiagnosticProvider)provider)
                .Resolve(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Shape:Nested")), typeof(string), ConfigAuditSourceRole.Base);
            Assert.Equal(ConfigAuditEntryState.Missing, staleChild.State);
            Assert.Contains(staleChild.Sources, source => source.Kind == ConfigAuditSourceKind.Missing);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_AttachesLocationsForScalarAndObjectFileValues()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.json"),
                """
                {
                  "Feature": {
                    "Enabled": true
                  }
                }
                """);

            var provider = CreateProvider(tempDir);

            var parent = AssertFileSource(Resolve(provider, "Feature", typeof(Dictionary<string, bool>)));
            var child = AssertFileSource(Resolve(provider, "Feature:Enabled", typeof(bool)));

            AssertLocation(parent, lineNumber: 2, byteColumnNumber: 3);
            AssertLocation(child, lineNumber: 3, byteColumnNumber: 5);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_ProjectsArrayElementsAndNestedObjectMembers()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.json"),
                """
                {
                  "Items": [
                    {
                      "Name": "one"
                    }
                  ]
                }
                """);

            var provider = CreateProvider(tempDir);

            var parent = AssertFileSource(Resolve(provider, "Items", typeof(List<NamedItem>)));
            var descendant = Resolve(provider, "Items:0:Name", typeof(string));

            AssertLocation(parent, lineNumber: 2, byteColumnNumber: 3);
            Assert.Equal(ConfigAuditEntryState.Resolved, descendant.State);
            Assert.Equal("one", descendant.Value);
            AssertFileSource(descendant);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_SuppressesLocationForCaseInsensitivePathCollisions()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.json"),
                """
                {
                  "Feature": {
                    "Enabled": true
                  },
                  "feature": {
                    "Enabled": false
                  }
                }
                """);

            var provider = CreateProvider(tempDir);

            var result = Resolve(provider, "feature:Enabled", typeof(bool));

            Assert.Equal(ConfigAuditEntryState.Invalid, result.State);
            Assert.Null(result.Value);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "config-key-collision");
            Assert.All(result.Sources, source => Assert.Null(source.Location));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_TreatsLiteralDotsAsPartOfAKey()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Join(tempDir, "appsettings.json"), """{"Feature.Enabled":true}""");

            var provider = CreateProvider(tempDir);

            var resolution = Resolve(provider, "Feature.Enabled", typeof(bool));

            Assert.Equal(ConfigAuditEntryState.Resolved, resolution.State);
            Assert.Equal(true, resolution.Value);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_KeepsLiteralAndNestedPathsDistinct()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.json"),
                """
                {
                  "Feature": {
                    "Enabled": false
                  },
                  "Feature.Enabled": true
                }
                """);

            var provider = CreateProvider(tempDir);

            var nested = Resolve(provider, "Feature:Enabled", typeof(bool));
            var literal = Resolve(provider, "Feature.Enabled", typeof(bool));
            Assert.Equal(ConfigAuditEntryState.Resolved, nested.State);
            Assert.Equal(false, nested.Value);
            Assert.Equal(ConfigAuditEntryState.Resolved, literal.State);
            Assert.Equal(true, literal.Value);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_KeepsLiteralObjectAndNestedPathDistinct()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.json"),
                """
                {
                  "Feature": {
                    "Enabled": {
                      "Nested": false
                    }
                  },
                  "Feature.Enabled": {
                    "Nested": true
                  }
                }
                """);

            var provider = CreateProvider(tempDir);

            var nested = Resolve(provider, "Feature:Enabled:Nested", typeof(bool));
            var literal = Resolve(provider, "Feature.Enabled:Nested", typeof(bool));
            Assert.Equal(ConfigAuditEntryState.Resolved, nested.State);
            Assert.Equal(false, nested.Value);
            Assert.Equal(ConfigAuditEntryState.Resolved, literal.State);
            Assert.Equal(true, literal.Value);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_UsesByteColumnsForBomCrLfAndNonAsciiContent()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            const string json = "{\r\n  \"é\": 1,\r\n  \"Port\": 5\r\n}";
            var path = Path.Join(tempDir, "appsettings.json");
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)]);

            var provider = CreateProvider(tempDir);

            var source = AssertFileSource(Resolve(provider, "Port", typeof(int)));

            AssertLocation(source, lineNumber: 3, byteColumnNumber: 3);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_ReportsByteColumnAfterNonAsciiCharactersOnSameLine()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            const string json = """{"é":1,"Port":5}""";
            File.WriteAllText(Path.Join(tempDir, "appsettings.json"), json);

            var expectedColumn = Encoding.UTF8.GetByteCount(json[..json.IndexOf("\"Port\"", StringComparison.Ordinal)]) + 1;
            var provider = CreateProvider(tempDir);

            var source = AssertFileSource(Resolve(provider, "Port", typeof(int)));

            AssertLocation(source, lineNumber: 1, expectedColumn);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SourceLocationMap_RejectsDuplicateExactPathLocations()
    {
        var map = ConfigFileSourceLocationMap.Create(Encoding.UTF8.GetBytes(
            """
            {
              "Port": 5,
              "Port": 6
            }
            """));

        var location = map.GetLocation("Port");

        Assert.Null(location);
    }

    [Fact]
    public void SourceLocationMap_ReturnsNoLocationsForMalformedJson()
    {
        var map = ConfigFileSourceLocationMap.Create(Encoding.UTF8.GetBytes("""{"Port": }"""));

        Assert.Null(map.GetLocation("Port"));
    }

    [Fact]
    public void SourceLocationMap_ReturnsNoLocationsForEmptyAndNonObjectJson()
    {
        var empty = ConfigFileSourceLocationMap.Create(Array.Empty<byte>());
        var nonObject = ConfigFileSourceLocationMap.Create(Encoding.UTF8.GetBytes("""["Port"]"""));

        Assert.Null(empty.GetLocation("Port"));
        Assert.Null(nonObject.GetLocation("Port"));
    }

    [Fact]
    public void SourceLocationMap_HandlesCrWhitespaceAndPropertyAtLineStart()
    {
        var map = ConfigFileSourceLocationMap.Create(Encoding.UTF8.GetBytes("{\r\"Port\": 5\r}"));

        var location = map.GetLocation("Port");

        Assert.NotNull(location);
        Assert.Equal(2, location.LineNumber);
        Assert.Equal(1, location.ByteColumnNumber);
    }

    [Fact]
    public void SourceLocationMap_KeepsCaseInsensitiveAmbiguityAfterLaterExactDuplicate()
    {
        var map = ConfigFileSourceLocationMap.Create(Encoding.UTF8.GetBytes(
            """
            {
              "Feature": {
                "Enabled": true
              },
              "feature": {
                "Enabled": false
              },
              "Feature": {
                "Enabled": true
              }
            }
            """));

        Assert.Null(map.GetLocation("Feature"));
        Assert.Null(map.GetLocation("Feature:Enabled"));
    }

    [Fact]
    public void Resolve_CreatesSourceLocationMapLazilyForAuditRequests()
    {
        var mapCreationCount = 0;
        var filePath = Path.Join(CreateTempDirectoryPath(), "appsettings.json");
        var snapshot = new ConfigFileProviderSnapshot(
            new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase)
            {
                [Environments.Production] = new JsonObject
                {
                    ["Port"] = 5
                }
            },
            new Dictionary<string, Dictionary<string, ConfigAuditSourceRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                [Environments.Production] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Port"] = new ConfigAuditSourceRecord
                    {
                        Kind = ConfigAuditSourceKind.File,
                        ProviderName = nameof(FileBasedConfigProvider),
                        ProviderPriority = 1,
                        FilePath = filePath,
                        ConfigPath = "Port",
                        AppliedToPath = "Port",
                        Role = ConfigAuditSourceRole.Base
                    }
                }
            },
            [],
            new Dictionary<string, Lazy<ConfigFileSourceLocationMap>>(StringComparer.OrdinalIgnoreCase)
            {
                [filePath] = new(
                    () =>
                    {
                        mapCreationCount++;
                        return ConfigFileSourceLocationMap.Create(Encoding.UTF8.GetBytes(
                            """
                            {
                              "Port": 5
                            }
                            """));
                    },
                    isThreadSafe: true)
            });
        var provider = new FileBasedConfigProvider(snapshot);

        Assert.Equal(5, provider.Resolve<int>(new ConfigProviderRequest(Environments.Production, AppSurfaceConfigKey.Parse("Port"))).Value);
        Assert.Equal(0, mapCreationCount);

        var source = AssertFileSource(Resolve(provider, "Port", typeof(int)));
        AssertLocation(source, lineNumber: 2, byteColumnNumber: 3);
        Assert.Equal(1, mapCreationCount);

        _ = Resolve(provider, "Port", typeof(int));
        Assert.Equal(1, mapCreationCount);
    }

    [Fact]
    public void Resolve_UsesOverrideLocationWhenParentIsReplaced()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.json"),
                """
                {
                  "Shape": {
                    "Nested": "base"
                  }
                }
                """);
            File.WriteAllText(
                Path.Join(tempDir, "config_override.json"),
                """
                {
                  "Shape": "scalar"
                }
                """);

            var provider = CreateProvider(tempDir);

            var resolution = Resolve(provider, "Shape", typeof(string));
            var source = Assert.Single(resolution.Sources, item => Path.GetFileName(item.FilePath) == "config_override.json");
            Assert.Contains(resolution.Sources, item => Path.GetFileName(item.FilePath) == "appsettings.json");
            var child = Resolve(provider, "Shape:Nested", typeof(string));

            AssertLocation(source, lineNumber: 2, byteColumnNumber: 3);
            Assert.Equal("config_override.json", Path.GetFileName(source.FilePath));
            Assert.Equal(ConfigAuditEntryState.Missing, child.State);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_ReusesCachedLocationsAfterSnapshotInitialization()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Join(tempDir, "appsettings.json");
            File.WriteAllText(
                configPath,
                """
                {
                  "Port": 5
                }
                """);

            var provider = CreateProvider(tempDir);

            var first = AssertFileSource(Resolve(provider, "Port", typeof(int)));
            File.WriteAllText(
                configPath,
                """
                {



                  "Port": 6
                }
                """);
            var second = AssertFileSource(Resolve(provider, "Port", typeof(int)));

            AssertLocation(first, lineNumber: 2, byteColumnNumber: 3);
            AssertLocation(second, lineNumber: 2, byteColumnNumber: 3);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static FileBasedConfigProvider CreateProvider(string tempDir)
    {
        var locationProvider = A.Fake<IConfigFileLocationProvider>();
        A.CallTo(() => locationProvider.Directory).Returns(tempDir);
        var logger = A.Fake<ILogger<FileBasedConfigProvider>>();
        return new FileBasedConfigProvider(locationProvider, logger);
    }

    private static string CreateTempDirectoryPath() =>
        Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    private static ConfigValueResolution Resolve(FileBasedConfigProvider provider, string key, Type valueType) =>
        ((IConfigDiagnosticProvider)provider)
        .Resolve(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse(key)), valueType, ConfigAuditSourceRole.Base);

    private static ConfigAuditSourceRecord AssertFileSource(ConfigValueResolution resolution) =>
        Assert.Single(resolution.Sources, source => source.Kind == ConfigAuditSourceKind.File);

    private static void AssertLocation(ConfigAuditSourceRecord source, int lineNumber, int byteColumnNumber)
    {
        Assert.NotNull(source.Location);
        Assert.Equal(lineNumber, source.Location.LineNumber);
        Assert.Equal(byteColumnNumber, source.Location.ByteColumnNumber);
    }

    private sealed class NamedItem
    {
        public string? Name { get; set; }
    }
}
