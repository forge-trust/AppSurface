using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class FileProjectionContractTests
{
    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{}\r")]
    [InlineData("{\"Empty\":{},\"List\":[]}")]
    [InlineData("{\"Array\":[null,true,false,12,0.25,1e100,\"text\",{},[],[1]]}")]
    [InlineData("{\"Root\":{\"a.b/c\\\\d\":42}}")]
    [InlineData("{\r\n\"A\":1,\r\"B\":2,\n\"C\":3}")]
    public void TokensPreserveShapeAndSelectedValueRanges(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var projection = ConfigFileTokenProjection.Parse(bytes);
        foreach (var entry in projection.Entries.Values)
        {
            using var value = JsonDocument.Parse(entry.Bytes.AsMemory(entry.ValueStart, entry.ValueLength));
            Assert.Equal(entry.Shape switch
            {
                ConfigFileValueShape.Array => JsonValueKind.Array,
                ConfigFileValueShape.Object => JsonValueKind.Object,
                ConfigFileValueShape.Null => JsonValueKind.Null,
                _ => value.RootElement.ValueKind
            }, value.RootElement.ValueKind);
            Assert.True(entry.Location.LineNumber > 0);
            Assert.True(entry.Location.ByteColumnNumber > 0);
            Assert.Equal(entry.Key.Value, entry.SourceSpelling);
        }
        if (json.Length > 0)
        {
            using var original = JsonDocument.Parse(bytes);
            using var bound = JsonDocument.Parse(projection.MaterializedRoot.ToJsonString());
            Assert.True(JsonElement.DeepEquals(original.RootElement, bound.RootElement));
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData(" ")]
    [InlineData("{")]
    [InlineData("{\"A\":")]
    [InlineData("{\"A\":[")]
    [InlineData("{\"A\": [1,]}")]
    [InlineData("{}{}")]
    public void MalformedDocumentsNeverPublishPartialProjection(string json) =>
        Assert.ThrowsAny<JsonException>(() => ConfigFileTokenProjection.Parse(Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData("{\"A\":{\"Bad:Key\":[{\"X\":1},[2]]},\"Sibling\":3}")]
    [InlineData("{\"A\":{\" bad\":4},\"Sibling\":3}")]
    [InlineData("{\"A\":{\"bad\\u0001\":4},\"Sibling\":3}")]
    public void UnrepresentableSubtreePoisonsItsAggregateButNotSibling(string json)
    {
        var projection = ConfigFileTokenProjection.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Contains(AppSurfaceConfigKey.Parse("A"), projection.InvalidKeys);
        Assert.Null(projection.Locations.GetLocation("A"));
        Assert.DoesNotContain(AppSurfaceConfigKey.Parse("Sibling"), projection.TerminalKeys);
    }

    [Theory]
    [InlineData("{\"A\":[[1]],\"A\":[[2]]}")]
    [InlineData("{\"A\":{\"B\":1,\"b\":2}}")]
    [InlineData("{\"A\":{\"B\":1},\"a\":{\"B\":2}}")]
    public void DuplicateTokensRetainEveryOccurrenceAndSuppressAncestorLocations(string json)
    {
        var projection = ConfigFileTokenProjection.Parse(Encoding.UTF8.GetBytes(json));
        Assert.True(projection.Occurrences.Count > projection.Entries.Count);
        Assert.Contains(AppSurfaceConfigKey.Parse("A"), projection.TerminalKeys);
        Assert.Null(projection.Locations.GetLocation("A"));
    }

    [Fact]
    public void BomIsExcludedFromFirstLineByteColumnAndInputIsCopyOwned()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"Port\":9}")).ToArray();
        var projection = ConfigFileTokenProjection.Parse(bytes);
        Array.Fill(bytes, (byte)0);
        var port = projection.Entries[AppSurfaceConfigKey.Parse("Port")];
        Assert.Equal(1, port.Location.LineNumber);
        Assert.Equal(2, port.Location.ByteColumnNumber);
        Assert.Equal("9", Encoding.UTF8.GetString(port.Bytes, port.ValueStart, port.ValueLength));
    }

    [Theory]
    [InlineData("{\"Root\":{\"Bad:Key\":1}}", "Root", "config-key-unrepresentable")]
    [InlineData("{\"Root\":{\"Port\":1,\"port\":2}}", "Root", "config-key-collision")]
    public void RuntimeAndAuditAgreeOnTerminalSubtrees(string json, string key, string code)
    {
        using var fixture = new Files(json);
        var request = new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse(key));
        var runtime = fixture.Provider.Resolve<object>(request);
        var audit = ((IConfigDiagnosticProvider)fixture.Provider).Resolve(request, typeof(object), ConfigAuditSourceRole.Base);
        Assert.Equal(ConfigProviderValueStatus.Terminal, runtime.Status);
        Assert.Equal(ConfigAuditEntryState.Invalid, audit.State);
        Assert.Equal(code, runtime.Diagnostic!.Code);
        Assert.Contains(audit.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Theory]
    [InlineData("Items:0", "first")]
    [InlineData("items:1:Name", "second")]
    [InlineData("Items:3", null)]
    [InlineData("Items:-1", null)]
    [InlineData("Items:bad", null)]
    [InlineData("Items:2", null)]
    public void ArrayIndexesUseLogicalSegments(string key, string? expected)
    {
        using var fixture = new Files("{\"Items\":[\"first\",{\"Name\":\"second\"},null]}");
        var result = fixture.Provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse(key)));
        Assert.Equal(expected is null ? ConfigProviderValueStatus.Missing : ConfigProviderValueStatus.Found, result.Status);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void ObsoleteShimPreservesStrictSemanticsAndTerminalFailures()
    {
        using var fixture = new Files("{\"A.B\":\"literal\",\"Number\":\"invalid\"}");
#pragma warning disable CS0618 // Deliberate compatibility shim contract.
        Assert.Equal("literal", fixture.Provider.GetValue<string>("Production", "A.B"));
        Assert.Null(fixture.Provider.GetValue<string>("Production", "A:B"));
        Assert.Throws<ConfigurationResolutionException>(() => fixture.Provider.GetValue<int>("Production", "Number"));
#pragma warning restore CS0618
    }

    [Theory]
    [InlineData("{}", "[]", "[]")]
    [InlineData("[]", "{}", "{}")]
    [InlineData("1", "{}", "{}")]
    [InlineData("{}", "1", "1")]
    [InlineData("1", "[2]", "[2]")]
    [InlineData("[1,2]", "3", "3")]
    [InlineData("[1,2]", "[]", "[]")]
    [InlineData("[1,2]", "[3]", "[3]")]
    [InlineData("{\"A\":1}", "{\"B\":2}", "{\"A\":1,\"B\":2}")]
    [InlineData("{\"A\":1}", "null", "{\"A\":1}")]
    public void LayeredShapesPreserveReplacementAndOriginRules(string lower, string upper, string expected)
    {
        using var fixture = new Files("{\"Shape\":" + lower + "}");
        fixture.Add("config_z.json", "{\"Shape\":" + upper + "}");
        var request = new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Shape"));
        var value = fixture.Provider.Resolve<JsonElement>(request);
        Assert.Equal(ConfigProviderValueStatus.Found, value.Status);
        using var expectedDocument = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(expectedDocument.RootElement, value.Value));
        var audit = ((IConfigDiagnosticProvider)fixture.Provider).Resolve(request, typeof(JsonElement), ConfigAuditSourceRole.Base);
        Assert.Equal(upper == "null" ? new[] { "appsettings.json" } : new[] { "config_z.json", "appsettings.json" },
            audit.Sources.Select(source => Path.GetFileName(source.FilePath)));
        Assert.All(audit.Sources, source => Assert.NotNull(source.Location));
        if (!expected.Contains("A", StringComparison.Ordinal))
            Assert.DoesNotContain(audit.AuditSources, source => source.ConfigPath == "Shape:A");
        if (upper == "[3]")
            Assert.DoesNotContain(audit.AuditSources, source => source.ConfigPath == "Shape:1");
        if (lower.StartsWith('[') && expectedDocument.RootElement.ValueKind != JsonValueKind.Array)
            Assert.DoesNotContain(audit.AuditSources, source => source.ConfigPath is "Shape:0" or "Shape:1");
    }

    [Fact]
    public void RealManagerStopsAtFileCollisionButUnrelatedSiblingStillResolves()
    {
        using var fixture = new Files("{\"Shape\":{\"Port\":1,\"port\":1},\"Sibling\":7}");
        var lower = new CountingFallback();
        var manager = new DefaultConfigManager(new EnvironmentConfigProvider(new EmptyEnvironment()), [fixture.Provider, lower],
            NullLogger<DefaultConfigManager>.Instance);
        var error = Assert.Throws<ConfigurationResolutionException>(() => manager.GetValue<object>("Production", AppSurfaceConfigKey.Parse("Shape")));
        Assert.Equal("config-key-collision", error.Diagnostic.Code);
        Assert.Equal(0, lower.Calls);
        Assert.Equal(7, manager.GetValue<int>("Production", AppSurfaceConfigKey.Parse("Sibling")));
        Assert.Equal(0, lower.Calls);
    }

    [Fact]
    public void OrderedCaseCollisionPoisonsAggregateEvenWhenOnlyDescendantCaseChanged()
    {
        using var fixture = new Files("{\"Shape\":{\"Port\":1},\"Sibling\":7}");
        fixture.Add("config_z.json", "{\"Shape\":{\"port\":2}}");
        var result = fixture.Provider.Resolve<object>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Shape")));
        Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
        Assert.Equal("config-key-collision", result.Diagnostic!.Code);
        Assert.Equal(7, fixture.Provider.Resolve<int>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Sibling"))).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualFileLimitsAreTerminalWithoutPublishingAPartialEnvironment(bool countLimit)
    {
        using var fixture = new Files("{\"Value\":1}", new ConfigResourceOptions
        {
            MaxFilesPerEnvironment = countLimit ? 1 : 256,
            MaxFileBytes = countLimit ? 1024 : 16
        });
        fixture.Add("config_z.json", "{\"TooLongForLimit\":2}");
        fixture.Add("appsettings.Development.json", "{\"Value\":3}");
        var request = new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Value"));
        var runtime = fixture.Provider.Resolve<int>(request);
        var audit = ((IConfigDiagnosticProvider)fixture.Provider).Resolve(request, typeof(int), ConfigAuditSourceRole.Base);
        Assert.Equal(ConfigProviderValueStatus.Terminal, runtime.Status);
        Assert.Equal(countLimit ? "config-file-count-limit" : "config-file-byte-limit", runtime.Diagnostic!.Code);
        Assert.Equal(ConfigAuditEntryState.Invalid, audit.State);
        Assert.Null(audit.Value);
        Assert.Empty(((IConfigAuditKeyEnumerator)fixture.Provider).EnumerateKeys("Production"));
        Assert.Equal(3, fixture.Provider.Resolve<int>(new ConfigProviderRequest("Development", AppSurfaceConfigKey.Parse("Value"))).Value);
    }

    [Fact]
    public void BoundedReaderAcceptsExactBoundaryAndRejectsGrowingInput()
    {
        using var exact = new MemoryStream(new byte[8192]);
        Assert.Equal(8192, FileBasedConfigProvider.ReadBounded(exact, 8192).Length);
        using var unlimited = new MemoryStream(new byte[1]);
        Assert.Single(FileBasedConfigProvider.ReadBounded(unlimited, long.MaxValue));
        using var over = new MemoryStream(new byte[8193]);
        Assert.Equal("config-file-byte-limit", Assert.Throws<ConfigResourceLimitException>(() => FileBasedConfigProvider.ReadBounded(over, 8192)).Code);
    }

    [Fact]
    public void NullDeserializerResultsRemainMissingInRuntimeAndAudit()
    {
        using var fixture = new Files("{\"Value\":{}}");
        var request = new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Value"));
        Assert.Equal(ConfigProviderValueStatus.Missing, fixture.Provider.Resolve<NullObject>(request).Status);
        var audit = ((IConfigDiagnosticProvider)fixture.Provider).Resolve(request, typeof(NullObject), ConfigAuditSourceRole.Base);
        Assert.Equal(ConfigAuditEntryState.Missing, audit.State);
        Assert.Contains(audit.Diagnostics, diagnostic => diagnostic.Code == "config-file-null-value");
    }

    [Fact]
    public void DiscoveryRetainsOverridesAndWithholdsCollidingValues()
    {
        using var fixture = new Files("{\"Value\":1,\"Bad\":\"sentinel\",\"bad\":\"sentinel\"}");
        fixture.Add("config_z.json", "{\"Value\":2}");
        var discovered = ((IConfigAuditKeyEnumerator)fixture.Provider).EnumerateKeys("Production");
        var value = Assert.Single(discovered, entry => entry.LogicalKey.Equals(AppSurfaceConfigKey.Parse("Value")));
        Assert.Equal(new[] { "config_z.json", "appsettings.json" }, value.Sources.Select(source => Path.GetFileName(source.FilePath)));
        var bad = Assert.Single(discovered, entry => entry.LogicalKey.Equals(AppSurfaceConfigKey.Parse("Bad")));
        Assert.Null(bad.RawValue);
        Assert.Contains(bad.Diagnostics, diagnostic => diagnostic.Code == "config-key-collision");
        Assert.All(bad.Diagnostics, diagnostic => Assert.DoesNotContain("sentinel", diagnostic.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void FileLookupHonorsCancellationBeforeReadingTheSnapshot()
    {
        using var fixture = new Files("{}");
        using var scope = new ConfigResolutionScope(new CancellationToken(canceled: true));
        var request = new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Value"), scope);
        Assert.Throws<OperationCanceledException>(() => fixture.Provider.Resolve<string>(request));
        Assert.Throws<OperationCanceledException>(() => ((IConfigDiagnosticProvider)fixture.Provider)
            .Resolve(request, typeof(string), ConfigAuditSourceRole.Base));
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData("a/b")]
    [InlineData("a[b]")]
    public void DictionaryLiteralSegmentsKeepExactFileIdentityAndDisplayLabels(string segment)
    {
        using var fixture = new Files(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Catalog.Root"] = new Dictionary<string, Item> { [segment] = new() { Name = "visible" } }
        }));
        using var services = new ServiceCollection().BuildServiceProvider();
        var known = new ConfigAuditKnownEntry(AppSurfaceConfigKey.FromSegments("Catalog.Root"), null,
            typeof(Dictionary<string, Item>), new ConfigAuditEntryOptions { TraverseCollectionElements = true });
        var reporter = new ConfigAuditReporter(new EnvironmentConfigProvider(new EmptyEnvironment()), [fixture.Provider], [known],
            services, new ConfigAuditRedactor(), Options.Create(new ConfigAuditDictionaryKeyCorrelationOptions()));
        var root = Assert.Single(reporter.GetReport("Production").Entries);
        Assert.Equal("Catalog.Root", root.ConfigPath);
        var item = Assert.Single(root.Children);
        Assert.Equal($"Catalog.Root[\"{segment}\"]", item.Key);
        Assert.Equal($"Catalog.Root:{segment}", item.ConfigPath);
        Assert.NotNull(Assert.Single(item.Sources).Location);
        var name = Assert.Single(item.Children);
        Assert.Equal($"Catalog.Root[\"{segment}\"].Name", name.Key);
        Assert.Equal($"Catalog.Root:{segment}:Name", name.ConfigPath);
        Assert.Equal("visible", name.DisplayValue);
        var source = Assert.Single(name.Sources);
        Assert.Equal(name.ConfigPath, source.ConfigPath);
        Assert.NotNull(source.Location);
    }

    [Fact]
    public void HiddenDictionaryKeysNeverReachCapturedChildIdentityOrInheritedDescendants()
    {
        const string hidden = "customer-private-sentinel";
        using var fixture = new Files(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Catalog"] = new Dictionary<string, Item> { [hidden] = new() { Name = "visible" } }
        }));
        using var services = new ServiceCollection().BuildServiceProvider();
        var known = new ConfigAuditKnownEntry(AppSurfaceConfigKey.Parse("Catalog"), null,
            typeof(Dictionary<string, Item>), new ConfigAuditEntryOptions { TraverseCollectionElements = true, DisplayDictionaryKeys = false });
        var reporter = new ConfigAuditReporter(new EnvironmentConfigProvider(new EmptyEnvironment()), [fixture.Provider], [known],
            services, new ConfigAuditRedactor(), Options.Create(new ConfigAuditDictionaryKeyCorrelationOptions()));
        var root = Assert.Single(reporter.GetReport("Production").Entries);
        var item = Assert.Single(root.Children);
        Assert.Null(item.ConfigPath);
        Assert.True(item.Element!.IsKeyRedacted);
        Assert.Null(Assert.Single(item.Children).ConfigPath);
        Assert.DoesNotContain(hidden, JsonSerializer.Serialize(root), StringComparison.Ordinal);
    }

    public sealed class Item
    {
        public string Name { get; set; } = string.Empty;
    }

    [JsonConverter(typeof(NullObjectConverter))]
    public sealed class NullObject;

    public sealed class NullObjectConverter : JsonConverter<NullObject>
    {
        public override NullObject? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            reader.Skip();
            return null;
        }
        public override void Write(Utf8JsonWriter writer, NullObject value, JsonSerializerOptions options) => writer.WriteNullValue();
    }

    private sealed class CountingFallback : IConfigProvider
    {
        public int Calls { get; private set; }
        public string Name => "Lower";
        public int Priority => -100;
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            Calls++;
            return ConfigProviderValueResult<T>.Missing();
        }
    }
    private sealed class EmptyEnvironment : IEnvironmentProvider
    {
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>();
    }

    private sealed class Files : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "config-file-projection-" + Guid.NewGuid().ToString("N"));
        public FileBasedConfigProvider Provider { get; }
        public Files(string json, ConfigResourceOptions? limits = null)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "appsettings.json"), json);
            Provider = new FileBasedConfigProvider(new Location(_directory), NullLogger<FileBasedConfigProvider>.Instance,
                Options.Create(limits ?? new ConfigResourceOptions()));
        }
        public void Add(string name, string json) => File.WriteAllText(Path.Combine(_directory, name), json);
        public void Dispose() => Directory.Delete(_directory, true);
    }

    private sealed record Location(string Directory) : IConfigFileLocationProvider;
}
