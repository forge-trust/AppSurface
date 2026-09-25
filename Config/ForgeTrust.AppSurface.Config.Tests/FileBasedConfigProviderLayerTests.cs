using System.Text;
using System.Text.Json.Nodes;
using FakeItEasy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class FileBasedConfigProviderLayerTests
{
    [Fact]
    public void Snapshot_PreservesOrderedDeepClonedLayersAndLegacyNullMerge()
    {
        var directory = CreateDirectory();
        try
        {
            File.WriteAllText(Path.Join(directory, "appsettings.json"), "{\"Feature\":{\"Name\":\"base\",\"Null\":null}}");
            File.WriteAllText(Path.Join(directory, "config_override.json"), "{\"Feature\":{\"Name\":\"override\"}}");

            var provider = CreateProvider(directory);

            Assert.Equal("override", provider.Resolve<string>(new ConfigProviderRequest(Environments.Production, AppSurfaceConfigKey.Parse("Feature:Name"))).Value);
            Assert.Equal(2, provider.Snapshot.Layers.Length);
            Assert.Equal(2, provider.Snapshot.LoadEvents.Length);
            Assert.Equal(0, provider.Snapshot.LoadEvents[0].Order);
            Assert.Equal(1, provider.Snapshot.LoadEvents[1].Order);
            Assert.Equal(Path.GetFullPath(Path.Join(directory, "appsettings.json")), provider.Snapshot.Layers[0].FilePath);
            var feature = Assert.IsType<JsonObject>(provider.Snapshot.Layers[0].Document["Feature"]);
            Assert.Null(feature["Null"]);
            Assert.NotNull(provider.Snapshot.Layers[0].SourceLocationMap.Value.GetLocation("Feature.Name"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Snapshot_LayerDocumentIsIsolatedAndLayerFormattingOmitsJsonValues()
    {
        var directory = CreateDirectory();
        try
        {
            File.WriteAllText(Path.Join(directory, "appsettings.json"), "{\"Secret\":\"do-not-format\"}");

            var provider = CreateProvider(directory);
            var layer = Assert.IsType<ConfigFileLayer>(Assert.Single(provider.Snapshot.Layers));
            var document = layer.Document;
            document["Secret"] = "mutated";

            Assert.Equal("do-not-format", layer.Document["Secret"]?.GetValue<string>());
            Assert.Equal(nameof(ConfigFileLayer), layer.ToString());
            Assert.DoesNotContain("do-not-format", layer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("{\"Feature\":{\"Value\":}", "config-file-malformed")]
    [InlineData("{\"Feature\":{\"Value\":1}", "config-file-malformed")]
    public void Snapshot_RetainsSanitizedParseFailures(string content, string expectedCode)
    {
        var directory = CreateDirectory();
        try
        {
            File.WriteAllText(Path.Join(directory, "appsettings.json"), content);

            var provider = CreateProvider(directory);

            var failure = Assert.IsType<ConfigFileLoadFailure>(Assert.Single(provider.Snapshot.LoadEvents));
            Assert.Equal(expectedCode, failure.Code);
            Assert.Equal(Environments.Production, failure.Environment);
            Assert.Equal("appsettings.json", failure.DisplayPath);
            Assert.DoesNotContain("Value", failure.DisplayPath);
            Assert.Empty(provider.Snapshot.Layers);
            Assert.Null(provider.Resolve<string>(new ConfigProviderRequest(Environments.Production, AppSurfaceConfigKey.Parse("Feature:Value"))).Value);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Snapshot_RetainsSiblingsWhenNestedArrayMemberCollides()
    {
        var directory = CreateDirectory();
        try
        {
            File.WriteAllText(
                Path.Join(directory, "appsettings.json"),
                "{\"Feature\":{\"Items\":[{\"Key\":1,\"key\":2}],\"Sibling\":7}}");

            var provider = CreateProvider(directory);

            Assert.IsType<ConfigFileLayer>(Assert.Single(provider.Snapshot.Layers));
            Assert.Equal(ConfigProviderValueStatus.Terminal, provider.Resolve<int>(new ConfigProviderRequest(Environments.Production,
                AppSurfaceConfigKey.Parse("Feature:Items:0"))).Status);
            Assert.Equal(7, provider.Resolve<int>(new ConfigProviderRequest(Environments.Production,
                AppSurfaceConfigKey.Parse("Feature:Sibling"))).Value);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snapshot_OrdersCaseOnlyPathCollisionDeterministically(bool reverseEnumeration)
    {
        var directory = VirtualDirectory();
        var upper = Path.Join(directory, "config_A.json");
        var lower = Path.Join(directory, "config_a.json");
        string[] paths = reverseEnumeration ? [upper, lower] : [lower, upper];
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [upper] = Encoding.UTF8.GetBytes("{\"A\":2}"),
            [lower] = Encoding.UTF8.GetBytes("{\"A\":1}")
        };
        var readPaths = new List<string>();
        var provider = CreateProvider(directory, (_, pattern, _) => pattern == "config_*.json" ? paths : [],
            path =>
            {
                readPaths.Add(path);
                return contents[path];
            });

        Assert.Equal(2, provider.Snapshot.LoadEvents.Length);
        var layer = Assert.IsType<ConfigFileLayer>(provider.Snapshot.LoadEvents[0]);
        Assert.Same(layer, Assert.Single(provider.Snapshot.Layers));
        Assert.Equal(0, layer.Order);
        Assert.Equal(Path.GetFullPath(upper), layer.FilePath);
        Assert.Equal(2, layer.Document["A"]!.GetValue<int>());
        Assert.NotNull(layer.SourceLocationMap.Value.GetLocation("A"));
        var failure = Assert.IsType<ConfigFileLoadFailure>(provider.Snapshot.LoadEvents[1]);
        Assert.Equal("config-file-path-collision", failure.Code);
        Assert.Equal(ConfigFileLoadFailureClassification.PathCollision, failure.Classification);
        Assert.Equal(1, failure.Order);
        Assert.Equal("config_a.json", failure.DisplayPath);
        Assert.Equal(Environments.Production, failure.Environment);
        Assert.Equal(upper, Assert.Single(readPaths));
        // The ordinal tie-break loads uppercase first; collision handling skips the lowercase file entirely.
        Assert.Equal(2, provider.Resolve<int>(new ConfigProviderRequest(Environments.Production, AppSurfaceConfigKey.Parse("A"))).Value);
        Assert.Equal(upper, provider.Snapshot.Origins[Environments.Production]["A"].FilePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snapshot_RetainsUnreadableFileEventWithoutExceptionText(bool denied)
    {
        var directory = VirtualDirectory();
        var first = Path.Join(directory, "appsettings.Development.json");
        var unreadable = Path.Join(directory, "config_unreadable.Development.json");
        var last = Path.Join(directory, "config_z.Development.json");
        var logger = new RecordingLogger();
        var readPaths = new List<string>();
        var provider = CreateProvider(directory,
            (_, pattern, _) => pattern == "appsettings*.json" ? [first] : [last, unreadable],
            path =>
            {
                readPaths.Add(path);
                if (path == unreadable)
                    throw ReadFailure(denied);
                return Encoding.UTF8.GetBytes(path == first ? "{\"Retained\":1}" : "{\"Continued\":2}");
            }, logger);

        Assert.Equal(3, provider.Snapshot.LoadEvents.Length);
        Assert.Equal(new[] { first, unreadable, last }, readPaths);
        Assert.Equal(2, provider.Snapshot.Layers.Length);
        var failure = Assert.IsType<ConfigFileLoadFailure>(provider.Snapshot.LoadEvents[1]);
        Assert.Equal("config-file-unreadable", failure.Code);
        Assert.Equal(ConfigFileLoadFailureClassification.Read, failure.Classification);
        Assert.Equal("config_unreadable.Development.json", failure.DisplayPath);
        Assert.Equal(Environments.Development, failure.Environment);
        Assert.Equal(1, failure.Order);
        Assert.Equal(2, provider.Snapshot.LoadEvents[2].Order);
        Assert.Equal(1, provider.Resolve<int>(new ConfigProviderRequest(Environments.Development, AppSurfaceConfigKey.Parse("Retained"))).Value);
        Assert.Equal(2, provider.Resolve<int>(new ConfigProviderRequest(Environments.Development, AppSurfaceConfigKey.Parse("Continued"))).Value);
        AssertSanitizedFailure(provider, logger, failure.Code, "config_unreadable.Development.json");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Snapshot_RetainsDirectoryEnumerationFailureEvenWhenSequenceThrowsLazily(bool denied, bool lazy)
    {
        var directory = VirtualDirectory();
        var logger = new RecordingLogger();
        var patterns = new List<string>();
        var reads = 0;
        var provider = CreateProvider(directory, (requestedDirectory, pattern, searchOption) =>
            {
                Assert.Equal(directory, requestedDirectory);
                Assert.Equal(SearchOption.TopDirectoryOnly, searchOption);
                patterns.Add(pattern);
                if (pattern == "appsettings*.json")
                    return [Path.Join(directory, "appsettings.json")];
                if (!lazy)
                    throw ReadFailure(denied);
                return EnumerateThenFail(Path.Join(directory, "config_partial.json"), ReadFailure(denied));
            },
            _ =>
            {
                reads++;
                return Encoding.UTF8.GetBytes("{\"A\":1}");
            }, logger);

        var failure = Assert.IsType<ConfigFileLoadFailure>(Assert.Single(provider.Snapshot.LoadEvents));

        Assert.Equal(new[] { "appsettings*.json", "config_*.json" }, patterns);
        Assert.Equal("config-file-directory-unreadable", failure.Code);
        Assert.Equal(ConfigFileLoadFailureClassification.Read, failure.Classification);
        Assert.Equal("*", failure.Environment);
        Assert.Equal(0, failure.Order);
        Assert.Equal(Path.GetFileName(directory), failure.DisplayPath);
        Assert.Empty(provider.Snapshot.Layers);
        Assert.Empty(provider.Snapshot.Environments);
        Assert.Empty(provider.Snapshot.Origins);
        Assert.Empty(provider.Snapshot.SourceLocationMaps);
        Assert.Equal(0, reads);
        AssertSanitizedFailure(provider, logger, failure.Code, Path.GetFileName(directory));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\t")]
    public void Snapshot_SanitizesFailureDisplayPathAndDoesNotLogExceptionDetails(string control)
    {
        // Virtual paths only: no filename containing control characters is created on Windows or other hosts.
        var directory = VirtualDirectory();
        var path = Path.Join(directory, $"config_bad{control}name.json");
        var logger = new RecordingLogger();
        var provider = CreateProvider(directory, (_, pattern, _) => pattern == "config_*.json" ? [path] : [],
            _ => Encoding.UTF8.GetBytes("{\"Provider\":\"sentinel-secret\""), logger);

        var failure = Assert.IsType<ConfigFileLoadFailure>(Assert.Single(provider.Snapshot.LoadEvents));

        Assert.Equal("config-file-malformed", failure.Code);
        Assert.Equal(ConfigFileLoadFailureClassification.Parse, failure.Classification);
        Assert.Equal("config_badname.json", failure.DisplayPath);
        Assert.DoesNotContain(failure.DisplayPath, char.IsControl);
        AssertSanitizedFailure(provider, logger, failure.Code, "config_badname.json");
    }

    [Theory]
    [InlineData("config_bad\nname", "config_badname")]
    [InlineData("\r\n", "configuration-directory")]
    public void Snapshot_SanitizesDirectoryFailureDisplayPath(string directoryName, string expectedDisplayName)
    {
        var directory = Path.Join(VirtualDirectory(), directoryName) + Path.DirectorySeparatorChar;
        var logger = new RecordingLogger();
        var provider = CreateProvider(directory, (_, _, _) => throw ReadFailure(denied: true),
            _ => throw new InvalidOperationException("Read must not occur."), logger);

        var failure = Assert.IsType<ConfigFileLoadFailure>(Assert.Single(provider.Snapshot.LoadEvents));

        Assert.Equal("config-file-directory-unreadable", failure.Code);
        Assert.Equal(expectedDisplayName, failure.DisplayPath);
        AssertSanitizedFailure(provider, logger, failure.Code, expectedDisplayName);
    }

    [Fact]
    public void Snapshot_UsesInjectedIoLazilyOnceAndCachesProductionParseResults()
    {
        var directory = VirtualDirectory();
        var existsCalls = 0;
        var patterns = new List<string>();
        var reads = 0;
        var provider = CreateProvider(directory, (requested, pattern, searchOption) =>
            {
                Assert.Equal(directory, requested);
                Assert.Equal(SearchOption.TopDirectoryOnly, searchOption);
                patterns.Add(pattern);
                return pattern == "appsettings*.json" ? [Path.Join(directory, "appsettings.json")] : [];
            },
            _ =>
            {
                reads++;
                return Encoding.UTF8.GetBytes("{\"Feature\":{\"Name\":\"base\"}}");
            }, directoryExists: requested =>
            {
                Assert.Equal(directory, requested);
                existsCalls++;
                return true;
            });

        Assert.Equal(0, existsCalls);
        Assert.Empty(patterns);
        Assert.Equal(0, reads);
        Assert.Equal("base", provider.Resolve<string>(new ConfigProviderRequest(Environments.Production, AppSurfaceConfigKey.Parse("Feature:Name"))).Value);
        var snapshot = provider.Snapshot;
        Assert.Same(snapshot, provider.Snapshot);
        Assert.Single(snapshot.Layers);
        Assert.Single(snapshot.LoadEvents);
        Assert.NotNull(snapshot.Layers[0].SourceLocationMap.Value.GetLocation("Feature.Name"));
        Assert.Equal("base", provider.Resolve<string>(new ConfigProviderRequest(Environments.Production, AppSurfaceConfigKey.Parse("Feature:Name"))).Value);
        Assert.Equal(1, existsCalls);
        Assert.Equal(new[] { "appsettings*.json", "config_*.json" }, patterns);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Snapshot_DoesNotEnumerateOrReadWhenInjectedDirectoryIsAbsent()
    {
        var provider = CreateProvider(VirtualDirectory(),
            (_, _, _) => throw new InvalidOperationException("Enumeration must not occur."),
            _ => throw new InvalidOperationException("Read must not occur."), directoryExists: _ => false);

        Assert.Empty(provider.Snapshot.LoadEvents);
        Assert.Empty(provider.Snapshot.Layers);
        Assert.Empty(provider.Snapshot.Diagnostics);
        Assert.Null(provider.Resolve<string>(new ConfigProviderRequest(Environments.Production, AppSurfaceConfigKey.Parse("Feature:Name"))).Value);
    }

    [Fact]
    public void Snapshot_RecordsOversizedEmptyAndMalformedFilesWithoutDroppingValidLayer()
    {
        var directory = VirtualDirectory();
        var paths = new[]
        {
            Path.Join(directory, "appsettings.json"),
            Path.Join(directory, "config_big.json"),
            Path.Join(directory, "config_empty.json"),
            Path.Join(directory, "config_malformed.json")
        };
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [paths[0]] = Encoding.UTF8.GetBytes("{\"Keep\":7}"),
            [paths[1]] = Encoding.UTF8.GetBytes($"{{\"TooLarge\":\"{new string('x', 80)}\"}}"),
            [paths[2]] = [],
            [paths[3]] = Encoding.UTF8.GetBytes("{\"A\":}")
        };
        var provider = CreateProvider(directory,
            (_, pattern, _) => pattern == "appsettings*.json" ? [paths[0]] : paths.Skip(1),
            path => contents[path],
            resourceOptions: Options.Create(new ConfigResourceOptions { MaxFileBytes = 64 }));

        var events = provider.Snapshot.LoadEvents;

        var validLayer = Assert.IsType<ConfigFileLayer>(events[0]);
        Assert.Equal(7, validLayer.Document["Keep"]?.GetValue<int>());
        Assert.Collection(events.Skip(1),
            oversized => Assert.Equal("config-file-byte-limit", Assert.IsType<ConfigFileLoadFailure>(oversized).Code),
            empty => Assert.Equal("config-file-empty", Assert.IsType<ConfigFileLoadFailure>(empty).Code),
            malformed => Assert.Equal("config-file-malformed", Assert.IsType<ConfigFileLoadFailure>(malformed).Code));
        Assert.Contains(provider.Snapshot.Diagnostics, item => item.Diagnostic.Code == "config-file-byte-limit");
    }

    [Fact]
    public void ResolveRaw_SkipsUnrepresentableMembersAndInvalidLocationPaths()
    {
        var provider = CreateRawProvider("{\"Bad::Member\":1,\"Valid\":{\"Value\":2}}");
        var raw = provider.ResolveRaw(Environments.Production, ConfigLogicalPath.Parse("Valid"));
        var locationMap = Assert.Single(provider.Snapshot.Layers).SourceLocationMap.Value;

        Assert.Equal(ConfigCompositionValueResolutionStatus.Resolved, raw.Status);
        Assert.Equal(2, JsonNode.Parse(raw.ReadRaw()!)!["Value"]!.GetValue<int>());
        Assert.Null(locationMap.GetLocation("Valid::Value"));
    }

    [Fact]
    public void Snapshot_DetectsDuplicateMembersNestedInsideLegacyEncodedArrays()
    {
        var directory = VirtualDirectory();
        var path = Path.Join(directory, "appsettings.json");
        var json = "{\"Items\":[{\"Key\":1,\"key\":2}]}";
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(json)).ToArray();
        var provider = CreateProvider(directory,
            (_, pattern, _) => pattern == "appsettings*.json" ? [path] : [],
            _ => bytes);

        var failure = Assert.IsType<ConfigFileLoadFailure>(Assert.Single(provider.Snapshot.LoadEvents));

        Assert.Equal("config-file-duplicate-member", failure.Code);
        Assert.Equal(ConfigFileLoadFailureClassification.Parse, failure.Classification);
        Assert.Empty(provider.Snapshot.Layers);
    }

    [Fact]
    public void ResolveRaw_ReturnsMergedRootAndPreservesFoundJsonNullAsResolved()
    {
        var environment = new JsonObject
        {
            ["PresentNull"] = null,
            ["Port"] = 5
        };
        var snapshot = new ConfigFileProviderSnapshot(
            new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase)
            {
                [Environments.Production] = environment
            },
            new Dictionary<string, Dictionary<string, ConfigAuditSourceRecord>>(StringComparer.OrdinalIgnoreCase),
            []);
        var provider = new FileBasedConfigProvider(snapshot);
        var rawProvider = (IConfigCompositionValueProvider)provider;

        var nullResult = rawProvider.ResolveRaw(Environments.Production, "PresentNull");
        var rootResult = rawProvider.ResolveRaw(Environments.Production, string.Empty);
        var missingEnvironment = rawProvider.ResolveRaw("Staging", "PresentNull");
        var missingPath = rawProvider.ResolveRaw(Environments.Production, "Absent");

        Assert.Equal(ConfigCompositionValueResolutionStatus.Resolved, nullResult.Status);
        Assert.Equal(ConfigCompositionValueResolutionStatus.Resolved, rootResult.Status);
        Assert.False(nullResult.IsSensitive);
        Assert.False(rootResult.IsSensitive);
        Assert.Equal(ConfigCompositionValueResolutionStatus.Missing, missingEnvironment.Status);
        Assert.Equal(ConfigCompositionValueResolutionStatus.Missing, missingPath.Status);
    }

    [Theory]
    [InlineData("SeRvEr", "SeRvIcE", "PaYmEnTs", "SERVER:SERVICE:PAYMENTS")]
    [InlineData("SeRvEr", "SeRvIcE", "PaYmEnTs", "server:SERVICE:payments")]
    public void ResolveRaw_SelectsCanonicalRootAndRetainsOrdinaryValuesAndSecretDeclarations(
        string outer, string? middle, string? inner, string requestedRoot)
    {
        var selectedRoot = JsonNode.Parse("""
            {
              "endpoint": "https://service.example",
              "Retries": 3,
              "ApiKey": { "key": "demo-key", "version": "4", "enabled": false },
              "Allowed": [ "one", "two" ]
            }
            """)!;
        JsonNode document = selectedRoot.DeepClone();
        foreach (var member in new[] { inner, middle, outer })
        {
            if (member is not null)
                document = new JsonObject { [member] = document };
        }
        var provider = CreateRawProvider(document.ToJsonString());

        var raw = ((IConfigCompositionValueProvider)provider).ResolveRaw("production", requestedRoot);

        Assert.Equal(ConfigCompositionValueResolutionStatus.Resolved, raw.Status);
        Assert.False(raw.IsSensitive);
        Assert.Equal(provider.Name, raw.ProviderName);
        Assert.Equal(provider.Priority, raw.Priority);
        var value = Assert.IsType<JsonObject>(JsonNode.Parse(raw.ReadRaw()!));
        Assert.True(JsonNode.DeepEquals(selectedRoot, value));
        Assert.Equal("https://service.example", value["endpoint"]!.GetValue<string>());
        Assert.Equal("demo-key", value["ApiKey"]!["key"]!.GetValue<string>());
        Assert.False(value["ApiKey"]!["enabled"]!.GetValue<bool>());
        Assert.Single(provider.Snapshot.Layers);
        Assert.IsType<ConfigFileLayer>(Assert.Single(provider.Snapshot.LoadEvents));
    }

    [Theory]
    [InlineData("{\"ServiceOther\":{\"Endpoint\":\"wrong\"},\"Service\":{\"Endpoint\":\"correct\"}}", "SERVICE", "correct")]
    public void ResolveRaw_ReturnsFirstCompleteCanonicalMatchWithoutMergingCandidates(
        string document, string requestedRoot, string expected)
    {
        var provider = CreateRawProvider(document);

        var raw = ((IConfigCompositionValueProvider)provider).ResolveRaw(Environments.Production, requestedRoot);

        Assert.Equal(ConfigCompositionValueResolutionStatus.Resolved, raw.Status);
        Assert.Equal(expected, JsonNode.Parse(raw.ReadRaw()!)!["Endpoint"]!.GetValue<string>());
        Assert.Single(provider.Snapshot.LoadEvents);
    }

    [Theory]
    [InlineData("Service.Other")]
    [InlineData("SERVICE:ENDPOINT:CHILD")]
    [InlineData("Service..Endpoint")]
    [InlineData("Service: ")]
    [InlineData("Service.Items.0")]
    [InlineData("ServiceOther")]
    public void ResolveRaw_MissingPathsDoNotTraverseScalarsArraysOrInvalidSegments(string requestedRoot)
    {
        var provider = CreateRawProvider("{\"Service\":{\"Endpoint\":\"value\",\"Items\":[1,2]}}");
        var raw = ((IConfigCompositionValueProvider)provider).ResolveRaw(Environments.Production, requestedRoot);

        Assert.Equal(ConfigCompositionValueResolutionStatus.Missing, raw.Status);
        Assert.Null(raw.ReadRaw());
    }

    [Fact]
    public void ResolveRaw_DoesNotTreatDottedJsonMemberAsHierarchy()
    {
        var directory = VirtualDirectory();
        var provider = CreateProvider(directory,
            (_, pattern, _) => pattern == "appsettings*.json"
                ? [Path.Join(directory, "appsettings.json")]
                : [Path.Join(directory, "config_override.json")],
            path => Encoding.UTF8.GetBytes(Path.GetFileName(path) == "appsettings.json"
                ? "{\"Server.Service\":{\"Endpoint\":\"literal\"},\"Server\":{\"Service\":{\"Endpoint\":\"nested\"}}}"
                : "{}"));

        var raw = ((IConfigCompositionValueProvider)provider).ResolveRaw(Environments.Production, "SERVER:SERVICE");

        Assert.Equal(ConfigCompositionValueResolutionStatus.Resolved, raw.Status);
        var root = JsonNode.Parse(raw.ReadRaw()!)!;
        Assert.Equal("nested", root["Endpoint"]!.GetValue<string>());
        Assert.Equal(2, provider.Snapshot.Layers.Length);
    }

    [Fact]
    public void ResolveRaw_UsesLiteralDottedJsonMemberAndCaseInsensitiveLogicalIdentity()
    {
        var provider = CreateRawProvider("{\"Server.Service\":\"literal\",\"Server\":{\"Service\":{\"Endpoint\":\"nested\"}}}");
        var literal = provider.Resolve<string>(new ConfigProviderRequest(Environments.Production,
            AppSurfaceConfigKey.FromSegments("Server.Service")));
        var nested = provider.Resolve<string>(new ConfigProviderRequest(Environments.Production,
            AppSurfaceConfigKey.Parse("server:SERVICE:Endpoint")));
        Assert.Equal("literal", literal.Value);
        Assert.Equal("nested", nested.Value);
    }

    private static FileBasedConfigProvider CreateRawProvider(string document)
    {
        var directory = VirtualDirectory();
        return CreateProvider(directory, (_, pattern, _) => pattern == "appsettings*.json"
                ? [Path.Join(directory, "appsettings.json")] : [],
            _ => Encoding.UTF8.GetBytes(document));
    }

    private static FileBasedConfigProvider CreateProvider(
        string directory,
        ILogger<FileBasedConfigProvider>? logger = null)
    {
        var locationProvider = A.Fake<IConfigFileLocationProvider>();
        A.CallTo(() => locationProvider.Directory).Returns(directory);
        return new FileBasedConfigProvider(locationProvider, logger ?? A.Fake<ILogger<FileBasedConfigProvider>>());
    }

    private static FileBasedConfigProvider CreateProvider(
        string directory,
        Func<string, string, SearchOption, IEnumerable<string>> enumerateFiles,
        Func<string, byte[]> readAllBytes,
        ILogger<FileBasedConfigProvider>? logger = null,
        Func<string, bool>? directoryExists = null,
        IOptions<ConfigResourceOptions>? resourceOptions = null)
    {
        var locationProvider = A.Fake<IConfigFileLocationProvider>();
        A.CallTo(() => locationProvider.Directory).Returns(directory);
        return new FileBasedConfigProvider(locationProvider, logger ?? A.Fake<ILogger<FileBasedConfigProvider>>(),
            directoryExists ?? (_ => true), enumerateFiles, readAllBytes, resourceOptions);
    }

    private static string VirtualDirectory() => Path.Join(Path.GetTempPath(), "appsurface-virtual-config");

    private static Exception ReadFailure(bool denied) => denied
        ? new UnauthorizedAccessException("sentinel-secret\r\nprivate-file-content")
        : new IOException("sentinel-secret\r\nprivate-file-content");

    private static IEnumerable<string> EnumerateThenFail(string path, Exception exception)
    {
        yield return path;
        throw exception;
    }

    private static void AssertSanitizedFailure(
        FileBasedConfigProvider provider, RecordingLogger logger, string code, string displayName)
    {
        var diagnostic = Assert.Single(provider.Snapshot.Diagnostics).Diagnostic;
        Assert.Equal(code, diagnostic.Code);
        Assert.Contains(displayName, diagnostic.Message, StringComparison.Ordinal);
        var log = Assert.Single(logger.Entries);
        Assert.Null(log.Exception);
        Assert.Contains(displayName, log.Message, StringComparison.Ordinal);
        foreach (var message in new[] { diagnostic.Message, log.Message })
        {
            Assert.DoesNotContain(message, char.IsControl);
            Assert.False(message.Contains("sentinel-secret", StringComparison.Ordinal), "Diagnostics exposed file or exception contents.");
            Assert.DoesNotContain("private-file-content", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Provider", message, StringComparison.Ordinal);
        }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingLogger : ILogger<FileBasedConfigProvider>
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((formatter(state, exception), exception));
    }
}
