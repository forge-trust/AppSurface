using FakeItEasy;
using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigDiagnosticsCommandRunnerTests
{
    [Fact]
    public void Run_UsesActiveEnvironmentAndWritesRenderedReport()
    {
        var reporter = A.Fake<IConfigAuditReporter>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environmentProvider.Environment).Returns("Staging");
        A.CallTo(() => reporter.GetReport("Staging")).Returns(CreateReport(
            "Staging",
            new ConfigAuditEntry
            {
                Key = "Billing.Endpoint",
                State = ConfigAuditEntryState.Resolved,
                DisplayValue = "https://billing.internal",
                Sources =
                [
                    new ConfigAuditSourceRecord
                    {
                        Kind = ConfigAuditSourceKind.File,
                        ProviderName = "FileBasedConfigProvider",
                        FilePath = "/repo/appsettings.Staging.json",
                        ConfigPath = "Billing.Endpoint",
                        AppliedToPath = "Billing.Endpoint",
                        Role = ConfigAuditSourceRole.Base
                    }
                ]
            }));
        using var output = new StringWriter();
        var runner = new ConfigDiagnosticsCommandRunner(
            reporter,
            new ConfigAuditTextRenderer(),
            environmentProvider);

        var result = runner.Run(output);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Staging", result.Environment);
        Assert.Null(result.Failure);
        Assert.Contains("Environment: Staging", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Billing.Endpoint = https://billing.internal", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Mode:", output.ToString(), StringComparison.Ordinal);
        A.CallTo(() => reporter.GetReport("Staging")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void Run_WithDefaultModeOverload_UsesLegacyReportGetReport()
    {
        var reporter = A.Fake<IConfigAuditReporter>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => reporter.GetReport("Production")).Returns(CreateReport(
            "Production",
            new ConfigAuditEntry
            {
                Key = "Billing.Endpoint",
                State = ConfigAuditEntryState.Resolved,
                DisplayValue = "https://billing.internal",
                Sources =
                [
                    new ConfigAuditSourceRecord
                    {
                        Kind = ConfigAuditSourceKind.File,
                        ProviderName = "FileBasedConfigProvider",
                        FilePath = "/repo/appsettings.Production.json",
                        ConfigPath = "Billing.Endpoint",
                        AppliedToPath = "Billing.Endpoint",
                        Role = ConfigAuditSourceRole.Base
                    }
                ]
            }));
        using var output = new StringWriter();
        var runner = new ConfigDiagnosticsCommandRunner(
            reporter,
            new ConfigAuditTextRenderer(),
            environmentProvider);

        var result = runner.Run(output, ConfigAuditReportMode.Default);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Production", result.Environment);
        Assert.Null(result.Failure);
        Assert.DoesNotContain("Mode: ExpandKnownEntryCollections", output.ToString(), StringComparison.Ordinal);
        A.CallTo(() => reporter.GetReport("Production")).MustHaveHappenedOnceExactly();
        A.CallTo(() => reporter.GetReport(A<ConfigAuditReportRequest>._)).MustNotHaveHappened();
    }

    [Fact]
    public void Run_WithExpandedMode_UsesReportRequestAndRendersMode()
    {
        var reporter = A.Fake<IConfigAuditReporter>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => reporter.GetReport(
            A<ConfigAuditReportRequest>.That.Matches(request =>
                    request.Environment == "Production"
                    && request.Mode == ConfigAuditReportMode.ExpandKnownEntryCollections)))
            .Returns(CreateReport(
                "Production",
                ConfigAuditReportMode.ExpandKnownEntryCollections,
                new ConfigAuditEntry
                {
                    Key = "App:Secrets",
                    State = ConfigAuditEntryState.Resolved,
                    DisplayValue = "{...}",
                    Sources =
                    [
                        new ConfigAuditSourceRecord
                        {
                            Kind = ConfigAuditSourceKind.File,
                            ProviderName = "FileBasedConfigProvider",
                            FilePath = "/repo/appsettings.Production.json",
                            ConfigPath = "App:Secrets",
                            AppliedToPath = "App:Secrets",
                            Role = ConfigAuditSourceRole.Base
                        }
                    ]
                }));
        using var output = new StringWriter();
        var runner = new ConfigDiagnosticsCommandRunner(
            reporter,
            new ConfigAuditTextRenderer(),
            environmentProvider);

        var result = runner.Run(output, ConfigAuditReportMode.ExpandKnownEntryCollections);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Mode: ExpandKnownEntryCollections", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("App:Secrets", output.ToString(), StringComparison.Ordinal);
        A.CallTo(() => reporter.GetReport(
                A<ConfigAuditReportRequest>.That.Matches(request =>
                    request.Environment == "Production"
                    && request.Mode == ConfigAuditReportMode.ExpandKnownEntryCollections)))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => reporter.GetReport("Production")).MustNotHaveHappened();
    }

    [Fact]
    public void Run_WithUndefinedMode_ReturnsSanitizedFailureWithoutCallingReporter()
    {
        var reporter = A.Fake<IConfigAuditReporter>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        using var output = new StringWriter();
        var runner = new ConfigDiagnosticsCommandRunner(
            reporter,
            new ConfigAuditTextRenderer(),
            environmentProvider);

        var result = runner.Run(output, (ConfigAuditReportMode)42);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(output.ToString());
        Assert.NotNull(result.Failure);
        Assert.Contains(nameof(ArgumentOutOfRangeException), result.Failure!.ToDisplayString(), StringComparison.Ordinal);
        A.CallTo(() => reporter.GetReport(A<string>._)).MustNotHaveHappened();
        A.CallTo(() => reporter.GetReport(A<ConfigAuditReportRequest>._)).MustNotHaveHappened();
    }

    [Fact]
    public void Run_MissingAndInvalidEntriesRemainSuccessfulInspectionResults()
    {
        var reporter = A.Fake<IConfigAuditReporter>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => reporter.GetReport("Production")).Returns(CreateReport(
            "Production",
            new ConfigAuditEntry
            {
                Key = "Missing.RequiredApiUrl",
                State = ConfigAuditEntryState.Missing,
                Sources =
                [
                    new ConfigAuditSourceRecord
                    {
                        Kind = ConfigAuditSourceKind.Missing,
                        ConfigPath = "Missing.RequiredApiUrl",
                        AppliedToPath = "Missing.RequiredApiUrl",
                        Role = ConfigAuditSourceRole.Base
                    }
                ]
            },
            new ConfigAuditEntry
            {
                Key = "Retry.Count",
                State = ConfigAuditEntryState.Invalid,
                DisplayValue = "10",
                Diagnostics =
                [
                    new ConfigAuditDiagnostic
                    {
                        Severity = ConfigAuditDiagnosticSeverity.Error,
                        Code = "config-validation-failed",
                        Key = "Retry.Count",
                        ConfigPath = "Retry.Count",
                        Message = "The configuration value must be between 1 and 5."
                    }
                ]
            }));
        using var output = new StringWriter();
        var runner = new ConfigDiagnosticsCommandRunner(
            reporter,
            new ConfigAuditTextRenderer(),
            environmentProvider);

        var result = runner.Run(output);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Missing.RequiredApiUrl", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("State: Missing", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Retry.Count = 10", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("State: Invalid", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_RedactedReportDoesNotRevealSecretValues()
    {
        var reporter = A.Fake<IConfigAuditReporter>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => reporter.GetReport("Production")).Returns(CreateReport(
            "Production",
            new ConfigAuditEntry
            {
                Key = "Payment.ApiKey",
                State = ConfigAuditEntryState.Resolved,
                DisplayValue = "[redacted]",
                IsRedacted = true
            }));
        using var output = new StringWriter();
        var runner = new ConfigDiagnosticsCommandRunner(
            reporter,
            new ConfigAuditTextRenderer(),
            environmentProvider);

        var result = runner.Run(output);

        Assert.True(result.Succeeded);
        Assert.Contains("Payment.ApiKey = [redacted]", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", output.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<Exception> OperationalFailures =>
        new()
        {
            new InvalidOperationException("provider failed with super-secret"),
            new ArgumentException("provider argument included super-secret"),
            new FormatException("renderer format included super-secret"),
            new IOException("output path included super-secret"),
            new UnauthorizedAccessException("output permission included super-secret"),
            new DiagnosticsProviderException("custom provider failure included super-secret")
        };

    [Theory]
    [MemberData(nameof(OperationalFailures))]
    public void Run_OperationalFailureReturnsSanitizedFailure(Exception exception)
    {
        var reporter = A.Fake<IConfigAuditReporter>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => reporter.GetReport("Production")).Throws(exception);
        using var output = new StringWriter();
        var runner = new ConfigDiagnosticsCommandRunner(
            reporter,
            new ConfigAuditTextRenderer(),
            environmentProvider);

        var result = runner.Run(output);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(output.ToString());
        Assert.NotNull(result.Failure);
        var display = result.Failure!.ToDisplayString();
        Assert.Contains("Problem:", display, StringComparison.Ordinal);
        Assert.Contains("Cause:", display, StringComparison.Ordinal);
        Assert.Contains("Fix:", display, StringComparison.Ordinal);
        Assert.Contains(exception.GetType().Name, display, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", display, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_ExpandedModeUnsupportedReporterReturnsSanitizedFailure()
    {
        var reporter = A.Fake<IConfigAuditReporter>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => reporter.GetReport(A<ConfigAuditReportRequest>._))
            .Throws(new NotSupportedException("super-secret should never leak"));
        using var output = new StringWriter();
        var runner = new ConfigDiagnosticsCommandRunner(
            reporter,
            new ConfigAuditTextRenderer(),
            environmentProvider);

        var result = runner.Run(output, ConfigAuditReportMode.ExpandKnownEntryCollections);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(output.ToString());
        Assert.NotNull(result.Failure);
        var display = result.Failure!.ToDisplayString();
        Assert.Contains(nameof(NotSupportedException), display, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", display, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_NullOutput_Throws()
    {
        var runner = new ConfigDiagnosticsCommandRunner(
            A.Fake<IConfigAuditReporter>(),
            new ConfigAuditTextRenderer(),
            A.Fake<IEnvironmentProvider>());

        Assert.Throws<ArgumentNullException>(() => runner.Run(null!));
    }

    [Fact]
    public void Run_EmptyActiveEnvironmentFailsWithoutCallingReporter()
    {
        var reporter = A.Fake<IConfigAuditReporter>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environmentProvider.Environment).Returns(" ");
        using var output = new StringWriter();
        var runner = new ConfigDiagnosticsCommandRunner(
            reporter,
            new ConfigAuditTextRenderer(),
            environmentProvider);

        var result = runner.Run(output);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("active AppSurface environment", result.Failure!.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception type:", result.Failure.ToDisplayString(), StringComparison.Ordinal);
        A.CallTo(() => reporter.GetReport(A<string>._)).MustNotHaveHappened();
    }

    private static ConfigAuditReport CreateReport(string environment, params ConfigAuditEntry[] entries) =>
        CreateReport(environment, mode: null, entries: entries);

    private static ConfigAuditReport CreateReport(
        string environment,
        ConfigAuditReportMode? mode,
        params ConfigAuditEntry[] entries) =>
        new()
        {
            Environment = environment,
            GeneratedAt = DateTimeOffset.UtcNow,
            Mode = mode,
            Providers =
            [
                new ConfigAuditProvider
                {
                    Name = "EnvironmentConfigProvider",
                    Priority = -1,
                    Precedence = 0,
                    IsOverride = true
                }
            ],
            Entries = entries,
            Redaction = new ConfigAuditRedaction
            {
                Enabled = true,
                MatchedFragments = ["secret", "token", "apikey", "key"],
                Placeholder = "[redacted]"
            }
        };

    private sealed class DiagnosticsProviderException(string message) : Exception(message)
    {
    }
}
