using System.Text.Json;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigAuditLogicalDiffTests
{
    [Fact]
    public void CapturedLogicalPathsDistinguishLiteralDotsFromExpandedMembers()
    {
        var baseline = RoundTrip(Report(
            Entry("A.B", "A.B", "literal"),
            Entry("A", "A", null, Entry("A.B", "A:B", "before"))));
        var target = RoundTrip(Report(
            Entry("A", "A", null, Entry("A.B", "A:B", "after")),
            Entry("A.B", "A.B", "literal")));

        var result = Compare(baseline, target);

        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "config-diff-duplicate-evidence");
        var dots = result.Items.Where(item => item.Key == "A.B").ToArray();
        Assert.Equal(2, dots.Length);
        Assert.Single(dots, item => item.Status == ConfigAuditDiffItemStatus.Unchanged);
        Assert.Single(dots, item => item.Status == ConfigAuditDiffItemStatus.Changed);
        Assert.Equal("A:B", baseline.Entries[1].Children[0].ConfigPath);
    }

    [Fact]
    public void LogicalIdentityIgnoresPresentationAndCaseWhileValuesRemainCaseSensitive()
    {
        var baseline = Report(Entry("first display", "Payments:ApiKey", "marker"));
        var target = Report(Entry("different display", "payments:apikey", "marker"));
        var item = Assert.Single(Compare(baseline, target).Items);
        Assert.Equal(ConfigAuditDiffItemStatus.Unchanged, item.Status);

        target = Report(Entry("different display", "payments:apikey", "MARKER"));
        item = Assert.Single(Compare(baseline, target).Items);
        Assert.Equal(ConfigAuditDiffItemStatus.Changed, item.Status);
    }

    [Fact]
    public void StructuralDictionaryIdentityCannotCollideWithLiteralPathText()
    {
        var dictionaryItem = new ConfigAuditEntry
        {
            Key = "Root[\"label\"]",
            State = ConfigAuditEntryState.Resolved,
            DisplayValue = "dictionary",
            Element = new ConfigAuditElementIdentity { Kind = ConfigAuditElementKind.DictionaryItem, KeyLabel = "label" }
        };
        var baseline = Report(
            Entry("Root", "Root", null, dictionaryItem),
            Entry("Root[label:label]", "Root[label:label]", "literal"));
        var result = Compare(baseline, RoundTrip(baseline));
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "config-diff-duplicate-evidence");
        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(ConfigAuditDiffItemStatus.Unchanged, item.Status));
    }

    [Fact]
    public void OlderRootWithoutConfigPathStillUsesStrictLogicalIdentity()
    {
        var baseline = Report(new ConfigAuditEntry
        {
            Key = "Payments:ApiKey",
            State = ConfigAuditEntryState.Resolved,
            DisplayValue = "marker"
        });
        var target = Report(Entry("Payments:ApiKey", "payments:apikey", "marker"));
        Assert.Equal(ConfigAuditDiffItemStatus.Unchanged, Assert.Single(Compare(baseline, target).Items).Status);
    }

    [Fact]
    public void MalformedLegacyRootKeepsItsOpaqueIdentityAcrossSerialization()
    {
        var report = Report(new ConfigAuditEntry
        {
            Key = "Legacy::Key",
            State = ConfigAuditEntryState.Resolved,
            DisplayValue = "marker"
        });

        var item = Assert.Single(Compare(report, RoundTrip(report)).Items);

        Assert.Equal("Legacy::Key", item.Key);
        Assert.Equal(ConfigAuditDiffItemStatus.Unchanged, item.Status);
    }

    [Fact]
    public void MalformedLegacyDiscoveredKeyStillDetectsValueChanges()
    {
        static ConfigAuditReport Discovered(string value) => new()
        {
            Environment = "Production",
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Redaction = new ConfigAuditRedaction { Enabled = true, Placeholder = "[redacted]" },
            DiscoveredKeys = [new ConfigAuditDiscoveredKey
            {
                Key = "Legacy::Key", Classification = ConfigAuditDiscoveredKeyClassification.Unknown,
                DisplayValue = value, ValueDisplayState = ConfigAuditDiscoveredValueDisplayState.Shown
            }]
        };

        var item = Assert.Single(Compare(Discovered("before"), Discovered("after")).Items);

        Assert.Equal("Legacy::Key", item.Key);
        Assert.Equal(ConfigAuditDiffItemStatus.Changed, item.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedReportGenerationsDoNotInventChildTopologyChanges(bool reverse)
    {
        var oldChild = new ConfigAuditEntry
        {
            Key = "Payments.Options",
            State = ConfigAuditEntryState.Resolved,
            Children = [new ConfigAuditEntry
            {
                Key = "Payments.Options.ApiKey", State = ConfigAuditEntryState.Resolved, DisplayValue = "marker"
            }]
        };
        var oldReport = Report(Entry("Payments", "Payments", null, oldChild), Entry("Other", "Other", "stable"));
        var currentReport = Report(Entry("Payments", "Payments", null,
            Entry("Payments.Options", "Payments:Options", null,
                Entry("Payments.Options.ApiKey", "Payments:Options:ApiKey", "marker"))), Entry("Other", "Other", "stable"));

        var result = reverse ? Compare(currentReport, oldReport) : Compare(oldReport, currentReport);

        Assert.DoesNotContain(result.Items, item => item.Status is ConfigAuditDiffItemStatus.Added or ConfigAuditDiffItemStatus.Removed);
        var children = result.Items.Where(item => item.Key.StartsWith("Payments.", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(children);
        Assert.All(children, item =>
        {
            Assert.Equal(ConfigAuditDiffItemStatus.Uncomparable, item.Status);
            Assert.Contains(item.Diagnostics, diagnostic => diagnostic.Code == "config-diff-logical-path-evidence-missing");
        });
        Assert.Equal(ConfigAuditDiffItemStatus.Unchanged, Assert.Single(result.Items, item => item.Key == "Other").Status);
    }

    [Fact]
    public void MissingChildMetadataDoesNotInvalidateDictionaryCorrelation()
    {
        static ConfigAuditEntry DictionaryChild() => new()
        {
            Key = "Root[\"label\"]",
            State = ConfigAuditEntryState.Resolved,
            DisplayValue = "dictionary",
            Element = new ConfigAuditElementIdentity { Kind = ConfigAuditElementKind.DictionaryItem, KeyLabel = "label" }
        };
        var oldReport = Report(Entry("Root", "Root", null, DictionaryChild(), new ConfigAuditEntry
        {
            Key = "Root.Child",
            State = ConfigAuditEntryState.Resolved,
            DisplayValue = "marker"
        }));
        var currentReport = Report(Entry("Root", "Root", null, DictionaryChild(), Entry("Root.Child", "Root:Child", "marker")));

        var result = Compare(oldReport, currentReport);

        var dictionary = Assert.Single(result.Items, item => item.Key == "Root[\"label\"]");
        Assert.Equal(ConfigAuditDiffItemStatus.Unchanged, dictionary.Status);
        Assert.Empty(dictionary.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedVisibleDictionaryObjectChildrenNeverInventTopology(bool reverse)
    {
        static ConfigAuditReport Capture(bool current) => Report(Entry("Root", "Root", null,
            DictionaryNode("Root[\"label\"]", current ? "Root:label" : null, "label", false, null,
                new ConfigAuditEntry
                {
                    Key = "Root[\"label\"].Value",
                    ConfigPath = current ? "Root:label:Value" : null,
                    State = ConfigAuditEntryState.Resolved,
                    DisplayValue = "marker"
                })));
        var oldReport = RoundTrip(Capture(false));
        var currentReport = RoundTrip(Capture(true));
        var result = reverse ? Compare(currentReport, oldReport) : Compare(oldReport, currentReport);

        Assert.DoesNotContain(result.Items, item => item.Status is ConfigAuditDiffItemStatus.Added or ConfigAuditDiffItemStatus.Removed);
        var children = result.Items.Where(item => item.Key == "Root[\"label\"].Value").ToArray();
        Assert.NotEmpty(children);
        Assert.All(children, AssertLogicalPathUncomparable);
        Assert.Equal(ConfigAuditDiffItemStatus.Unchanged, Assert.Single(result.Items, item => item.Key == "Root[\"label\"]").Status);
        Assert.Equal(ConfigAuditDiffItemStatus.Unchanged, Assert.Single(result.Items, item => item.Key == "Root").Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DictionaryBelowUncertainObjectInheritsUncertaintyEvenWithHmac(bool hmac, bool reverse)
    {
        static ConfigAuditReport Capture(bool current, bool correlated)
        {
            var label = correlated ? current ? "[redacted-key-9]" : "[redacted-key-1]" : "label";
            var display = correlated ? $"Root.Options[{label}]" : $"Root.Options[\"{label}\"]";
            return Report(Entry("Root", "Root", null, new ConfigAuditEntry
            {
                Key = "Root.Options",
                ConfigPath = current ? "Root:Options" : null,
                State = ConfigAuditEntryState.Resolved,
                Children = [DictionaryNode(display, current && !correlated ? "Root:Options:label" : null,
                    label, correlated, correlated ? "v1c:key:stable" : null, new ConfigAuditEntry
                    {
                        Key = display + ".Value", ConfigPath = current && !correlated ? "Root:Options:label:Value" : null,
                        State = ConfigAuditEntryState.Resolved, DisplayValue = "marker"
                    })]
            }));
        }
        var oldReport = RoundTrip(Capture(false, hmac));
        var currentReport = RoundTrip(Capture(true, hmac));
        var result = reverse ? Compare(currentReport, oldReport) : Compare(oldReport, currentReport);

        Assert.DoesNotContain(result.Items, item => item.Status is ConfigAuditDiffItemStatus.Added or ConfigAuditDiffItemStatus.Removed);
        var subtree = result.Items.Where(item => item.Key != "Root").ToArray();
        Assert.True(subtree.Length >= 3);
        Assert.All(subtree, AssertLogicalPathUncomparable);
        Assert.Equal(ConfigAuditDiffItemStatus.Unchanged, Assert.Single(result.Items, item => item.Key == "Root").Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DirectRootHmacSubtreeRetainsNormalComparisonDespiteMixedSibling(bool redacted, bool changed)
    {
        static ConfigAuditReport Capture(bool current, bool hidden, string value)
        {
            var label = hidden ? current ? "[redacted-key-9]" : "[redacted-key-1]" : "label";
            var display = hidden ? $"Root[{label}]" : $"Root[\"{label}\"]";
            return Report(Entry("Root", "Root", null,
                new ConfigAuditEntry
                {
                    Key = "Root.Sibling",
                    ConfigPath = current ? "Root:Sibling" : null,
                    State = ConfigAuditEntryState.Resolved,
                    DisplayValue = "sibling"
                },
                DictionaryNode(display, current && !hidden ? "Root:label" : null, label, hidden, "v1c:key:stable",
                    new ConfigAuditEntry
                    {
                        Key = display + ".Value",
                        ConfigPath = current && !hidden ? "Root:label:Value" : null,
                        State = ConfigAuditEntryState.Resolved,
                        DisplayValue = value
                    })));
        }
        var result = Compare(RoundTrip(Capture(false, redacted, "before")),
            RoundTrip(Capture(true, redacted, changed ? "after" : "before")));

        Assert.DoesNotContain(result.Items, item => item.Status is ConfigAuditDiffItemStatus.Added or ConfigAuditDiffItemStatus.Removed);
        var dictionary = Assert.Single(result.Items, item => item.Key.StartsWith("Root[", StringComparison.Ordinal) && !item.Key.EndsWith(".Value", StringComparison.Ordinal));
        Assert.Equal(ConfigAuditDiffItemStatus.Unchanged, dictionary.Status);
        Assert.Empty(dictionary.Diagnostics);
        var child = Assert.Single(result.Items, item => item.Key.EndsWith(".Value", StringComparison.Ordinal));
        Assert.Equal(changed ? ConfigAuditDiffItemStatus.Changed : ConfigAuditDiffItemStatus.Unchanged, child.Status);
        Assert.Empty(child.Diagnostics);
        Assert.All(result.Items.Where(item => item.Key == "Root.Sibling"), AssertLogicalPathUncomparable);
    }

    [Fact]
    public void MixedSiblingDoesNotReplaceRedactedDictionaryDiagnosticWithRecaptureAdvice()
    {
        static ConfigAuditReport Capture(bool current) => Report(Entry("Root", "Root", null,
            new ConfigAuditEntry
            {
                Key = "Root.Sibling",
                ConfigPath = current ? "Root:Sibling" : null,
                State = ConfigAuditEntryState.Resolved,
                DisplayValue = "sibling"
            },
            DictionaryNode("Root[[redacted-key-1]]", null, "[redacted-key-1]", true, null,
                new ConfigAuditEntry
                {
                    Key = "Root[[redacted-key-1]].Value",
                    State = ConfigAuditEntryState.Resolved,
                    DisplayValue = "[redacted]",
                    IsRedacted = true
                })));
        var result = Compare(RoundTrip(Capture(false)), RoundTrip(Capture(true)));
        var subtree = result.Items.Where(item => item.Key.StartsWith("Root[", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, subtree.Length);
        Assert.All(subtree, item =>
        {
            Assert.Equal(ConfigAuditDiffItemStatus.Uncomparable, item.Status);
            Assert.Contains(item.Diagnostics, diagnostic => diagnostic.Code == "config-diff-redacted-dictionary-key-uncomparable");
            Assert.DoesNotContain(item.Diagnostics, diagnostic => diagnostic.Code == "config-diff-logical-path-evidence-missing");
        });
    }

    private static void AssertLogicalPathUncomparable(ConfigAuditDiffItem item)
    {
        Assert.Equal(ConfigAuditDiffItemStatus.Uncomparable, item.Status);
        Assert.Contains(item.Diagnostics, diagnostic => diagnostic.Code == "config-diff-logical-path-evidence-missing");
    }

    private static ConfigAuditEntry DictionaryNode(string display, string? source, string label, bool redacted,
        string? correlation, params ConfigAuditEntry[] children) => new()
        {
            Key = display,
            ConfigPath = source,
            State = ConfigAuditEntryState.Resolved,
            Element = new ConfigAuditElementIdentity
            {
                Kind = ConfigAuditElementKind.DictionaryItem,
                KeyLabel = label,
                IsKeyRedacted = redacted,
                ComparisonKeyCorrelationId = correlation
            },
            Children = children
        };

    [Theory]
    [InlineData(ConfigAuditElementKind.ArrayItem)]
    [InlineData(ConfigAuditElementKind.ListItem)]
    public void IndexedLogicalPathRemainsDistinctFromLiteralBrackets(ConfigAuditElementKind kind)
    {
        var child = new ConfigAuditEntry
        {
            Key = "Items[0]",
            ConfigPath = "Items:0",
            State = ConfigAuditEntryState.Resolved,
            DisplayValue = "indexed",
            Element = new ConfigAuditElementIdentity { Kind = kind, Index = 0 }
        };
        var report = Report(Entry("Items", "Items", null, child), Entry("Items[0]", "Items[0]", "literal"));
        var result = Compare(report, RoundTrip(report));
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "config-diff-duplicate-evidence");
        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(ConfigAuditDiffItemStatus.Unchanged, item.Status));
    }

    [Fact]
    public void DiscoveredLogicalSpellingDoesNotBecomeAValueChange()
    {
        static ConfigAuditReport Discovered(string key) => new()
        {
            Environment = "Production",
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Redaction = new ConfigAuditRedaction { Enabled = true, Placeholder = "[redacted]" },
            DiscoveredKeys = [new ConfigAuditDiscoveredKey
            {
                Key = key, Classification = ConfigAuditDiscoveredKeyClassification.Unknown,
                DisplayValue = "marker", ValueDisplayState = ConfigAuditDiscoveredValueDisplayState.Shown
            }]
        };
        var item = Assert.Single(Compare(Discovered("Payments:ApiKey"), Discovered("payments:apikey")).Items);
        Assert.Equal(ConfigAuditDiffItemStatus.Unchanged, item.Status);
    }

    private static ConfigAuditDiffReport Compare(ConfigAuditReport baseline, ConfigAuditReport target) =>
        new ConfigAuditReportDiffer().Compare(baseline, target, new ConfigAuditDiffOptions { IncludeUnchangedItems = true });

    private static ConfigAuditReport RoundTrip(ConfigAuditReport report) =>
        JsonSerializer.Deserialize<ConfigAuditReport>(JsonSerializer.Serialize(report))!;

    private static ConfigAuditReport Report(params ConfigAuditEntry[] entries) => new()
    {
        Environment = "Production",
        GeneratedAt = DateTimeOffset.UnixEpoch,
        Redaction = new ConfigAuditRedaction { Enabled = true, Placeholder = "[redacted]" },
        Entries = entries
    };

    private static ConfigAuditEntry Entry(string display, string source, string? value, params ConfigAuditEntry[] children) => new()
    {
        Key = display,
        ConfigPath = source,
        State = ConfigAuditEntryState.Resolved,
        DisplayValue = value,
        Children = children
    };
}
