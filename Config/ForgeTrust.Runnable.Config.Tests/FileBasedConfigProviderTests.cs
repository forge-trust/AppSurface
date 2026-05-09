using FakeItEasy;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.Runnable.Config.Tests;

public class FileBasedConfigProviderTests
{
    [Fact]
    public void GetValue_MergesFilesByEnvironmentAndPriority()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, "appsettings.json"),
                """{"Feature":{"Enabled":false,"Name":"Prod"}}""");
            File.WriteAllText(
                Path.Combine(tempDir, "config_extra.json"),
                """{"Feature":{"Extra":"ProdExtra"}}""");
            File.WriteAllText(
                Path.Combine(tempDir, "appsettings.Development.json"),
                """{"Feature":{"Name":"Dev"}}""");
            File.WriteAllText(
                Path.Combine(tempDir, "config_extra.Development.json"),
                """{"Feature":{"Enabled":true,"Extra":"Value"}}""");
            File.WriteAllText(Path.Combine(tempDir, "config_bad.json"), "{not json}");

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
            .Returns(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        var provider = new FileBasedConfigProvider(locationProvider, logger);

        Assert.Null(provider.GetValue<string>("Production", "Any.Key"));
    }

    [Fact]
    public void GetValue_ReusesCachedConfigurationAfterInitialization()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var configPath = Path.Combine(tempDir, "appsettings.json");
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
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Valid file
            File.WriteAllText(
                Path.Combine(tempDir, "appsettings.json"),
                """{"Feature":{"Enabled":true}}""");

            // Invalid JSON
            File.WriteAllText(
                Path.Combine(tempDir, "config_broken.json"),
                """{"Feature": { "Enabled": } }"""); // Syntax error

            // Non-object root
            File.WriteAllText(
                Path.Combine(tempDir, "config_array.json"),
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
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, "appsettings.json"),
                """{"Feature":{"Enabled":true}}""");

            File.WriteAllText(
                Path.Combine(tempDir, "config_override.json"),
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
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, "appsettings.json"),
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
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, "appsettings.Staging.json"),
                """{"Env":"Staging"}""");
            File.WriteAllText(
                Path.Combine(tempDir, "config_Feature.Development.json"),
                """{"Env":"Dev"}""");
            File.WriteAllText(
                Path.Combine(tempDir, "config_Base.json"),
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
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, "appsettings.json"),
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
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, "appsettings.json"),
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
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "appsettings.json"), """{"Key": "string"}""");
            File.WriteAllText(Path.Combine(tempDir, "appsettings.Production.json"), """{"Key": {"Nested": 1}}""");

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
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "appsettings.json"), """{"Key": [1, 2]}""");

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
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            A.CallTo(() => locationProvider.Directory).Returns(tempDir);
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

            File.WriteAllText(Path.Combine(tempDir, "appsettings.json"), "");

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
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var locationProvider = A.Fake<IConfigFileLocationProvider>();
            A.CallTo(() => locationProvider.Directory).Returns(tempDir);
            var logger = A.Fake<ILogger<FileBasedConfigProvider>>();

            File.WriteAllText(Path.Combine(tempDir, "appsettings.json"), "   ");

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
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "appsettings.json"), "{\"Key1\": \"Value1\"}");
            File.WriteAllText(Path.Combine(tempDir, "config_extra.json"), "{\"Key2\": \"Value2\"}");

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
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "appsettings.json"), """{"Items":["a","b"]}""");
            File.WriteAllText(Path.Combine(tempDir, "config_override.json"), """{"Items":["override"]}""");

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
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "appsettings.json"), """{"Feature":{"Count":"not-a-number"}}""");

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
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "appsettings.json"), """{"Shape":{"Nested":"base"}}""");
            File.WriteAllText(Path.Combine(tempDir, "config_override.json"), """{"Shape":"scalar"}""");

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
}
