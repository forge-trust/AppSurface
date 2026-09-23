using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigDiagnosticRenderingTests
{
    [Fact]
    public void ReportIdentifiersAreBoundedAndControlCharactersCannotInjectLines()
    {
        var limits = new ConfigResourceOptions { MaxRenderedIdentifierCharacters = 16 };
        var renderer = new ConfigAuditTextRenderer(Options.Create(limits));
        limits.MaxRenderedIdentifierCharacters = 1000;
        var identifier = "native\n" + new string('x', 300);
        var diagnostic = new ConfigAuditDiagnostic
        {
            Severity = ConfigAuditDiagnosticSeverity.Warning,
            Code = "code\nforged",
            Message = "reason\r\nforged"
        };
        var source = new ConfigAuditSourceRecord
        {
            Role = ConfigAuditSourceRole.Base,
            Kind = ConfigAuditSourceKind.File,
            ProviderName = identifier,
            FilePath = identifier,
            ConfigPath = identifier,
            Location = new ConfigAuditSourceLocation(1, 2)
        };
        var report = new ConfigAuditReport
        {
            Environment = identifier,
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Redaction = new ConfigAuditRedaction { Enabled = true, Placeholder = "[redacted]" },
            Providers = [new ConfigAuditProvider { Name = identifier, Precedence = 1, Priority = 1, IsOverride = false }],
            Entries = [new ConfigAuditEntry { Key = identifier, State = ConfigAuditEntryState.Resolved, DisplayValue = "[redacted]", IsRedacted = true,
                Sources = [source], Diagnostics = [diagnostic] }],
            DiscoveredKeys = [new ConfigAuditDiscoveredKey { Key = identifier, Classification = ConfigAuditDiscoveredKeyClassification.Unknown, Sources = [source], Diagnostics = [diagnostic] }],
            Diagnostics = [diagnostic]
        };
        var rendered = renderer.Render(report);
        Assert.DoesNotContain(identifier, rendered);
        Assert.DoesNotContain("\nforged", rendered);
        Assert.Contains("native\\u000a", rendered);
        Assert.Contains("…#", rendered);
        Assert.Contains("reason\\u000d\\u000aforged", rendered);
        Assert.Contains("[redacted]", rendered);
        Assert.Throws<ArgumentNullException>(() => new ConfigAuditTextRenderer(null!));
        Assert.Throws<OptionsValidationException>(() => new ConfigAuditTextRenderer(Options.Create(new ConfigResourceOptions { MaxRenderedIdentifierCharacters = 0 })));
    }

    [Fact]
    public void ReportDisplayValuesCannotInjectLines()
    {
        var report = new ConfigAuditReport
        {
            Environment = "Production",
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Redaction = new ConfigAuditRedaction { Enabled = false, Placeholder = "[redacted]" },
            Entries = [new ConfigAuditEntry
            {
                Key = "Entry", State = ConfigAuditEntryState.Resolved, DisplayValue = "safe\nDiagnostic: forged"
            }],
            DiscoveredKeys = [new ConfigAuditDiscoveredKey
            {
                Key = "Discovered", Classification = ConfigAuditDiscoveredKeyClassification.Unknown,
                DisplayValue = "safe\nSource: forged",
                ValueDisplayState = ConfigAuditDiscoveredValueDisplayState.Shown
            }]
        };

        var rendered = new ConfigAuditTextRenderer().Render(report);

        Assert.DoesNotContain("\nDiagnostic: forged", rendered);
        Assert.DoesNotContain("\nSource: forged", rendered);
        Assert.Contains("safe\\u000aDiagnostic: forged", rendered);
        Assert.Contains("safe\\u000aSource: forged", rendered);
    }

    [Fact]
    public void DiffIdentifiersUseTheSameBoundsInFullSourceDetail()
    {
        var identifier = "native\u202e" + new string('x', 300);
        var report = new ConfigAuditDiffReport
        {
            BaselineEnvironment = identifier,
            TargetEnvironment = identifier,
            GeneratedAt = DateTimeOffset.UnixEpoch,
            SourceDetail = ConfigAuditDiffSourceDetail.Full,
            EvidenceMode = ConfigAuditDiffEvidenceMode.CapturedSnapshot,
            Summary = new ConfigAuditDiffSummary { Changed = 1, Added = 0, Removed = 0, Unchanged = 0, Uncomparable = 0, Diagnostics = 1 },
            Items = [new ConfigAuditDiffItem
            {
                Kind = ConfigAuditDiffItemKind.KnownEntry, Status = ConfigAuditDiffItemStatus.Changed, Significance = ConfigAuditDiffSignificance.Context, Key = identifier, Description = "safe\nexplanation",
                BaselineSources = [new ConfigAuditSourceRecord { Role = ConfigAuditSourceRole.Base, Kind = ConfigAuditSourceKind.File, FilePath = identifier, ProviderName = identifier, ConfigPath = identifier }],
                TargetSources = [new ConfigAuditSourceRecord { Role = ConfigAuditSourceRole.Override, Kind = ConfigAuditSourceKind.EnvironmentVariable, EnvironmentVariableName = identifier }],
                Diagnostics = [new ConfigAuditComparisonDiagnostic { Severity = ConfigAuditDiagnosticSeverity.Warning, Code = identifier, Message = "safe\nexplanation" }]
            }],
            Diagnostics = [new ConfigAuditComparisonDiagnostic { Severity = ConfigAuditDiagnosticSeverity.Warning, Code = identifier, Message = "safe\nexplanation" }]
        };
        var rendered = new ConfigAuditDiffTextRenderer(Options.Create(new ConfigResourceOptions { MaxRenderedIdentifierCharacters = 16 })).Render(report);
        Assert.DoesNotContain(identifier, rendered);
        Assert.DoesNotContain("\nexplanation", rendered);
        Assert.Contains("native\\u202e", rendered);
        Assert.Contains("safe\\u000aexplanation", rendered);
        Assert.Contains("…#", rendered);
        Assert.Throws<ArgumentNullException>(() => new ConfigAuditDiffTextRenderer(null!));
    }

    [Fact]
    public void DiffDisplayValuesCannotInjectLines()
    {
        var report = new ConfigAuditDiffReport
        {
            BaselineEnvironment = "Production",
            TargetEnvironment = "Production",
            GeneratedAt = DateTimeOffset.UnixEpoch,
            SourceDetail = ConfigAuditDiffSourceDetail.Summarized,
            EvidenceMode = ConfigAuditDiffEvidenceMode.CapturedSnapshot,
            Summary = new ConfigAuditDiffSummary
            {
                Changed = 1,
                Added = 0,
                Removed = 0,
                Unchanged = 0,
                Uncomparable = 0,
                Diagnostics = 0
            },
            Items = [new ConfigAuditDiffItem
            {
                Kind = ConfigAuditDiffItemKind.KnownEntry,
                Status = ConfigAuditDiffItemStatus.Changed,
                Significance = ConfigAuditDiffSignificance.Context,
                Key = "Entry",
                Description = "changed",
                ValueEvidence = ConfigAuditDiffValueEvidence.DisplayValuesComparable,
                BaselineDisplayValue = "before\nSource: forged",
                TargetDisplayValue = "after\nDiagnostic: forged"
            }]
        };

        var rendered = new ConfigAuditDiffTextRenderer().Render(report);

        Assert.DoesNotContain("\nSource: forged", rendered);
        Assert.DoesNotContain("\nDiagnostic: forged", rendered);
        Assert.Contains("before\\u000aSource: forged", rendered);
        Assert.Contains("after\\u000aDiagnostic: forged", rendered);
    }

    [Fact]
    public void ExplicitProseIsBoundedWithoutFingerprintingItsContent()
    {
        Assert.Equal("text", ConfigDiagnosticText.Prose("text", 4));
        Assert.Equal("text…", ConfigDiagnosticText.Prose("text-more", 4));
        Assert.Equal("…", ConfigDiagnosticText.Prose("\n", 4));
        Assert.Equal("(none)", ConfigDiagnosticText.Prose(null));
        Assert.Equal("\\u000a", ConfigDiagnosticText.Prose("\n"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConfigDiagnosticText.Prose("text", 0));
        var diagnostic = new ConfigAuditDiagnostic { Severity = ConfigAuditDiagnosticSeverity.Warning, Code = "external", Message = new string('x', 5000) };
        var report = new ConfigAuditReport
        {
            Environment = "Production",
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Redaction = new ConfigAuditRedaction { Enabled = true, Placeholder = "[redacted]" },
            Diagnostics = [diagnostic]
        };
        var rendered = new ConfigAuditTextRenderer().Render(report);
        Assert.Contains(new string('x', 4096) + "…", rendered);
        Assert.DoesNotContain(new string('x', 4097), rendered);
        Assert.DoesNotContain("…#", rendered);
    }
}
