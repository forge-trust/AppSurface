using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Config.Tests;

public class ConfigAuditModelsTests
{
    [Fact]
    public void ConfigAuditKnownEntry_RejectsInvalidConstructorArguments()
    {
        Assert.Throws<ArgumentException>(() => new ConfigAuditKnownEntry("", null, typeof(string)));
        Assert.Throws<ArgumentNullException>(() => new ConfigAuditKnownEntry("Valid.Key", null, null!));
    }

    [Fact]
    public void ConfigAuditSourceLocation_RejectsInvalidConstructorArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigAuditSourceLocation(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigAuditSourceLocation(1, 0));
    }

    [Fact]
    public void ConfigAuditSourceRecord_DefaultsLocationToNull()
    {
        var source = new ConfigAuditSourceRecord
        {
            Kind = ConfigAuditSourceKind.File,
            Role = ConfigAuditSourceRole.Base
        };

        Assert.Null(source.Location);
    }

    [Fact]
    public void ConfigAuditSourceRecord_WithRolePreservesLocation()
    {
        var location = new ConfigAuditSourceLocation(12, 34);
        var source = new ConfigAuditSourceRecord
        {
            Kind = ConfigAuditSourceKind.File,
            ProviderName = "Files",
            FilePath = "/tmp/appsettings.json",
            ConfigPath = "Feature.Enabled",
            AppliedToPath = "Feature.Enabled",
            Location = location,
            Role = ConfigAuditSourceRole.Base
        };

        var patched = source.WithRole(ConfigAuditSourceRole.Patch);

        Assert.Same(location, patched.Location);
        Assert.Equal(ConfigAuditSourceRole.Patch, patched.Role);
    }

    [Fact]
    public void ConfigAuditSourceRecord_SerializesLocationShape()
    {
        var source = new ConfigAuditSourceRecord
        {
            Kind = ConfigAuditSourceKind.File,
            ConfigPath = "Feature.Enabled",
            AppliedToPath = "Feature.Enabled",
            Location = new ConfigAuditSourceLocation(2, 7),
            Role = ConfigAuditSourceRole.Base
        };

        var json = JsonSerializer.Serialize(source);

        Assert.Contains(@"""Location"":", json, StringComparison.Ordinal);
        Assert.Contains(@"""LineNumber"":2", json, StringComparison.Ordinal);
        Assert.Contains(@"""ByteColumnNumber"":7", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigAuditKnownEntry_SnapshotsOptions()
    {
        var options = new ConfigAuditEntryOptions
        {
            TraverseCollectionElements = true,
            MaxCollectionDepth = 2,
            MaxCollectionElements = 3,
            MaxReportNodes = 4,
            DisplayDictionaryKeys = false,
            Sensitivity = ConfigAuditSensitivity.Sensitive,
            DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac
        };

        var entry = new ConfigAuditKnownEntry("Valid.Key", null, typeof(string), options);

        Assert.True(entry.Options.TraverseCollectionElements);
        Assert.Equal(2, entry.Options.MaxCollectionDepth);
        Assert.Equal(3, entry.Options.MaxCollectionElements);
        Assert.Equal(4, entry.Options.MaxReportNodes);
        Assert.False(entry.Options.DisplayDictionaryKeys);
        Assert.Equal(ConfigAuditSensitivity.Sensitive, entry.Options.Sensitivity);
        Assert.Equal(ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac, entry.Options.DictionaryKeyCorrelationMode);
        Assert.NotSame(options, entry.Options);
    }

    [Fact]
    public void ConfigAuditEntryOptionsBuilder_CreatesImmutableSnapshot()
    {
        var builder = new ConfigAuditEntryOptionsBuilder
        {
            TraverseCollectionElements = true,
            MaxCollectionDepth = 2,
            MaxCollectionElements = 3,
            MaxReportNodes = 4,
            DisplayDictionaryKeys = false,
            Sensitivity = ConfigAuditSensitivity.Sensitive,
            DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac
        };

        var options = builder.ToOptions();
        builder.TraverseCollectionElements = false;
        builder.MaxCollectionDepth = 99;
        builder.Sensitivity = ConfigAuditSensitivity.NonSensitive;
        builder.DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.None;

        Assert.True(options.TraverseCollectionElements);
        Assert.Equal(2, options.MaxCollectionDepth);
        Assert.Equal(3, options.MaxCollectionElements);
        Assert.Equal(4, options.MaxReportNodes);
        Assert.False(options.DisplayDictionaryKeys);
        Assert.Equal(ConfigAuditSensitivity.Sensitive, options.Sensitivity);
        Assert.Equal(ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac, options.DictionaryKeyCorrelationMode);
    }

    [Fact]
    public void ConfigAuditEntryOptionsBuilder_TracksExplicitDefaultValuedOverrides()
    {
        var wrapperOptions = new ConfigAuditCollectionTraversalAttribute
        {
            MaxCollectionDepth = 9,
            MaxCollectionElements = 7,
            MaxReportNodes = 11,
            DisplayDictionaryKeys = false,
            DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac
        }.ToOptions();
        var builder = new ConfigAuditEntryOptionsBuilder
        {
            TraverseCollectionElements = false,
            MaxCollectionDepth = 4,
            MaxCollectionElements = 128,
            MaxReportNodes = 4096,
            DisplayDictionaryKeys = true,
            Sensitivity = ConfigAuditSensitivity.NonSensitive,
            DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.None
        };

        var merged = wrapperOptions.ApplyAssignedOverrides(builder.ToOptions());

        Assert.False(merged.TraverseCollectionElements);
        Assert.Equal(4, merged.MaxCollectionDepth);
        Assert.Equal(128, merged.MaxCollectionElements);
        Assert.Equal(4096, merged.MaxReportNodes);
        Assert.True(merged.DisplayDictionaryKeys);
        Assert.Equal(ConfigAuditSensitivity.NonSensitive, merged.Sensitivity);
        Assert.Equal(ConfigAuditDictionaryKeyCorrelationMode.None, merged.DictionaryKeyCorrelationMode);
    }

    [Fact]
    public void ConfigAuditCollectionTraversalAttribute_CreatesTraversalOptionsWithSafeDefaults()
    {
        var attribute = new ConfigAuditCollectionTraversalAttribute();

        var options = attribute.ToOptions();

        Assert.True(options.TraverseCollectionElements);
        Assert.Equal(4, options.MaxCollectionDepth);
        Assert.Equal(128, options.MaxCollectionElements);
        Assert.Equal(4096, options.MaxReportNodes);
        Assert.True(options.DisplayDictionaryKeys);
        Assert.Equal(ConfigAuditSensitivity.Unknown, options.Sensitivity);
        Assert.False(options.AssignedOptions.HasFlag(ConfigAuditEntryOptionAssignments.Sensitivity));
        Assert.Equal(ConfigAuditDictionaryKeyCorrelationMode.None, options.DictionaryKeyCorrelationMode);
        Assert.True(options.AssignedOptions.HasFlag(ConfigAuditEntryOptionAssignments.DictionaryKeyCorrelationMode));
    }

    [Fact]
    public void ConfigAuditEntryOptions_MergesSensitivityMonotonically()
    {
        var sensitive = new ConfigAuditEntryOptions
        {
            Sensitivity = ConfigAuditSensitivity.Sensitive
        };
        var nonSensitive = new ConfigAuditEntryOptions
        {
            Sensitivity = ConfigAuditSensitivity.NonSensitive
        };
        var unknown = new ConfigAuditEntryOptions
        {
            Sensitivity = ConfigAuditSensitivity.Unknown
        };

        var sensitiveThenNonSensitive = sensitive.ApplyAssignedOverrides(nonSensitive);
        var unknownThenNonSensitive = unknown.ApplyAssignedOverrides(nonSensitive);
        var nonSensitiveThenSensitive = nonSensitive.ApplyAssignedOverrides(sensitive);

        Assert.Equal(ConfigAuditSensitivity.Sensitive, sensitiveThenNonSensitive.Sensitivity);
        Assert.Equal(ConfigAuditSensitivity.NonSensitive, unknownThenNonSensitive.Sensitivity);
        Assert.Equal(ConfigAuditSensitivity.Sensitive, nonSensitiveThenSensitive.Sensitivity);
    }

    [Fact]
    public void ConfigAuditKnownEntry_WithOptionsReturnsIndependentEntry()
    {
        var entry = new ConfigAuditKnownEntry("Valid.Key", null, typeof(string));
        var updated = entry.WithOptions(
            new ConfigAuditEntryOptions
            {
                TraverseCollectionElements = true,
                MaxCollectionElements = 2
            });

        Assert.False(entry.Options.TraverseCollectionElements);
        Assert.True(updated.Options.TraverseCollectionElements);
        Assert.Equal(2, updated.Options.MaxCollectionElements);
    }

    [Fact]
    public void ConfigAuditEntryOptions_DefaultsPreserveOpaqueCollectionBehavior()
    {
        var options = new ConfigAuditEntryOptions();

        Assert.False(options.TraverseCollectionElements);
        Assert.Equal(4, options.MaxCollectionDepth);
        Assert.Equal(128, options.MaxCollectionElements);
        Assert.Equal(4096, options.MaxReportNodes);
        Assert.True(options.DisplayDictionaryKeys);
        Assert.Equal(ConfigAuditSensitivity.Unknown, options.Sensitivity);
        Assert.Equal(ConfigAuditDictionaryKeyCorrelationMode.None, options.DictionaryKeyCorrelationMode);
    }

    [Fact]
    public void ConfigAuditEntryOptions_NormalizePreservesValidOptions()
    {
        var options = new ConfigAuditEntryOptions
        {
            TraverseCollectionElements = true,
            MaxCollectionDepth = 2,
            MaxCollectionElements = 3,
            MaxReportNodes = 4,
            DisplayDictionaryKeys = false,
            Sensitivity = ConfigAuditSensitivity.Sensitive,
            DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac
        };

        var normalized = options.Normalize();

        Assert.True(normalized.TraverseCollectionElements);
        Assert.Equal(2, normalized.MaxCollectionDepth);
        Assert.Equal(3, normalized.MaxCollectionElements);
        Assert.Equal(4, normalized.MaxReportNodes);
        Assert.False(normalized.DisplayDictionaryKeys);
        Assert.Equal(ConfigAuditSensitivity.Sensitive, normalized.Sensitivity);
        Assert.Equal(ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac, normalized.DictionaryKeyCorrelationMode);
    }

    [Fact]
    public void ConfigAuditEntryOptions_NormalizeFailsClosedInvalidSensitivityWithoutResettingTraversalOptions()
    {
        var options = new ConfigAuditEntryOptions
        {
            TraverseCollectionElements = true,
            MaxCollectionDepth = 2,
            MaxCollectionElements = 1,
            MaxReportNodes = 3,
            DisplayDictionaryKeys = false,
            Sensitivity = (ConfigAuditSensitivity)999
        };

        var normalized = options.Normalize();

        Assert.True(normalized.TraverseCollectionElements);
        Assert.Equal(2, normalized.MaxCollectionDepth);
        Assert.Equal(1, normalized.MaxCollectionElements);
        Assert.Equal(3, normalized.MaxReportNodes);
        Assert.False(normalized.DisplayDictionaryKeys);
        Assert.Equal(ConfigAuditSensitivity.Sensitive, normalized.Sensitivity);
    }

    [Fact]
    public void ConfigAuditEntryOptions_ReportsAndNormalizesInvalidCorrelationMode()
    {
        var options = new ConfigAuditEntryOptions
        {
            TraverseCollectionElements = true,
            DictionaryKeyCorrelationMode = (ConfigAuditDictionaryKeyCorrelationMode)999
        };

        var diagnostics = options.Validate("Tenants");
        var normalized = options.Normalize();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("config-audit-options-invalid", diagnostic.Code);
        Assert.Contains(nameof(ConfigAuditEntryOptions.DictionaryKeyCorrelationMode), diagnostic.Message, StringComparison.Ordinal);
        Assert.True(normalized.TraverseCollectionElements);
        Assert.Equal(ConfigAuditDictionaryKeyCorrelationMode.None, normalized.DictionaryKeyCorrelationMode);
    }

    [Fact]
    public void ConfigAuditEntryOptions_ReportsAndNormalizesCorrelationWithoutCollectionTraversal()
    {
        var options = new ConfigAuditEntryOptions
        {
            TraverseCollectionElements = false,
            DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac
        };

        var diagnostics = options.Validate("Tenants");
        var normalized = options.Normalize();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("config-audit-options-invalid", diagnostic.Code);
        Assert.Contains(nameof(ConfigAuditEntryOptions.DictionaryKeyCorrelationMode), diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ConfigAuditEntryOptions.TraverseCollectionElements), diagnostic.Message, StringComparison.Ordinal);
        Assert.False(normalized.TraverseCollectionElements);
        Assert.Equal(ConfigAuditDictionaryKeyCorrelationMode.None, normalized.DictionaryKeyCorrelationMode);
    }

    [Fact]
    public void ConfigAuditEntryOptions_RejectsNullAssignedOverrides()
    {
        var options = new ConfigAuditEntryOptions();

        Assert.Throws<ArgumentNullException>(() => options.ApplyAssignedOverrides(null!));
    }

    [Fact]
    public void ConfigAuditDictionaryKeyCorrelator_ReportsMissingOptionalConfiguration()
    {
        var nullOptionsContext = new ConfigAuditDictionaryKeyCorrelator(null).CreateContext("Production", "Tenants");
        var missingKeyIdContext = new ConfigAuditDictionaryKeyCorrelator(
            new ConfigAuditDictionaryKeyCorrelationOptions
            {
                SecretKey = "0123456789abcdef0123456789abcdef",
                ApplicationScope = "billing"
            }).CreateContext("Production", "Tenants");
        var missingScopeContext = new ConfigAuditDictionaryKeyCorrelator(
            new ConfigAuditDictionaryKeyCorrelationOptions
            {
                SecretKey = "0123456789abcdef0123456789abcdef",
                KeyId = "AZaz09._-"
            }).CreateContext("Production", "Tenants");

        Assert.False(nullOptionsContext.IsAvailable);
        Assert.Equal("a secret key was not configured", nullOptionsContext.UnavailableReason);
        Assert.False(missingKeyIdContext.IsAvailable);
        Assert.Equal("a display-safe key id was not configured", missingKeyIdContext.UnavailableReason);
        Assert.False(missingScopeContext.IsAvailable);
        Assert.Equal("an application scope was not configured", missingScopeContext.UnavailableReason);
    }

    [Fact]
    public void ConfigAuditDictionaryKeyCorrelationContext_ClonesSecretKey()
    {
        var secretKey = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
        var context = ConfigAuditDictionaryKeyCorrelationContext.Available(
            secretKey,
            "kid",
            "billing",
            "Production",
            "Tenants");
        var beforeMutation = context.CreateCorrelationId("alpha");

        Array.Fill(secretKey, (byte)'x');
        var afterMutation = context.CreateCorrelationId("alpha");

        Assert.Equal(beforeMutation, afterMutation);
    }

    [Fact]
    public void ConfigAuditRedactor_CreatePolicySanitizesCorrelationMetadata()
    {
        var redactor = new ConfigAuditRedactor();

        var unrequested = redactor.CreatePolicy(
            new ConfigAuditDictionaryKeyCorrelationOptions
            {
                KeyId = "kid-a",
                ApplicationScope = "billing"
            },
            dictionaryKeyCorrelationRequested: false);
        var requestedWithoutOptions = redactor.CreatePolicy(
            correlationOptions: null,
            dictionaryKeyCorrelationRequested: true);
        var requestedWithInvalidMetadata = redactor.CreatePolicy(
            new ConfigAuditDictionaryKeyCorrelationOptions
            {
                KeyId = "kid-a\nforged",
                ApplicationScope = " "
            },
            dictionaryKeyCorrelationRequested: true);

        Assert.Equal(ConfigAuditDictionaryKeyCorrelationMode.None, unrequested.DictionaryKeyCorrelationMode);
        Assert.Null(unrequested.DictionaryKeyCorrelationKeyId);
        Assert.Null(unrequested.DictionaryKeyCorrelationApplicationScope);
        Assert.Equal(ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac, requestedWithoutOptions.DictionaryKeyCorrelationMode);
        Assert.Null(requestedWithoutOptions.DictionaryKeyCorrelationKeyId);
        Assert.Null(requestedWithoutOptions.DictionaryKeyCorrelationApplicationScope);
        Assert.Equal(ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac, requestedWithInvalidMetadata.DictionaryKeyCorrelationMode);
        Assert.Null(requestedWithInvalidMetadata.DictionaryKeyCorrelationKeyId);
        Assert.Null(requestedWithInvalidMetadata.DictionaryKeyCorrelationApplicationScope);
    }

    [Fact]
    public void ConfigAuditDictionaryLabelSet_ReusesLabelsForDuplicateRawKeys()
    {
        var labels = new ConfigAuditDictionaryLabelSet();

        var first = labels.GetRedactedLabel("secret-key");
        var duplicate = labels.GetRedactedLabel("secret-key");
        var second = labels.GetRedactedLabel("other-secret-key");

        Assert.Equal(first, duplicate);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ConfigAuditPath_SeparatesDisplayLabelsFromSourceSegments()
    {
        var labels = new ConfigAuditDictionaryLabelSet();
        var correlation = ConfigAuditDictionaryKeyCorrelationContext.Unavailable("not configured");
        var root = ConfigAuditPath.Root("Root");

        var safe = root.AppendDictionaryKey("tenant-1", new ConfigAuditEntryOptions(), labels, correlation);
        var empty = root.AppendDictionaryKey(null, new ConfigAuditEntryOptions(), labels, correlation);
        var inherited = empty.AppendDictionaryKey("safe", new ConfigAuditEntryOptions(), labels, correlation);

        Assert.Equal("Root[\"tenant-1\"]", safe.DisplayPath);
        Assert.Equal("Root.tenant-1", safe.SourcePath);
        Assert.False(safe.RequiresInheritedSource);

        Assert.Equal("Root[\"\"]", empty.DisplayPath);
        Assert.Equal("Root", empty.SourcePath);
        Assert.True(empty.RequiresInheritedSource);

        Assert.Equal("Root.safe", inherited.SourcePath);
        Assert.True(inherited.RequiresInheritedSource);
    }

    [Fact]
    public void ConfigAuditTextRenderer_OrdersMixedElementsAndFormatsUnknownProviders()
    {
        var renderer = new ConfigAuditTextRenderer();
        var report = new ConfigAuditReport
        {
            Environment = "Production",
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Redaction = new ConfigAuditRedaction
            {
                Enabled = true,
                Placeholder = "[redacted]"
            },
            DiscoveredKeys =
            [
                new ConfigAuditDiscoveredKey
                {
                    Key = "Discovered.Fallback",
                    Classification = (ConfigAuditDiscoveredKeyClassification)99,
                    DisplayValue = "value",
                    Sources =
                    [
                        new ConfigAuditSourceRecord
                        {
                            Kind = ConfigAuditSourceKind.Provider,
                            ProviderName = "ProviderName",
                            Role = ConfigAuditSourceRole.Base
                        }
                    ],
                    Diagnostics =
                    [
                        new ConfigAuditDiagnostic
                        {
                            Severity = ConfigAuditDiagnosticSeverity.Warning,
                            Code = "discovered-warning",
                            Message = "Discovered diagnostic."
                        }
                    ]
                }
            ],
            Entries =
            [
                new ConfigAuditEntry
                {
                    Key = "Root",
                    State = ConfigAuditEntryState.Resolved,
                    Sources =
                    [
                        new ConfigAuditSourceRecord
                        {
                            Kind = ConfigAuditSourceKind.Provider,
                            Role = ConfigAuditSourceRole.Base
                        }
                    ],
                    Children =
                    [
                        new ConfigAuditEntry
                        {
                            Key = "Root[\"zeta\"]",
                            State = ConfigAuditEntryState.Resolved,
                            Element = new ConfigAuditElementIdentity
                            {
                                Kind = ConfigAuditElementKind.DictionaryItem,
                                KeyLabel = "zeta"
                            }
                        },
                        new ConfigAuditEntry
                        {
                            Key = "Root[\"alpha\"]",
                            State = ConfigAuditEntryState.Resolved,
                            Element = new ConfigAuditElementIdentity
                            {
                                Kind = ConfigAuditElementKind.DictionaryItem,
                                KeyLabel = "alpha"
                            }
                        },
                        new ConfigAuditEntry
                        {
                            Key = "Root[0]",
                            State = ConfigAuditEntryState.Resolved,
                            Element = new ConfigAuditElementIdentity
                            {
                                Kind = ConfigAuditElementKind.ArrayItem,
                                Index = 0
                            }
                        },
                        new ConfigAuditEntry
                        {
                            Key = "Root.Plain",
                            State = ConfigAuditEntryState.Resolved
                        }
                    ]
                }
            ]
        };

        var rendered = renderer.Render(report);

        Assert.True(rendered.IndexOf("Root[0]", StringComparison.Ordinal) < rendered.IndexOf("Root[\"alpha\"]", StringComparison.Ordinal));
        Assert.True(rendered.IndexOf("Root[\"alpha\"]", StringComparison.Ordinal) < rendered.IndexOf("Root[\"zeta\"]", StringComparison.Ordinal));
        Assert.True(rendered.IndexOf("Root[0]", StringComparison.Ordinal) < rendered.IndexOf("Root.Plain", StringComparison.Ordinal));
        Assert.Contains("Provider", rendered, StringComparison.Ordinal);
        Assert.Contains("Discovered keys:", rendered, StringComparison.Ordinal);
        Assert.Contains("Discovered.Fallback [99] = value", rendered, StringComparison.Ordinal);
        Assert.Contains("Diagnostic: [Warning] discovered-warning: Discovered diagnostic.", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicEnums_KeepStableOrdinals()
    {
        Assert.Equal(0, (int)ConfigAuditEntryState.Resolved);
        Assert.Equal(1, (int)ConfigAuditEntryState.PartiallyResolved);
        Assert.Equal(2, (int)ConfigAuditEntryState.Defaulted);
        Assert.Equal(3, (int)ConfigAuditEntryState.Missing);
        Assert.Equal(4, (int)ConfigAuditEntryState.Invalid);

        Assert.Equal(0, (int)ConfigAuditDiscoveredKeyClassification.Known);
        Assert.Equal(1, (int)ConfigAuditDiscoveredKeyClassification.KnownDescendant);
        Assert.Equal(2, (int)ConfigAuditDiscoveredKeyClassification.Unknown);

        Assert.Equal(0, (int)ConfigAuditElementKind.ArrayItem);
        Assert.Equal(1, (int)ConfigAuditElementKind.ListItem);
        Assert.Equal(2, (int)ConfigAuditElementKind.DictionaryItem);

        Assert.Equal(0, (int)ConfigAuditDictionaryKeyCorrelationMode.None);
        Assert.Equal(1, (int)ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac);

        Assert.Equal(0, (int)ConfigAuditSourceKind.Provider);
        Assert.Equal(1, (int)ConfigAuditSourceKind.File);
        Assert.Equal(2, (int)ConfigAuditSourceKind.EnvironmentVariable);
        Assert.Equal(3, (int)ConfigAuditSourceKind.Default);
        Assert.Equal(4, (int)ConfigAuditSourceKind.Missing);

        Assert.Equal(0, (int)ConfigAuditSourceRole.Base);
        Assert.Equal(1, (int)ConfigAuditSourceRole.Override);
        Assert.Equal(2, (int)ConfigAuditSourceRole.Patch);
        Assert.Equal(3, (int)ConfigAuditSourceRole.Fallback);

        Assert.Equal(0, (int)ConfigAuditSensitivity.Unknown);
        Assert.Equal(1, (int)ConfigAuditSensitivity.NonSensitive);
        Assert.Equal(2, (int)ConfigAuditSensitivity.Sensitive);

        Assert.Equal(0, (int)ConfigAuditDiagnosticSeverity.Info);
        Assert.Equal(1, (int)ConfigAuditDiagnosticSeverity.Warning);
        Assert.Equal(2, (int)ConfigAuditDiagnosticSeverity.Error);

        var providerKey = new ConfigAuditProviderDiscoveredKey(
            "Discovered.Value",
            RawValue: null,
            ConfigAuditDiscoveredValueKind.Array,
            Sources: [],
            Diagnostics: []);
        Assert.Equal(ConfigAuditDiscoveredValueKind.Array, providerKey.ValueKind);
    }
}
