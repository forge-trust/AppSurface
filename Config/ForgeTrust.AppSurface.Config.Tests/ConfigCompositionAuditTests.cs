using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigCompositionAuditTests
{
    private const string KeySentinel = "opaque-key-sentinel";
    private const string VersionSentinel = "opaque-version-sentinel";
    private const string ResourceSentinel = "projects/opaque-project-sentinel/secrets/opaque-resource-sentinel/versions/19";
    private const string PayloadSentinel = "resolved-payload-sentinel";
    private const string EnvironmentSentinel = "environment-payload-sentinel";

    [Fact]
    public void Reporter_ResolvesRealFileDeclarationOnceAndRetainsRedactedFileInventory()
    {
        using var host = new HostingEnvironment(FileDocument(Descriptor()));
        var provider = host.AddProvider("test-provider");
        var reporter = host.CreateReporter<AuditOptions>();

        var report = reporter.GetReport(host.Environment);

        Assert.Equal(1, provider.ResolveCalls);
        Assert.Equal(new[] { "test-provider:Service:access" }, host.ResolveSequence);
        var root = Assert.Single(report.Entries);
        Assert.Equal(ConfigAuditEntryState.PartiallyResolved, root.State);
        AssertOpaqueSlot(Child(root, "Service.access"), "secret-resolved", hasValue: true);
        Assert.Equal("us-east-1", Child(root, "Service.region").DisplayValue);
        Assert.Contains(report.Providers, item => item.Name == nameof(FileBasedConfigProvider));
        Assert.Contains(report.DiscoveredKeys, item => item.Key == "Unrelated.region");
        AssertDescriptorInventory(report, "Service.access");
        AssertSafeReport(report);

        // Effective audits are fresh observations; serializing/rendering an existing report does no I/O.
        Assert.Equal(1, provider.ResolveCalls);
        provider.Outcome = "missing";
        var laterReport = reporter.GetReport(host.Environment);
        Assert.Equal(2, provider.ResolveCalls);
        Assert.Equal(ConfigAuditEntryState.Invalid, Assert.Single(laterReport.Entries).State);
        AssertOpaqueSlot(Child(Assert.Single(laterReport.Entries), "Service.access"), "secret-not-found", hasValue: false);
        Assert.Equal(ConfigAuditEntryState.PartiallyResolved, root.State);
        AssertDescriptorInventory(laterReport, "Service.access");
        AssertSafeReport(laterReport);
    }

    [Theory]
    [InlineData("unique", "secret-resolved", false, false)]
    [InlineData("missing", "secret-not-found", true, false)]
    [InlineData("denied", "secret-provider-access-denied", true, false)]
    [InlineData("unavailable", "secret-provider-unavailable", true, false)]
    [InlineData("invalid-reference", "secret-reference-invalid", true, false)]
    [InlineData("thrown", "secret-provider-failed", true, false)]
    [InlineData("ambiguous", "secret-provider-ambiguous", true, false)]
    [InlineData("denied", "secret-environment-rescued", false, true)]
    public void RuntimeAndAudit_UseTheSameProviderSequenceAndEffectiveOutcome(
        string outcome, string code, bool failed, bool rescue)
    {
        using var host = new HostingEnvironment(FileDocument(Descriptor()));
        // Reverse registration makes canonical ordering and terminal short-circuiting observable.
        var last = host.AddProvider("z-provider", outcome == "ambiguous" ? "resolved" : "missing");
        var first = host.AddProvider("a-provider", outcome is "unique" or "ambiguous" ? "resolved" : outcome);
        if (rescue) host.Variables["PRODUCTION__SERVICE__ACCESS"] = EnvironmentSentinel;
        var reporter = host.CreateReporter<AuditOptions>();
        var manager = host.CreateManager();

        if (failed)
        {
            var failure = Assert.Throws<ConfigurationCompositionException>(() => manager.GetValue<AuditOptions>(host.Environment, "Service"));
            Assert.Equal(code, Assert.Single(failure.Failures).Code);
        }
        else
        {
            var value = Assert.IsType<AuditOptions>(manager.GetValue<AuditOptions>(host.Environment, "Service"));
            Assert.True(value.Access.HasValue);
            Assert.Equal(rescue ? nameof(EnvironmentConfigProvider) : first.Id, value.Access.ResolvedProvider);
        }
        var runtimeSequence = host.ResolveSequence.ToArray();
        var terminal = outcome is "denied" or "unavailable" or "invalid-reference" or "thrown";
        Assert.Equal(terminal ? new[] { "a-provider:Service:access" } :
            new[] { "a-provider:Service:access", "z-provider:Service:access" }, runtimeSequence);
        host.ResolveSequence.Clear();

        var report = reporter.GetReport(host.Environment);

        Assert.Equal(runtimeSequence, host.ResolveSequence);
        Assert.Equal(2, first.ResolveCalls);
        Assert.Equal(terminal ? 0 : 2, last.ResolveCalls);
        var root = Assert.Single(report.Entries);
        Assert.Equal(failed ? ConfigAuditEntryState.Invalid : ConfigAuditEntryState.PartiallyResolved, root.State);
        var slot = Child(root, "Service.access");
        AssertOpaqueSlot(slot, code, hasValue: !failed);
        if (failed) Assert.Contains(root.Diagnostics, diagnostic => diagnostic.Code == code);
        else Assert.Contains(slot.Sources, source => source.ProviderName == (rescue ? nameof(EnvironmentConfigProvider) : first.Id));
        AssertDescriptorInventory(report, "Service.access");
        AssertSafeReport(report);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRoot_DoesNotConstructInspectOrDefaultWrapperOrResolveAgain(bool invalidDeclaration)
    {
        using var host = new HostingEnvironment(FileDocument(Descriptor(invalid: invalidDeclaration)));
        var provider = host.AddProvider("test-provider", "missing");

        var report = host.CreateReporter<AuditOptions>(typeof(InspectionProbeConfig)).GetReport(host.Environment);

        var root = Assert.Single(report.Entries);
        Assert.Equal(ConfigAuditEntryState.Invalid, root.State);
        Assert.Null(root.DisplayValue);
        Assert.DoesNotContain(root.Sources, source => source.Kind == ConfigAuditSourceKind.Default);
        Assert.Equal(0, host.Inspection.Constructions);
        Assert.Equal(0, host.Inspection.Inspections);
        Assert.Equal(0, host.Inspection.DefaultReads);
        Assert.Equal(invalidDeclaration ? 0 : 1, provider.ResolveCalls);
        Assert.Contains(root.Diagnostics, diagnostic => diagnostic.Code ==
            (invalidDeclaration ? "secret-descriptor-invalid" : "secret-not-found"));
        AssertDescriptorInventory(report, "Service.access");
        AssertSafeReport(report);
    }

    [Fact]
    public void ResolvedRoot_InspectsWrapperOnceWithoutRepeatingResolutionOrReadingDefault()
    {
        using var host = new HostingEnvironment(FileDocument(Descriptor()));
        var provider = host.AddProvider("test-provider");

        var report = host.CreateReporter<AuditOptions>(typeof(InspectionProbeConfig)).GetReport(host.Environment);

        Assert.Equal(1, host.Inspection.Constructions);
        Assert.Equal(1, host.Inspection.Inspections);
        Assert.Equal(0, host.Inspection.DefaultReads);
        Assert.Equal(1, provider.ResolveCalls);
        AssertOpaqueSlot(Child(Assert.Single(report.Entries), "Service.access"), "secret-resolved", hasValue: true);
        AssertSafeReport(report);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledDeclaration_ValidatesLocallyButNeverReadsRemote(bool environmentValue)
    {
        using var host = new HostingEnvironment(FileDocument(Descriptor(enabled: false)));
        var provider = host.AddProvider("test-provider", "denied");
        if (environmentValue) host.Variables["SERVICE__ACCESS"] = EnvironmentSentinel;
        var reporter = host.CreateReporter<AuditOptions>();

        var runtime = Assert.IsType<AuditOptions>(host.CreateManager().GetValue<AuditOptions>(host.Environment, "Service"));
        var report = reporter.GetReport(host.Environment);

        Assert.False(runtime.Access.Enabled);
        Assert.Equal(environmentValue, runtime.Access.HasValue);
        Assert.Equal(environmentValue ? nameof(EnvironmentConfigProvider) : null, runtime.Access.ResolvedProvider);
        Assert.True(provider.ValidateCalls > 0);
        Assert.Equal(0, provider.ResolveCalls);
        Assert.Empty(host.ResolveSequence);
        AssertOpaqueSlot(Child(Assert.Single(report.Entries), "Service.access"),
            environmentValue ? "secret-environment-supplied" : "secret-declared-disabled", environmentValue);
        AssertDescriptorInventory(report, "Service.access");
        AssertSafeReport(report);
    }

    [Fact]
    public void DirectEnvironmentRoot_BypassesInvalidFilePlanAndAllRemoteReadsAndDescendants()
    {
        using var host = new HostingEnvironment(FileDocument(Descriptor(invalid: true)));
        var provider = host.AddProvider("test-provider", "denied");
        host.Variables["PRODUCTION_SERVICE"] = JsonSerializer.Serialize(new { access = EnvironmentSentinel, region = "root-region" });
        host.Variables["SERVICE__ACCESS"] = PayloadSentinel;
        host.Variables["SERVICE__REGION"] = "descendant-region";
        var reporter = host.CreateReporter<AuditOptions>();

        var runtime = Assert.IsType<AuditOptions>(host.CreateManager().GetValue<AuditOptions>(host.Environment, "Service"));
        var report = reporter.GetReport(host.Environment);

        Assert.True(runtime.Access.TryGetValue(out var value));
        Assert.True(value == EnvironmentSentinel, "The direct root must suppress the descendant payload.");
        Assert.Equal("root-region", runtime.Region);
        Assert.Equal(0, provider.ValidateCalls);
        Assert.Equal(0, provider.ResolveCalls);
        var root = Assert.Single(report.Entries);
        Assert.Equal(ConfigAuditEntryState.Resolved, root.State);
        Assert.Empty(root.Diagnostics);
        var slot = Child(root, "Service.access");
        AssertOpaqueSlot(slot, "secret-direct-environment-root", hasValue: true);
        Assert.Contains(slot.Sources, source => source.EnvironmentVariableName == "PRODUCTION_SERVICE");
        AssertDescriptorInventory(report, "Service.access");
        AssertSafeReport(report);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedRenamedPropertyAndIncludedField_KeepSecretUnderItsSerializedParent(bool failed)
    {
        using var host = new HostingEnvironment($$$$"""
            {"Service":{"channel":{"access":{{{{Descriptor()}}}},"region":"us-east-1"}}}
            """);
        var provider = host.AddProvider("test-provider", failed ? "missing" : "resolved");

        var report = host.CreateReporter<NestedOptions>().GetReport(host.Environment);

        Assert.Equal(1, provider.ResolveCalls);
        Assert.Equal(new[] { "test-provider:Service:channel:access" }, host.ResolveSequence);
        var root = Assert.Single(report.Entries);
        var channel = Assert.Single(root.Children);
        Assert.Equal("Service.channel", channel.Key);
        var slot = Child(channel, "Service.channel.access");
        AssertOpaqueSlot(slot, failed ? "secret-not-found" : "secret-resolved", hasValue: !failed);
        Assert.DoesNotContain(root.Children, child => child.Key == slot.Key);
        Assert.DoesNotContain(Flatten(root), entry => entry.Key.Contains("Credential", StringComparison.Ordinal)
            || entry.Key.Contains("Settings", StringComparison.Ordinal));
        if (!failed) Assert.Equal("us-east-1", Child(channel, "Service.channel.region").DisplayValue);
        AssertDescriptorInventory(report, "Service.channel.access");
        AssertSafeReport(report);
    }

    [Theory]
    [InlineData("shape")]
    [InlineData("provider")]
    [InlineData("missing-key")]
    public void InvalidDeclaration_RedactsBenignInventoryDescendantsAndAllSerializedReportSurfaces(string invalidKind)
    {
        var descriptor = invalidKind == "missing-key"
            ? JsonSerializer.Serialize(new { version = VersionSentinel, resource = ResourceSentinel, extra = PayloadSentinel })
            : Descriptor(invalid: invalidKind == "shape");
        using var host = new HostingEnvironment(FileDocument(descriptor));
        var provider = host.AddProvider("test-provider");
        provider.InvalidReference = invalidKind == "provider";

        var report = host.CreateReporter<AuditOptions>().GetReport(host.Environment);

        Assert.False(ConfigAuditRedactor.ContainsSensitiveFragment("Service.access.version"));
        Assert.Equal(ConfigAuditEntryState.Invalid, Assert.Single(report.Entries).State);
        Assert.Equal(0, provider.ResolveCalls);
        AssertDescriptorInventory(report, "Service.access");
        AssertSafeReport(report);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DescriptorIdentity_RemainsRedactedWhenIndependentlyRegisteredAndRootContractIsInvalid(bool unsupported)
    {
        using var host = new HostingEnvironment(FileDocument(Descriptor()));
        host.AddProvider("test-provider");
        var knownVersion = new ConfigAuditKnownEntry("Service.access.version", null, typeof(string));
        var reporter = unsupported
            ? host.CreateReporter<UnsupportedCollectionOptions>(additionalEntries: [knownVersion])
            : host.CreateReporter<AuditOptions>(additionalEntries: [knownVersion]);

        var report = reporter.GetReport(host.Environment);

        var version = Assert.Single(report.Entries, entry => entry.Key == knownVersion.Key);
        Assert.True(version.IsRedacted);
        Assert.Equal(ConfigAuditRedactor.Placeholder, version.DisplayValue);
        AssertDescriptorInventory(report, "Service.access");
        AssertSafeReport(report);
    }

    private sealed class UnsupportedCollectionOptions
    {
        [JsonPropertyName("access")]
        public Secret<string>[] Access { get; set; } = [];
    }

    [Fact]
    public void MissingRoot_RendersOnlyDeclaredSecretHierarchyWithoutSynthesizingOrdinaryMembers()
    {
        using var host = new HostingEnvironment("""{"Unrelated":{"region":"inventory-region"}}""");
        var provider = host.AddProvider("test-provider");
        var reporter = host.CreateReporter<NestedOptions>();

        Assert.Null(host.CreateManager().GetValue<NestedOptions>(host.Environment, "Service"));
        var report = reporter.GetReport(host.Environment);

        var root = Assert.Single(report.Entries);
        Assert.Equal(ConfigAuditEntryState.Missing, root.State);
        Assert.Null(root.DisplayValue);
        var channel = Assert.Single(root.Children);
        Assert.Equal("Service.channel", channel.Key);
        var access = Assert.Single(channel.Children);
        Assert.Equal("Service.channel.access", access.Key);
        Assert.Equal(ConfigAuditEntryState.Missing, access.State);
        AssertOpaqueSlot(access, "secret-descriptor-absent", hasValue: false);
        Assert.DoesNotContain(Flatten(root), entry => entry.Key.EndsWith(".region", StringComparison.Ordinal));
        Assert.Equal(0, provider.ValidateCalls);
        Assert.Equal(0, provider.ResolveCalls);
        Assert.Contains(report.DiscoveredKeys, item => item.Key == "Unrelated.region");
        AssertSafeReport(report);
    }

    [Fact]
    public void TraversalAndFormatting_NeverReadOpaquePayloadGetterOrCallToString()
    {
        var opaque = new OpaqueProbe();
        var redactor = new ConfigAuditRedactor();
        var traverser = new ConfigAuditValueTraverser(redactor);

        var result = Traverse(traverser, opaque);
        var display = redactor.FormatValue("Service.access", opaque, []);

        Assert.Empty(result.Children);
        Assert.Empty(result.Diagnostics);
        Assert.True(display.IsRedacted);
        Assert.Equal(ConfigAuditRedactor.Placeholder, display.DisplayValue);
        Assert.Equal(0, opaque.ValueReads);
        Assert.Equal(0, opaque.FormattingCalls);
        // The real empty wrapper's Value getter throws; it must not generate a swallowed read-failure diagnostic.
        var empty = Traverse(traverser, new Secret<string>());
        Assert.Empty(empty.Children);
        Assert.Empty(empty.Diagnostics);
    }

    [Fact]
    public void Traversal_DoesNotInvokeTheSecretDestinationMemberGetter()
    {
        var model = new MemberGetterProbe();
        var slot = new ConfigSecretSlotTrace("Service:access", false, false, null, null,
            "secret-declared-disabled", [], [], null);
        var result = new ConfigAuditValueTraverser(new ConfigAuditRedactor(), [slot]).BuildChildren(
            ConfigAuditPath.Root("Service"), model, [], ConfigAuditFactContext.Empty,
            new ConfigAuditEntryOptions(), new HashSet<object>(ReferenceEqualityComparer.Instance),
            new ConfigAuditDictionaryLabelSet(), ConfigAuditDictionaryKeyCorrelationContext.Unavailable("test"));

        Assert.Equal(0, model.Reads);
        Assert.Empty(result.Diagnostics);
        AssertOpaqueSlot(Assert.Single(result.Children), "secret-declared-disabled", hasValue: false);
        Assert.DoesNotContain(result.Children.SelectMany(Flatten), child => child.Key.EndsWith(".Value", StringComparison.Ordinal));
    }

    [Fact]
    public void OrdinaryRoot_DoesNotInvokeSecretCompositionProviders()
    {
        using var host = new HostingEnvironment("""{"Region":"us-east-1"}""");
        var provider = host.AddProvider("test-provider");

        var report = host.CreateReporter<string>(key: "Region").GetReport(host.Environment);

        Assert.Equal("us-east-1", Assert.Single(report.Entries).DisplayValue);
        Assert.Equal(0, provider.ValidateCalls);
        Assert.Equal(0, provider.ResolveCalls);
        Assert.Contains(report.DiscoveredKeys, key => key.Key == "Region");
    }

    private static string Descriptor(bool enabled = true, bool invalid = false) => invalid
        ? JsonSerializer.Serialize(new { key = KeySentinel, version = VersionSentinel, enabled, resource = ResourceSentinel, extra = PayloadSentinel })
        : JsonSerializer.Serialize(new { key = $"{ResourceSentinel}/{KeySentinel}", version = VersionSentinel, enabled });

    private static string FileDocument(string descriptor) => $$$$"""
        {"Service":{"access":{{{{descriptor}}}},"region":"us-east-1"},"Unrelated":{"region":"inventory-region"}}
        """;

    private static ConfigAuditEntry Child(ConfigAuditEntry parent, string path) => Assert.Single(parent.Children, child => child.Key == path);
    private static IEnumerable<ConfigAuditEntry> Flatten(ConfigAuditEntry entry) => new[] { entry }.Concat(entry.Children.SelectMany(Flatten));

    private static void AssertOpaqueSlot(ConfigAuditEntry slot, string code, bool hasValue)
    {
        Assert.Empty(slot.Children);
        Assert.Equal(hasValue ? ConfigAuditRedactor.Placeholder : null, slot.DisplayValue);
        Assert.True(slot.IsRedacted);
        Assert.Contains(slot.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.DoesNotContain(slot.Diagnostics, diagnostic => diagnostic.Code == "config-composition-slot-state");
    }

    private static void AssertDescriptorInventory(ConfigAuditReport report, string path)
    {
        var version = Assert.Single(report.DiscoveredKeys, item => item.Key == $"{path}.version");
        Assert.True(version.IsRedacted);
        Assert.Equal(ConfigAuditDiscoveredValueDisplayState.Redacted, version.ValueDisplayState);
        Assert.Equal(ConfigAuditRedactor.Placeholder, version.DisplayValue);
        Assert.Contains(version.Sources, source => source.Kind == ConfigAuditSourceKind.File && Path.GetFileName(source.FilePath) == "appsettings.json");
        var inventory = report.DiscoveredKeys.Where(item => item.Key.StartsWith($"{path}.", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(inventory);
        Assert.All(inventory, item =>
        {
            Assert.True(item.IsRedacted);
            Assert.Equal(ConfigAuditRedactor.Placeholder, item.DisplayValue);
        });
    }

    private static void AssertSafeReport(ConfigAuditReport report)
    {
        var surfaces = new[]
        {
            JsonSerializer.Serialize(report),
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new ConfigAuditTextRenderer().Render(report)
        };
        foreach (var surface in surfaces)
        {
            foreach (var sentinel in new[] { KeySentinel, VersionSentinel, ResourceSentinel, PayloadSentinel, EnvironmentSentinel })
                Assert.False(surface.Contains(sentinel, StringComparison.Ordinal), "Audit output must not include descriptor or payload sentinels.");
        }
    }

    private static ConfigAuditTraversalResult Traverse(ConfigAuditValueTraverser traverser, object value) =>
        traverser.BuildChildren(ConfigAuditPath.Root("Service"), value, [], ConfigAuditFactContext.Empty,
            new ConfigAuditEntryOptions(), new HashSet<object>(ReferenceEqualityComparer.Instance),
            new ConfigAuditDictionaryLabelSet(), ConfigAuditDictionaryKeyCorrelationContext.Unavailable("test"));

    private sealed class AuditOptions
    {
        [JsonPropertyName("access")]
        public Secret<string> Access { get; init; } = new();
        [JsonPropertyName("region")]
        public string Region { get; init; } = "";
    }

    private sealed class NestedOptions
    {
        [JsonPropertyName("channel")]
        public ChannelOptions Settings { get; init; } = new();
    }

    private sealed class ChannelOptions
    {
        [JsonInclude, JsonPropertyName("access")]
        public Secret<string> Credential = new();
        [JsonPropertyName("region")]
        public string Region { get; init; } = "";
    }

    private sealed class MemberGetterProbe
    {
        [JsonIgnore]
        public int Reads { get; private set; }
        [JsonPropertyName("access")]
        public Secret<string> Access
        {
            get
            {
                Reads++;
                throw new InvalidOperationException("The secret destination getter must not run during audit.");
            }
            set { }
        }
    }

    private sealed class InspectionCounts
    {
        public int Constructions { get; set; }
        public int Inspections { get; set; }
        public int DefaultReads { get; set; }
    }

    private sealed class InspectionProbeConfig : Config<AuditOptions>, IConfigInspectable
    {
        private readonly InspectionCounts _counts;
        public InspectionProbeConfig(InspectionCounts counts)
        {
            _counts = counts;
            counts.Constructions++;
        }
        public override AuditOptions DefaultValue
        {
            get
            {
                _counts.DefaultReads++;
                return new AuditOptions();
            }
        }
        ConfigWrapperInspection IConfigInspectable.Inspect(string key, object? rawValue, ConfigAuditEntryState resolutionState)
        {
            _counts.Inspections++;
            return new(rawValue ?? DefaultValue, resolutionState, null, []);
        }
    }

    private sealed class OpaqueProbe : IConfigSecretValue
    {
        public bool Enabled => true;
        public bool HasValue => true;
        public string ResolvedProvider => "test-provider";
        public int ValueReads { get; private set; }
        public int FormattingCalls { get; private set; }
        public string Value
        {
            get
            {
                ValueReads++;
                throw new InvalidOperationException("Opaque payload access is forbidden during audit.");
            }
        }
        public override string ToString()
        {
            FormattingCalls++;
            throw new InvalidOperationException("Opaque formatting is forbidden during audit.");
        }
    }

    private sealed class CountingSecretProvider(string id, string outcome, List<string> sequence) : IConfigSecretProvider
    {
        public string Id => id;
        public string Outcome { get; set; } = outcome;
        public int ValidateCalls { get; private set; }
        public int ResolveCalls { get; private set; }
        public bool InvalidReference { get; set; }
        public ConfigSecretReferenceValidation ValidateReference(ConfigSecretReference reference)
        {
            ValidateCalls++;
            return InvalidReference ? ConfigSecretReferenceValidation.Invalid() : ConfigSecretReferenceValidation.Supported();
        }
        public ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context)
        {
            ResolveCalls++;
            sequence.Add($"{Id}:{reference.LogicalPath}");
            return Outcome switch
            {
                "missing" => ConfigSecretProviderResolution.Missing(Id),
                "denied" => ConfigSecretProviderResolution.AccessDenied(Id),
                "unavailable" => ConfigSecretProviderResolution.Unavailable(Id),
                "invalid-reference" => ConfigSecretProviderResolution.InvalidReference(Id),
                "thrown" => throw new InvalidOperationException(PayloadSentinel),
                _ => ConfigSecretProviderResolution.Resolved(PayloadSentinel, ConfigSecretSourceMetadata.Create(Id))
            };
        }
    }

    /// <summary>Owns a real file provider and case-sensitive environment without touching process-global variables.</summary>
    private sealed class HostingEnvironment : IEnvironmentProvider, IConfigFileLocationProvider, IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly FileBasedConfigProvider _file;
        private readonly EnvironmentConfigProvider _environment;
        private readonly List<IConfigSecretProvider> _providers = [];
        private ConfigCompositionEngine? _engine;
        public HostingEnvironment(string document)
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("appsurface-composition-audit-").FullName;
            File.WriteAllText(Path.Join(Directory, "appsettings.json"), document);
            _file = new FileBasedConfigProvider(this, NullLogger<FileBasedConfigProvider>.Instance);
            _environment = new EnvironmentConfigProvider(this);
            _services = new ServiceCollection().AddSingleton(Inspection).BuildServiceProvider();
        }
        public string Directory { get; }
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);
        public List<string> ResolveSequence { get; } = [];
        public InspectionCounts Inspection { get; } = new();
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) =>
            Variables.TryGetValue(name, out var value) ? value : defaultValue;
        public CountingSecretProvider AddProvider(string id, string outcome = "resolved")
        {
            var provider = new CountingSecretProvider(id, outcome, ResolveSequence);
            _providers.Add(provider);
            return provider;
        }
        public ConfigAuditReporter CreateReporter<T>(Type? wrapper = null, string key = "Service", params ConfigAuditKnownEntry[] additionalEntries) =>
            new(_environment, [_file], [new ConfigAuditKnownEntry(key, wrapper, typeof(T)), .. additionalEntries], _services,
                new ConfigAuditRedactor(), Options.Create(new ConfigAuditDictionaryKeyCorrelationOptions()), Engine);
        public IConfigManager CreateManager() => new DefaultConfigManager(_environment, [_file], NullLogger<DefaultConfigManager>.Instance, Engine);
        private ConfigCompositionEngine Engine => _engine ??= new(_environment, [_file], _providers, [], new AppSurfaceConfigOptions(), TimeProvider.System);
        public void Dispose()
        {
            _services.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
