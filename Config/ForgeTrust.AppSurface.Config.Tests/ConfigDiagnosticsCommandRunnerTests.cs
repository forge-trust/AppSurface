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
        var output = new StringWriter();
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
        A.CallTo(() => reporter.GetReport("Staging")).MustHaveHappenedOnceExactly();
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
        var output = new StringWriter();
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
        var output = new StringWriter();
        var runner = new ConfigDiagnosticsCommandRunner(
            reporter,
            new ConfigAuditTextRenderer(),
            environmentProvider);

        var result = runner.Run(output);

        Assert.True(result.Succeeded);
        Assert.Contains("Payment.ApiKey = [redacted]", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_ReporterFailureReturnsSanitizedFailure()
    {
        var reporter = A.Fake<IConfigAuditReporter>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => reporter.GetReport("Production"))
            .Throws(new InvalidOperationException("provider failed with super-secret"));
        var output = new StringWriter();
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
        Assert.Contains("InvalidOperationException", display, StringComparison.Ordinal);
        Assert.DoesNotContain("provider failed", display, StringComparison.Ordinal);
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
        var output = new StringWriter();
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
        new()
        {
            Environment = environment,
            GeneratedAt = DateTimeOffset.UtcNow,
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
}
