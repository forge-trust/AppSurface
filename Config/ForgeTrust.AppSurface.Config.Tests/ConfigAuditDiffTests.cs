using System.Text;
using System.Text.Json;
using FakeItEasy;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigAuditDiffTests
{
    [Fact]
    public void Compare_NullInputsThrow()
    {
        var differ = new ConfigAuditReportDiffer();
        var report = CreateReport("Production");

        Assert.Throws<ArgumentNullException>(() => differ.Compare(null!, report));
        Assert.Throws<ArgumentNullException>(() => differ.Compare(report, null!));
    }

    [Fact]
    public void Compare_ReportsAddedRemovedChangedAndUnchangedEntries()
    {
        var baseline = CreateReport(
            "Staging",
            entries:
            [
                Entry("Feature.Enabled", "false"),
                Entry("Only.Baseline", "old"),
                Entry("Same.Value", "same")
            ]);
        var target = CreateReport(
            "Production",
            entries:
            [
                Entry("Feature.Enabled", "true"),
                Entry("Only.Target", "new"),
                Entry("Same.Value", "same")
            ]);

        var diff = new ConfigAuditReportDiffer().Compare(
            baseline,
            target,
            new ConfigAuditDiffOptions { IncludeUnchangedItems = true });

        Assert.Equal(1, diff.Summary.Changed);
        Assert.Equal(1, diff.Summary.Added);
        Assert.Equal(1, diff.Summary.Removed);
        Assert.Equal(2, diff.Summary.Unchanged);
        Assert.Contains(diff.Items, item => item.Key == "Feature.Enabled" && item.Status == ConfigAuditDiffItemStatus.Changed);
        Assert.Contains(diff.Items, item => item.Key == "Only.Target" && item.Status == ConfigAuditDiffItemStatus.Added);
        Assert.Contains(diff.Items, item => item.Key == "Only.Baseline" && item.Status == ConfigAuditDiffItemStatus.Removed);
        Assert.Contains(diff.Items, item => item.Key == "Same.Value" && item.Status == ConfigAuditDiffItemStatus.Unchanged);
        Assert.Contains(diff.Items, item => item.Key == "Feature.Enabled" && item.Significance == ConfigAuditDiffSignificance.NeedsAttention);
        Assert.Contains(diff.Items, item => item.Key == "Only.Target" && item.Significance == ConfigAuditDiffSignificance.NeedsAttention);
        Assert.Contains(diff.Items, item => item.Key == "Only.Baseline" && item.Significance == ConfigAuditDiffSignificance.NeedsAttention);
    }

    [Fact]
    public void Compare_SourceOnlyEntryDriftIsContext()
    {
        var baseline = CreateReport(
            "Staging",
            entries: [Entry("Service.Url", "https://service", sourcePath: "/config/staging.json")]);
        var target = CreateReport(
            "Production",
            entries: [Entry("Service.Url", "https://service", sourcePath: "/config/production.json")]);

        var diff = new ConfigAuditReportDiffer().Compare(baseline, target);
        var item = Assert.Single(diff.Items, item => item.Key == "Service.Url");

        Assert.Equal(ConfigAuditDiffItemStatus.Changed, item.Status);
        Assert.Equal(ConfigAuditDiffSignificance.Context, item.Significance);
        Assert.Contains("source", item.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compare_DuplicateEntryIdentitiesArePairedDeterministicallyAndDiagnosed()
    {
        var baseline = CreateReport(
            "Staging",
            entries:
            [
                Entry("Duplicate.Key", "one"),
                Entry("Duplicate.Key", "two")
            ]);
        var target = CreateReport(
            "Production",
            entries:
            [
                Entry("Duplicate.Key", "one")
            ]);

        var diff = new ConfigAuditReportDiffer().Compare(baseline, target);

        Assert.Contains(diff.Diagnostics, diagnostic => diagnostic.Code == "config-diff-duplicate-evidence");
        Assert.Contains(diff.Items, item => item.Key == "Duplicate.Key" && item.Status == ConfigAuditDiffItemStatus.Removed);
    }

    [Fact]
    public void Compare_ProviderDiscoveredKeyDiagnosticAndRedactionPolicyDriftAreTyped()
    {
        var baseline = CreateReport(
            "Staging",
            providers:
            [
                Provider("FileProvider", priority: 10, precedence: 1),
                Provider("RemovedProvider", priority: 30, precedence: 3)
            ],
            entries: [Entry("Feature.Enabled", "true")],
            discoveredKeys:
            [
                Discovered("Feature.Flag", "true", ConfigAuditDiscoveredValueDisplayState.Shown),
                Discovered("Manual.Legacy", null, ConfigAuditDiscoveredValueDisplayState.Unspecified)
            ],
            diagnostics:
            [
                Diagnostic(ConfigAuditDiagnosticSeverity.Warning, "baseline-warning", "Baseline warning.")
            ]);
        var target = CreateReport(
            "Production",
            providers:
            [
                Provider("FileProvider", priority: 20, precedence: 1),
                Provider("AddedProvider", priority: 40, precedence: 4)
            ],
            entries: [Entry("Feature.Enabled", "true")],
            discoveredKeys:
            [
                Discovered("Feature.Flag", "false", ConfigAuditDiscoveredValueDisplayState.Shown),
                Discovered("Manual.Legacy", null, ConfigAuditDiscoveredValueDisplayState.Unspecified)
            ],
            diagnostics:
            [
                Diagnostic(ConfigAuditDiagnosticSeverity.Error, "target-error", "Target error.")
            ],
            redaction: Redaction(placeholder: "[hidden]"));

        var diff = new ConfigAuditReportDiffer().Compare(
            baseline,
            target,
            new ConfigAuditDiffOptions { IncludeUnchangedItems = true });
        var rendered = new ConfigAuditDiffTextRenderer().Render(diff);

        Assert.Contains(diff.Items, item => item.Kind == ConfigAuditDiffItemKind.Provider && item.Status == ConfigAuditDiffItemStatus.Changed);
        Assert.Contains(diff.Items, item => item.Kind == ConfigAuditDiffItemKind.Provider && item.Status == ConfigAuditDiffItemStatus.Added);
        Assert.Contains(diff.Items, item => item.Kind == ConfigAuditDiffItemKind.Provider && item.Status == ConfigAuditDiffItemStatus.Removed);
        Assert.Contains(diff.Items, item => item.Key == "Feature.Enabled" && item.ValueEvidence == ConfigAuditDiffValueEvidence.RedactionPolicyMismatch);
        Assert.Contains(diff.Items, item => item.Kind == ConfigAuditDiffItemKind.DiscoveredKey && item.Key == "Feature.Flag");
        Assert.Contains(diff.Items, item => item.Kind == ConfigAuditDiffItemKind.RedactionPolicy);
        Assert.Contains(diff.Items, item => item.Kind == ConfigAuditDiffItemKind.Diagnostic && item.Status == ConfigAuditDiffItemStatus.Added);
        Assert.Contains(diff.Items, item => item.Kind == ConfigAuditDiffItemKind.Diagnostic && item.Status == ConfigAuditDiffItemStatus.Removed);
        Assert.Contains(diff.Diagnostics, diagnostic => diagnostic.Code == "config-diff-manual-report-evidence");
        Assert.Contains("redaction policy metadata differs", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_DictionaryEntriesMatchByComparisonCorrelationMetadata()
    {
        var baseline = CreateReport(
            "Staging",
            entries:
            [
                Entry(
                    "Tenants[[redacted-key-1]]",
                    "[redacted]",
                    isRedacted: true,
                    element: DictionaryElement("[redacted-key-1]", isKeyRedacted: true, keyCorrelationId: "v1:kid:staging", comparisonKeyCorrelationId: "v1c:kid:same"))
            ]);
        var target = CreateReport(
            "Production",
            entries:
            [
                Entry(
                    "Tenants[[redacted-key-9]]",
                    "[redacted]",
                    isRedacted: true,
                    element: DictionaryElement("[redacted-key-9]", isKeyRedacted: true, keyCorrelationId: "v1:kid:production", comparisonKeyCorrelationId: "v1c:kid:same"))
            ]);

        var diff = new ConfigAuditReportDiffer().Compare(
            baseline,
            target,
            new ConfigAuditDiffOptions { IncludeUnchangedItems = true });
        var item = Assert.Single(diff.Items, item => item.Kind == ConfigAuditDiffItemKind.KnownEntry);

        Assert.Equal(ConfigAuditDiffItemStatus.Unchanged, item.Status);
        Assert.Equal(ConfigAuditDiffValueEvidence.BothRedacted, item.ValueEvidence);
        Assert.Contains("raw equality is unknown", item.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_DoesNotUseEnvironmentScopedKeyCorrelationForRedactedDictionaryMatching()
    {
        var baseline = CreateReport(
            "Staging",
            entries:
            [
                Entry(
                    "Tenants[[redacted-key-1]]",
                    "[redacted]",
                    isRedacted: true,
                    element: DictionaryElement("[redacted-key-1]", isKeyRedacted: true, keyCorrelationId: "v1:kid:staging"))
            ]);
        var target = CreateReport(
            "Production",
            entries:
            [
                Entry(
                    "Tenants[[redacted-key-1]]",
                    "[redacted]",
                    isRedacted: true,
                    element: DictionaryElement("[redacted-key-1]", isKeyRedacted: true, keyCorrelationId: "v1:kid:production"))
            ]);

        var diff = new ConfigAuditReportDiffer().Compare(baseline, target);
        var item = Assert.Single(diff.Items, item => item.Kind == ConfigAuditDiffItemKind.DictionaryEntry);

        Assert.Equal(ConfigAuditDiffItemStatus.Uncomparable, item.Status);
        Assert.Contains(item.Diagnostics, diagnostic => diagnostic.Code == "config-diff-redacted-dictionary-key-uncomparable");
    }

    [Fact]
    public void Compare_RedactedDictionaryDescendantsInheritUncomparableIdentity()
    {
        var baseline = CreateReport(
            "Staging",
            entries:
            [
                Entry(
                    "Tenants",
                    null,
                    children:
                    [
                        Entry(
                            "Tenants[[redacted-key-1]]",
                            "[redacted]",
                            isRedacted: true,
                            element: DictionaryElement("[redacted-key-1]", isKeyRedacted: true, keyCorrelationId: "v1:kid:staging"),
                            children:
                            [
                                Entry("Tenants[[redacted-key-1]].Name", "alpha")
                            ])
                    ])
            ]);
        var target = CreateReport(
            "Production",
            entries:
            [
                Entry(
                    "Tenants",
                    null,
                    children:
                    [
                        Entry(
                            "Tenants[[redacted-key-1]]",
                            "[redacted]",
                            isRedacted: true,
                            element: DictionaryElement("[redacted-key-1]", isKeyRedacted: true, keyCorrelationId: "v1:kid:production"),
                            children:
                            [
                                Entry("Tenants[[redacted-key-1]].Name", "alpha")
                            ])
                    ])
            ]);

        var diff = new ConfigAuditReportDiffer().Compare(
            baseline,
            target,
            new ConfigAuditDiffOptions { IncludeUnchangedItems = true });
        var descendant = Assert.Single(diff.Items, item => item.Key == "Tenants[[redacted-key-1]].Name");

        Assert.Equal(ConfigAuditDiffItemKind.DictionaryEntry, descendant.Kind);
        Assert.Equal(ConfigAuditDiffItemStatus.Uncomparable, descendant.Status);
        Assert.Contains(descendant.Diagnostics, diagnostic => diagnostic.Code == "config-diff-redacted-dictionary-key-uncomparable");
    }

    [Fact]
    public void Compare_DictionaryDescendantsMatchByComparisonCorrelationMetadata()
    {
        var baseline = CreateReport(
            "Staging",
            entries:
            [
                Entry(
                    "Tenants",
                    null,
                    children:
                    [
                        Entry(
                            "Tenants[[redacted-key-1]]",
                            null,
                            element: DictionaryElement("[redacted-key-1]", isKeyRedacted: true, keyCorrelationId: "v1:kid:staging", comparisonKeyCorrelationId: "v1c:kid:same"),
                            children:
                            [
                                Entry("Tenants[[redacted-key-1]].Name", "alpha")
                            ])
                    ])
            ]);
        var target = CreateReport(
            "Production",
            entries:
            [
                Entry(
                    "Tenants",
                    null,
                    children:
                    [
                        Entry(
                            "Tenants[[redacted-key-9]]",
                            null,
                            element: DictionaryElement("[redacted-key-9]", isKeyRedacted: true, keyCorrelationId: "v1:kid:production", comparisonKeyCorrelationId: "v1c:kid:same"),
                            children:
                            [
                                Entry("Tenants[[redacted-key-9]].Name", "alpha")
                            ])
                    ])
            ]);

        var diff = new ConfigAuditReportDiffer().Compare(
            baseline,
            target,
            new ConfigAuditDiffOptions { IncludeUnchangedItems = true });
        var descendant = Assert.Single(diff.Items, item => item.Key == "Tenants[[redacted-key-1]].Name");

        Assert.Equal(ConfigAuditDiffItemKind.KnownEntry, descendant.Kind);
        Assert.Equal(ConfigAuditDiffItemStatus.Unchanged, descendant.Status);
        Assert.Equal(ConfigAuditDiffValueEvidence.DisplayValuesComparable, descendant.ValueEvidence);
    }

    [Fact]
    public void Compare_DictionaryEntriesMatchBySafeLabel()
    {
        var baseline = CreateReport(
            "Staging",
            entries:
            [
                Entry(
                    "Tenants[\"tenant-a\"]",
                    "enabled",
                    element: DictionaryElement("tenant-a", isKeyRedacted: false))
            ]);
        var target = CreateReport(
            "Production",
            entries:
            [
                Entry(
                    "Tenants[\"tenant-a\"]",
                    "disabled",
                    element: DictionaryElement("tenant-a", isKeyRedacted: false))
            ]);

        var diff = new ConfigAuditReportDiffer().Compare(baseline, target);
        var item = Assert.Single(diff.Items, item => item.Key == "Tenants[\"tenant-a\"]");

        Assert.Equal(ConfigAuditDiffItemStatus.Changed, item.Status);
        Assert.Equal(ConfigAuditDiffValueEvidence.DisplayValuesComparable, item.ValueEvidence);
    }

    [Fact]
    public void Render_OrdersOutputWarnsForSameHostSummarizesSourcesAndDoesNotLeakSecrets()
    {
        var baseline = CreateReport(
            "Staging",
            entries:
            [
                Entry(
                    "Payment.Secret",
                    "[redacted]",
                    isRedacted: true,
                    sourcePath: "/very/private/appsettings.Staging.json")
            ]);
        var target = CreateReport(
            "Production",
            entries:
            [
                Entry(
                    "Payment.Secret",
                    "[redacted]",
                    isRedacted: true,
                    sourcePath: "/very/private/appsettings.Production.json")
            ]);

        var diff = new ConfigAuditReportDiffer().Compare(
            baseline,
            target,
            new ConfigAuditDiffOptions { IncludeUnchangedItems = true });
        var rendered = new ConfigAuditDiffTextRenderer().Render(diff);

        Assert.Contains("Warning: same-host named-environment comparison", rendered, StringComparison.Ordinal);
        Assert.Contains("raw equality is unknown", rendered, StringComparison.Ordinal);
        Assert.Contains("appsettings.Staging.json", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("/very/private", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_CapturedSnapshotWordingAndFullSourceDetailAreOptIn()
    {
        var baseline = CreateReport(
            "Staging",
            entries: [Entry("Service.Url", "https://service", sourcePath: "/full/path/staging.json")]);
        var target = CreateReport(
            "Production",
            entries: [Entry("Service.Url", "https://other", sourcePath: "/full/path/production.json")]);

        var diff = new ConfigAuditReportDiffer().Compare(
            baseline,
            target,
            new ConfigAuditDiffOptions
            {
                EvidenceMode = ConfigAuditDiffEvidenceMode.CapturedSnapshot,
                SourceDetail = ConfigAuditDiffSourceDetail.Full
            });
        var rendered = new ConfigAuditDiffTextRenderer().Render(diff);

        Assert.Contains("captured snapshots", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("same-host named-environment comparison", rendered, StringComparison.Ordinal);
        Assert.Contains("/full/path/staging.json", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EmptyRetainedItemsAndAllSourceKinds()
    {
        var emptyDiff = new ConfigAuditReportDiffer().Compare(
            CreateReport("Staging"),
            CreateReport("Production"),
            new ConfigAuditDiffOptions { EvidenceMode = ConfigAuditDiffEvidenceMode.CapturedSnapshot });

        var emptyRendered = new ConfigAuditDiffTextRenderer().Render(emptyDiff);

        Assert.Contains("No changed, added, removed, or uncomparable items retained.", emptyRendered, StringComparison.Ordinal);

        var baseline = CreateReport(
            "Staging",
            entries:
            [
                Entry(
                    "Composite.Value",
                    "baseline",
                    sources:
                    [
                        Source(ConfigAuditSourceKind.File, providerName: "FileProvider", filePath: "/full/path/appsettings.json", configPath: "Composite:Value", location: null),
                        Source(ConfigAuditSourceKind.EnvironmentVariable, environmentVariableName: "APP_COMPOSITE_VALUE"),
                        Source(ConfigAuditSourceKind.Default, providerName: "DefaultProvider"),
                        Source(ConfigAuditSourceKind.Missing),
                        Source((ConfigAuditSourceKind)999, providerName: "CustomProvider")
                    ])
            ]);
        var target = CreateReport(
            "Production",
            entries:
            [
                Entry(
                    "Composite.Value",
                    "target",
                    sources:
                    [
                        Source(ConfigAuditSourceKind.File, providerName: "FileProvider", filePath: null, configPath: "Composite:Value", location: null)
                    ])
            ]);

        var rendered = new ConfigAuditDiffTextRenderer().Render(new ConfigAuditReportDiffer().Compare(baseline, target));

        Assert.Contains("FileProvider appsettings.json :: Composite:Value", rendered, StringComparison.Ordinal);
        Assert.Contains("FileProvider  :: Composite:Value", rendered, StringComparison.Ordinal);
        Assert.Contains("Environment variable APP_COMPOSITE_VALUE", rendered, StringComparison.Ordinal);
        Assert.Contains("Default value on DefaultProvider", rendered, StringComparison.Ordinal);
        Assert.Contains("none", rendered, StringComparison.Ordinal);
        Assert.Contains("CustomProvider", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_DiscoveredKeyEvidenceCoversMissingSourceAndUncertaintyBranches()
    {
        var baseline = CreateReport(
            "Staging",
            discoveredKeys:
            [
                Discovered("Only.Baseline", "old", ConfigAuditDiscoveredValueDisplayState.Shown),
                Discovered("Both.Redacted", "[redacted]", ConfigAuditDiscoveredValueDisplayState.Redacted, isRedacted: true),
                Discovered("Redacted.ThenShown", "[redacted]", ConfigAuditDiscoveredValueDisplayState.Redacted, isRedacted: true),
                Discovered("Omitted.Value", null, ConfigAuditDiscoveredValueDisplayState.OmittedInventory),
                Discovered("Unspecified.Value", null, ConfigAuditDiscoveredValueDisplayState.Unspecified),
                Discovered("Source.Only", "same", ConfigAuditDiscoveredValueDisplayState.Shown, sourcePath: "/config/staging.json")
            ]);
        var target = CreateReport(
            "Production",
            discoveredKeys:
            [
                Discovered("Only.Target", "new", ConfigAuditDiscoveredValueDisplayState.Shown),
                Discovered("Both.Redacted", "[redacted]", ConfigAuditDiscoveredValueDisplayState.Redacted, isRedacted: true),
                Discovered("Redacted.ThenShown", "shown", ConfigAuditDiscoveredValueDisplayState.Shown),
                Discovered("Omitted.Value", null, ConfigAuditDiscoveredValueDisplayState.OmittedInventory),
                Discovered("Unspecified.Value", null, ConfigAuditDiscoveredValueDisplayState.Unspecified),
                Discovered("Source.Only", "same", ConfigAuditDiscoveredValueDisplayState.Shown, sourcePath: "/config/production.json")
            ]);

        var diff = new ConfigAuditReportDiffer().Compare(
            baseline,
            target,
            new ConfigAuditDiffOptions { IncludeUnchangedItems = true });
        var rendered = new ConfigAuditDiffTextRenderer().Render(diff);

        Assert.Contains(diff.Items, item => item.Key == "Only.Baseline" && item.Status == ConfigAuditDiffItemStatus.Removed);
        Assert.Contains(diff.Items, item => item.Key == "Only.Target" && item.Status == ConfigAuditDiffItemStatus.Added);
        Assert.Contains(diff.Items, item => item.Key == "Both.Redacted" && item.ValueEvidence == ConfigAuditDiffValueEvidence.BothRedacted);
        Assert.Contains(diff.Items, item => item.Key == "Redacted.ThenShown" && item.ValueEvidence == ConfigAuditDiffValueEvidence.RedactedVersusShown);
        Assert.Contains(diff.Items, item => item.Key == "Omitted.Value" && item.ValueEvidence == ConfigAuditDiffValueEvidence.Omitted);
        Assert.Contains(diff.Items, item => item.Key == "Unspecified.Value" && item.ValueEvidence == ConfigAuditDiffValueEvidence.Unspecified);
        Assert.Contains(diff.Items, item => item.Key == "Source.Only" && item.Kind == ConfigAuditDiffItemKind.Source && item.Significance == ConfigAuditDiffSignificance.Context);
        Assert.Contains("one value was redacted and one was shown", rendered, StringComparison.Ordinal);
        Assert.Contains("value display evidence is incomplete", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_EntryEvidenceCoversRedactedShownOmittedAndManualReportDiagnostics()
    {
        var baseline = CreateReport(
            "",
            generatedAt: default(DateTimeOffset),
            entries:
            [
                Entry("Redacted.ThenShown", "[redacted]", isRedacted: true),
                Entry("Omitted.Value", null)
            ]);
        var target = CreateReport(
            "Production",
            entries:
            [
                Entry("Redacted.ThenShown", "shown"),
                Entry("Omitted.Value", null)
            ]);

        var diff = new ConfigAuditReportDiffer().Compare(
            baseline,
            target,
            new ConfigAuditDiffOptions { IncludeUnchangedItems = true });
        var rendered = new ConfigAuditDiffTextRenderer().Render(diff);

        Assert.Contains(diff.Diagnostics, diagnostic => diagnostic.Message.Contains("no environment name", StringComparison.Ordinal));
        Assert.Contains(diff.Diagnostics, diagnostic => diagnostic.Message.Contains("default generated timestamp", StringComparison.Ordinal));
        Assert.Contains(diff.Items, item => item.Key == "Redacted.ThenShown" && item.ValueEvidence == ConfigAuditDiffValueEvidence.RedactedVersusShown);
        Assert.Contains(diff.Items, item => item.Key == "Omitted.Value" && item.ValueEvidence == ConfigAuditDiffValueEvidence.Omitted);
        Assert.Contains("one value was redacted and one was shown", rendered, StringComparison.Ordinal);
        Assert.Contains("at least one display value was omitted", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_DiagnosticChangesCoverUnchangedInfoAndFallbackKeys()
    {
        var baseline = CreateReport(
            "Staging",
            diagnostics:
            [
                Diagnostic(ConfigAuditDiagnosticSeverity.Info, "same-info", "Same info."),
                Diagnostic(ConfigAuditDiagnosticSeverity.Info, "changed-info", "Old info."),
                Diagnostic(ConfigAuditDiagnosticSeverity.Warning, "fallback-code", "Old fallback.", key: null, configPath: null)
            ]);
        var target = CreateReport(
            "Production",
            diagnostics:
            [
                Diagnostic(ConfigAuditDiagnosticSeverity.Info, "same-info", "Same info."),
                Diagnostic(ConfigAuditDiagnosticSeverity.Info, "changed-info", "New info."),
                Diagnostic(ConfigAuditDiagnosticSeverity.Warning, "fallback-code", "New fallback.", key: null, configPath: null)
            ]);

        var diff = new ConfigAuditReportDiffer().Compare(
            baseline,
            target,
            new ConfigAuditDiffOptions { IncludeUnchangedItems = true });

        Assert.Contains(diff.Items, item => item.Key == "same-info" && item.Status == ConfigAuditDiffItemStatus.Unchanged && item.Significance == ConfigAuditDiffSignificance.Unchanged);
        Assert.Contains(diff.Items, item => item.Key == "changed-info" && item.Status == ConfigAuditDiffItemStatus.Changed && item.Significance == ConfigAuditDiffSignificance.Context);
        Assert.Contains(diff.Items, item => item.Key == "fallback-code" && item.Status == ConfigAuditDiffItemStatus.Changed);
    }

    [Fact]
    public void Render_ManualReportCoversFallbackWordingAndItemDiagnostics()
    {
        var report = new ConfigAuditDiffReport
        {
            BaselineEnvironment = "Staging",
            TargetEnvironment = "Production",
            GeneratedAt = DateTimeOffset.UtcNow,
            EvidenceMode = (ConfigAuditDiffEvidenceMode)999,
            SourceDetail = ConfigAuditDiffSourceDetail.Summarized,
            Summary = new ConfigAuditDiffSummary
            {
                Changed = 1,
                Added = 0,
                Removed = 0,
                Unchanged = 0,
                Uncomparable = 0,
                Diagnostics = 0
            },
            Items =
            [
                new ConfigAuditDiffItem
                {
                    Kind = ConfigAuditDiffItemKind.KnownEntry,
                    Status = ConfigAuditDiffItemStatus.Changed,
                    Significance = ConfigAuditDiffSignificance.NeedsAttention,
                    Key = "Manual.Value",
                    Description = "Manual item.",
                    ValueEvidence = (ConfigAuditDiffValueEvidence)999,
                    Diagnostics =
                    [
                        new ConfigAuditComparisonDiagnostic
                        {
                            Severity = ConfigAuditDiagnosticSeverity.Warning,
                            Code = "manual-diagnostic",
                            Message = "Manual diagnostic."
                        }
                    ]
                }
            ]
        };

        var rendered = new ConfigAuditDiffTextRenderer().Render(report);

        Assert.Contains("Evidence: 999", rendered, StringComparison.Ordinal);
        Assert.Contains("Value evidence: 999", rendered, StringComparison.Ordinal);
        Assert.Contains("Diagnostic: [Warning] manual-diagnostic: Manual diagnostic.", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_UsesReporterDifferAndRendererWithoutCommandFramework()
    {
        var reporter = A.Fake<IConfigAuditReporter>();
        A.CallTo(() => reporter.GetReport("Staging")).Returns(CreateReport("Staging", entries: [Entry("Feature.Enabled", "false")]));
        A.CallTo(() => reporter.GetReport("Production")).Returns(CreateReport("Production", entries: [Entry("Feature.Enabled", "true")]));
        using var output = new StringWriter();
        var runner = new ConfigAuditDiffCommandRunner(
            reporter,
            new ConfigAuditReportDiffer(),
            new ConfigAuditDiffTextRenderer());

        var result = runner.Run("Staging", "Production", output);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Config audit diff: Staging -> Production", output.ToString(), StringComparison.Ordinal);
        A.CallTo(() => reporter.GetReport("Staging")).MustHaveHappenedOnceExactly();
        A.CallTo(() => reporter.GetReport("Production")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void Run_OperationalFailuresAreSanitizedWithDocsLink()
    {
        var reporter = A.Fake<IConfigAuditReporter>();
        A.CallTo(() => reporter.GetReport("Staging")).Throws(new InvalidOperationException("super-secret provider path"));
        using var output = new StringWriter();
        var runner = new ConfigAuditDiffCommandRunner(
            reporter,
            new ConfigAuditReportDiffer(),
            new ConfigAuditDiffTextRenderer());

        var result = runner.Run("Staging", "Production", output);

        Assert.False(result.Succeeded);
        Assert.Equal(ConfigAuditDiffFailureStage.Baseline, result.Failure!.Stage);
        Assert.Contains("Docs:", result.Failure.ToDisplayString(), StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", result.Failure.ToDisplayString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void Run_InputAndTargetFailuresAreSanitized()
    {
        var reporter = A.Fake<IConfigAuditReporter>();
        A.CallTo(() => reporter.GetReport("Staging")).Returns(CreateReport("Staging"));
        A.CallTo(() => reporter.GetReport("Production")).Throws(new InvalidOperationException("super-secret target path"));
        var runner = new ConfigAuditDiffCommandRunner(
            reporter,
            new ConfigAuditReportDiffer(),
            new ConfigAuditDiffTextRenderer());

        var blankBaseline = runner.Run("", "Production", TextWriter.Null);
        var blankTarget = runner.Run("Staging", " ", TextWriter.Null);
        var targetFailure = runner.Run("Staging", "Production", TextWriter.Null);

        Assert.Equal(ConfigAuditDiffFailureStage.Baseline, blankBaseline.Failure!.Stage);
        Assert.Equal(ConfigAuditDiffFailureStage.Target, blankTarget.Failure!.Stage);
        Assert.Equal(ConfigAuditDiffFailureStage.Target, targetFailure.Failure!.Stage);
        Assert.DoesNotContain("super-secret", targetFailure.Failure.ToDisplayString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_CompareAndRenderFailuresAreSanitized()
    {
        var reporter = A.Fake<IConfigAuditReporter>();
        A.CallTo(() => reporter.GetReport("Invalid")).Returns(new ConfigAuditReport
        {
            Environment = "Invalid",
            GeneratedAt = DateTimeOffset.UtcNow,
            Providers = [],
            Entries = null!,
            DiscoveredKeys = [],
            Diagnostics = [],
            Redaction = Redaction()
        });
        A.CallTo(() => reporter.GetReport("Production")).Returns(CreateReport("Production"));
        A.CallTo(() => reporter.GetReport("Staging")).Returns(CreateReport("Staging"));
        var runner = new ConfigAuditDiffCommandRunner(
            reporter,
            new ConfigAuditReportDiffer(),
            new ConfigAuditDiffTextRenderer());

        var compareFailure = runner.Run("Invalid", "Production", TextWriter.Null);
        var renderFailure = runner.Run("Staging", "Production", new ThrowingTextWriter());

        Assert.Equal(ConfigAuditDiffFailureStage.Compare, compareFailure.Failure!.Stage);
        Assert.DoesNotContain("NullReferenceException", compareFailure.Failure.Problem, StringComparison.Ordinal);
        Assert.Equal(ConfigAuditDiffFailureStage.Render, renderFailure.Failure!.Stage);
        Assert.Contains("InvalidOperationException", renderFailure.Failure.ToDisplayString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunCapturedSnapshots_ParsesJsonAndUsesCapturedEvidenceMode()
    {
        using var output = new StringWriter();
        var runner = new ConfigAuditDiffCommandRunner(
            A.Fake<IConfigAuditReporter>(),
            new ConfigAuditReportDiffer(),
            new ConfigAuditDiffTextRenderer());
        var baseline = JsonSerializer.Serialize(CreateReport("Staging", entries: [Entry("Feature.Enabled", "false")]));
        var target = JsonSerializer.Serialize(CreateReport("Production", entries: [Entry("Feature.Enabled", "true")]));

        var result = runner.RunCapturedSnapshots(baseline, target, output);

        Assert.True(result.Succeeded);
        Assert.Contains("captured snapshots", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("same-host named-environment comparison", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunCapturedSnapshots_SuccessPreservesIncludeUnchangedAndSourceDetailOptions()
    {
        using var output = new StringWriter();
        var runner = new ConfigAuditDiffCommandRunner(
            A.Fake<IConfigAuditReporter>(),
            new ConfigAuditReportDiffer(),
            new ConfigAuditDiffTextRenderer());
        var baseline = JsonSerializer.Serialize(CreateReport("Staging", entries: [Entry("Feature.Enabled", "same", sourcePath: "/full/path/staging.json")]));
        var target = JsonSerializer.Serialize(CreateReport("Production", entries: [Entry("Feature.Enabled", "same", sourcePath: "/full/path/staging.json")]));

        var result = runner.RunCapturedSnapshots(
            baseline,
            target,
            output,
            new ConfigAuditDiffOptions
            {
                IncludeUnchangedItems = true,
                SourceDetail = ConfigAuditDiffSourceDetail.Full
            });

        Assert.True(result.Succeeded);
        Assert.Contains("Unchanged KnownEntry: Feature.Enabled", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("/full/path/staging.json", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunCapturedSnapshots_ParseFailureIsSanitized()
    {
        using var output = new StringWriter();
        var runner = new ConfigAuditDiffCommandRunner(
            A.Fake<IConfigAuditReporter>(),
            new ConfigAuditReportDiffer(),
            new ConfigAuditDiffTextRenderer());

        var result = runner.RunCapturedSnapshots("{ not json super-secret", "{}", output);

        Assert.False(result.Succeeded);
        Assert.Equal(ConfigAuditDiffFailureStage.SnapshotParse, result.Failure!.Stage);
        Assert.DoesNotContain("super-secret", result.Failure.ToDisplayString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void RunCapturedSnapshots_TargetParseFailureAndOptionsAreSanitized()
    {
        using var output = new StringWriter();
        var runner = new ConfigAuditDiffCommandRunner(
            A.Fake<IConfigAuditReporter>(),
            new ConfigAuditReportDiffer(),
            new ConfigAuditDiffTextRenderer());
        var baseline = JsonSerializer.Serialize(CreateReport("Staging", entries: [Entry("Feature.Enabled", "same")]));

        var result = runner.RunCapturedSnapshots(
            baseline,
            "{ not json super-secret",
            output,
            new ConfigAuditDiffOptions
            {
                EvidenceMode = ConfigAuditDiffEvidenceMode.SameHostNamedEnvironment,
                IncludeUnchangedItems = true,
                SourceDetail = ConfigAuditDiffSourceDetail.Full
            });

        Assert.False(result.Succeeded);
        Assert.Equal("Staging", result.BaselineEnvironment);
        Assert.Null(result.TargetEnvironment);
        Assert.Equal(ConfigAuditDiffFailureStage.SnapshotParse, result.Failure!.Stage);
        Assert.DoesNotContain("super-secret", result.Failure.ToDisplayString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void RunCapturedSnapshots_EmptySnapshotFailureIsSanitizedWithoutExceptionTypeForInputFailures()
    {
        var runner = new ConfigAuditDiffCommandRunner(
            A.Fake<IConfigAuditReporter>(),
            new ConfigAuditReportDiffer(),
            new ConfigAuditDiffTextRenderer());

        var result = runner.RunCapturedSnapshots(" ", "{}", TextWriter.Null);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(ConfigAuditDiffFailureStage.SnapshotParse, result.Failure!.Stage);
        Assert.Contains("ArgumentException", result.Failure.ToDisplayString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_HandlesThousandsOfDuplicateCollisions()
    {
        var baselineEntries = Enumerable.Range(0, 1_500)
            .Select(index => Entry($"Bulk.{index % 25}", index.ToString()))
            .ToArray();
        var targetEntries = Enumerable.Range(0, 1_500)
            .Select(index => Entry($"Bulk.{index % 25}", index.ToString()))
            .ToArray();
        var baselineDiscovered = Enumerable.Range(0, 1_500)
            .Select(index => Discovered($"Bulk:{index % 25}", index.ToString(), ConfigAuditDiscoveredValueDisplayState.Shown))
            .ToArray();
        var targetDiscovered = Enumerable.Range(0, 1_500)
            .Select(index => Discovered($"Bulk:{index % 25}", index.ToString(), ConfigAuditDiscoveredValueDisplayState.Shown))
            .ToArray();

        var diff = new ConfigAuditReportDiffer().Compare(
            CreateReport("Staging", entries: baselineEntries, discoveredKeys: baselineDiscovered),
            CreateReport("Production", entries: targetEntries, discoveredKeys: targetDiscovered),
            new ConfigAuditDiffOptions { IncludeUnchangedItems = true });

        Assert.Equal(3_001, diff.Summary.Unchanged);
        Assert.Contains(diff.Diagnostics, diagnostic => diagnostic.Code == "config-diff-duplicate-evidence");
    }

    private static ConfigAuditReport CreateReport(
        string environment,
        IReadOnlyList<ConfigAuditProvider>? providers = null,
        IReadOnlyList<ConfigAuditEntry>? entries = null,
        IReadOnlyList<ConfigAuditDiscoveredKey>? discoveredKeys = null,
        IReadOnlyList<ConfigAuditDiagnostic>? diagnostics = null,
        ConfigAuditRedaction? redaction = null,
        DateTimeOffset? generatedAt = null) =>
        new()
        {
            Environment = environment,
            GeneratedAt = generatedAt ?? DateTimeOffset.UtcNow,
            Providers = providers ?? [Provider("EnvironmentConfigProvider", priority: -1, precedence: 0, isOverride: true)],
            Entries = entries ?? [],
            DiscoveredKeys = discoveredKeys ?? [],
            Diagnostics = diagnostics ?? [],
            Redaction = redaction ?? Redaction()
        };

    private static ConfigAuditEntry Entry(
        string key,
        string? value,
        bool isRedacted = false,
        string sourcePath = "/config/appsettings.json",
        ConfigAuditElementIdentity? element = null,
        IReadOnlyList<ConfigAuditEntry>? children = null,
        IReadOnlyList<ConfigAuditSourceRecord>? sources = null) =>
        new()
        {
            Key = key,
            DeclaredType = "System.String",
            State = ConfigAuditEntryState.Resolved,
            DisplayValue = value,
            IsRedacted = isRedacted,
            Element = element,
            Children = children ?? [],
            Sources = sources ??
            [
                new ConfigAuditSourceRecord
                {
                    Kind = ConfigAuditSourceKind.File,
                    ProviderName = "FileBasedConfigProvider",
                    ProviderPriority = 10,
                    FilePath = sourcePath,
                    ConfigPath = key,
                    AppliedToPath = key,
                    Role = ConfigAuditSourceRole.Base,
                    Location = new ConfigAuditSourceLocation(2, 4)
                }
            ]
        };

    private static ConfigAuditSourceRecord Source(
        ConfigAuditSourceKind kind,
        string? providerName = null,
        string? filePath = null,
        string? environmentVariableName = null,
        string? configPath = null,
        ConfigAuditSourceLocation? location = null) =>
        new()
        {
            Kind = kind,
            ProviderName = providerName,
            FilePath = filePath,
            EnvironmentVariableName = environmentVariableName,
            ConfigPath = configPath,
            AppliedToPath = configPath,
            Role = ConfigAuditSourceRole.Base,
            Location = location
        };

    private static ConfigAuditElementIdentity DictionaryElement(
        string keyLabel,
        bool isKeyRedacted,
        string? keyCorrelationId = null,
        string? comparisonKeyCorrelationId = null) =>
        new()
        {
            Kind = ConfigAuditElementKind.DictionaryItem,
            KeyLabel = keyLabel,
            IsKeyRedacted = isKeyRedacted,
            KeyCorrelationId = keyCorrelationId,
            ComparisonKeyCorrelationId = comparisonKeyCorrelationId
        };

    private static ConfigAuditProvider Provider(
        string name,
        int priority,
        int precedence,
        bool isOverride = false) =>
        new()
        {
            Name = name,
            Priority = priority,
            Precedence = precedence,
            IsOverride = isOverride
        };

    private static ConfigAuditDiscoveredKey Discovered(
        string key,
        string? value,
        ConfigAuditDiscoveredValueDisplayState valueDisplayState,
        bool isRedacted = false,
        string sourcePath = "/config/appsettings.json") =>
        new()
        {
            Key = key,
            Classification = ConfigAuditDiscoveredKeyClassification.Unknown,
            DisplayValue = value,
            ValueDisplayState = valueDisplayState,
            IsRedacted = isRedacted,
            Sources =
            [
                new ConfigAuditSourceRecord
                {
                    Kind = ConfigAuditSourceKind.File,
                    ProviderName = "FileBasedConfigProvider",
                    FilePath = sourcePath,
                    ConfigPath = key,
                    AppliedToPath = key,
                    Role = ConfigAuditSourceRole.Base
                }
            ]
        };

    private static ConfigAuditDiagnostic Diagnostic(
        ConfigAuditDiagnosticSeverity severity,
        string code,
        string message,
        string? key = null,
        string? configPath = null) =>
        new()
        {
            Severity = severity,
            Code = code,
            Message = message,
            Key = key ?? code,
            ConfigPath = configPath ?? code
        };

    private static ConfigAuditRedaction Redaction(string placeholder = "[redacted]") =>
        new()
        {
            Enabled = true,
            Placeholder = placeholder,
            MatchedFragments = ["secret", "token", "password"],
            DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac,
            DictionaryKeyCorrelationKeyId = "kid",
            DictionaryKeyCorrelationApplicationScope = "app"
        };

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(string? value) =>
            throw new InvalidOperationException("super-secret writer path");
    }
}
