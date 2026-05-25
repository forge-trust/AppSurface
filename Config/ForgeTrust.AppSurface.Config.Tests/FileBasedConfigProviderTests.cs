using System.Text;
using FakeItEasy;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Config.Tests;

public class FileBasedConfigProviderTests
{
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

            Assert.Equal("Dev", provider.GetValue<string>("Development", "Feature.Name"));
            Assert.True(provider.GetValue<bool>("Development", "Feature.Enabled"));
            Assert.Equal("Value", provider.GetValue<string>("Development", "Feature.Extra"));
            Assert.False(provider.GetValue<bool>("Production", "Feature.Enabled"));
            Assert.Equal("ProdExtra", provider.GetValue<string>("Production", "Feature.Extra"));
            Assert.Null(provider.GetValue<string>("Production", "Feature.Unknown"));
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

        Assert.Null(provider.GetValue<string>("Production", "Any.Key"));
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

            Assert.True(provider.GetValue<bool>("Production", "Feature.Enabled"));

            File.WriteAllText(configPath, """{"Feature":{"Enabled":false}}""");

            Assert.True(provider.GetValue<bool>("Production", "Feature.Enabled"));
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
            Assert.True(provider.GetValue<bool>("Production", "Feature.Enabled"));
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
            Assert.True(provider.GetValue<bool>("Production", "Feature.Enabled"));
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
    public void GetValue_ReturnsDefaultOnDeserializationFailure()
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

            var value = provider.GetValue<int>("Production", "Feature.Count");

            Assert.Equal(0, value);
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

            Assert.Equal("Staging", provider.GetValue<string>("Staging", "Env"));
            Assert.Equal("Dev", provider.GetValue<string>("Development", "Env"));
            Assert.Equal("Base", provider.GetValue<string>("Production", "Env"));
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

            Assert.Equal(3, provider.GetValue<int>("Production", "App.Settings.RetryCount"));
            var endpoints = provider.GetValue<string[]>("Production", "App.Settings.Endpoints");
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

            var list1 = provider.GetValue<List<string>>("Production", "List");
            Assert.NotNull(list1);
            list1.Add("c");

            var list2 = provider.GetValue<List<string>>("Production", "List");
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
        Assert.Null(provider.GetValue<string>("Production", "Any"));
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

            Assert.Equal(1, provider.GetValue<int>("Production", "Key.Nested"));
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
            Assert.Null(provider.GetValue<string>("Production", "Key.Sub"));
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
            var result = provider.GetValue<string>("Production", "Key");

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
            var result = provider.GetValue<string>("Production", "Key");

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

            Assert.Equal("Value1", provider.GetValue<string>("Production", "Key1"));
            Assert.Equal("Value2", provider.GetValue<string>("Production", "Key2"));
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

            var values = provider.GetValue<List<string>>("Production", "Items");

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
        var result = provider.GetValue<string>("Production", "Key");

        Assert.Null(result);
    }

    [Fact]
    public void Initialize_HandlesWhitespaceDirectoryProperty()
    {
        var locationProvider = A.Fake<IConfigFileLocationProvider>();
        A.CallTo(() => locationProvider.Directory).Returns("   ");
        var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

        var provider = new FileBasedConfigProvider(locationProvider, logger);
        var result = provider.GetValue<string>("Production", "Key");

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
                .Resolve("Production", "Feature.Count", typeof(int), ConfigAuditSourceRole.Base);

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

            Assert.Equal("scalar", provider.GetValue<string>("Production", "Shape"));
            var staleChild = ((IConfigDiagnosticProvider)provider)
                .Resolve("Production", "Shape.Nested", typeof(string), ConfigAuditSourceRole.Base);
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
            var child = AssertFileSource(Resolve(provider, "Feature.Enabled", typeof(bool)));

            AssertLocation(parent, lineNumber: 2, byteColumnNumber: 3);
            AssertLocation(child, lineNumber: 3, byteColumnNumber: 5);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_AttachesCollectionParentLocationWithoutArrayDescendantOrigins()
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
            var descendant = Resolve(provider, "Items.0.Name", typeof(string));

            AssertLocation(parent, lineNumber: 2, byteColumnNumber: 3);
            Assert.Equal(ConfigAuditEntryState.Missing, descendant.State);
            Assert.Contains(descendant.Sources, source => source.Kind == ConfigAuditSourceKind.Missing);
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

            var source = AssertFileSource(Resolve(provider, "feature.Enabled", typeof(bool)));

            Assert.Null(source.Location);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_TreatsDottedJsonPropertyNamesAsUnsupportedPaths()
    {
        var tempDir = CreateTempDirectoryPath();
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Join(tempDir, "appsettings.json"), """{"Feature.Enabled":true}""");

            var provider = CreateProvider(tempDir);

            var resolution = Resolve(provider, "Feature.Enabled", typeof(bool));

            Assert.Equal(ConfigAuditEntryState.Missing, resolution.State);
            Assert.Contains(resolution.Sources, source => source.Kind == ConfigAuditSourceKind.Missing);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_SuppressesLocationWhenDottedLiteralCollidesWithNestedPath()
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

            var resolution = Resolve(provider, "Feature.Enabled", typeof(bool));
            var source = AssertFileSource(resolution);

            Assert.Equal(false, resolution.Value);
            Assert.Null(source.Location);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_SuppressesDescendantLocationWhenDottedLiteralObjectCollidesWithNestedPath()
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

            var resolution = Resolve(provider, "Feature.Enabled.Nested", typeof(bool));
            var source = AssertFileSource(resolution);

            Assert.Equal(false, resolution.Value);
            Assert.Null(source.Location);
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
    public void SourceLocationMap_UsesLastDuplicateExactPathLocation()
    {
        var map = ConfigFileSourceLocationMap.Create(Encoding.UTF8.GetBytes(
            """
            {
              "Port": 5,
              "Port": 6
            }
            """));

        var location = map.GetLocation("Port");

        Assert.NotNull(location);
        Assert.Equal(3, location.LineNumber);
        Assert.Equal(3, location.ByteColumnNumber);
    }

    [Fact]
    public void SourceLocationMap_ReturnsNoLocationsForMalformedJson()
    {
        var map = ConfigFileSourceLocationMap.Create(Encoding.UTF8.GetBytes("""{"Port": }"""));

        Assert.Null(map.GetLocation("Port"));
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
        Assert.Null(map.GetLocation("Feature.Enabled"));
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

            var source = AssertFileSource(Resolve(provider, "Shape", typeof(string)));
            var child = Resolve(provider, "Shape.Nested", typeof(string));

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
        .Resolve("Production", key, valueType, ConfigAuditSourceRole.Base);

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
