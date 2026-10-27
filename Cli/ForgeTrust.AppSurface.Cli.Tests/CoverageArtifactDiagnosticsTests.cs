using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies stable storage diagnostics and safe rendering.</summary>
public sealed class CoverageArtifactDiagnosticsTests
{
    [Fact]
    public void Failure_UsesTypedBusyGuidanceAndDoesNotCopyExceptionMessage()
    {
        var diagnostic = CoverageArtifactDiagnostic.Failure(
            "ASCOV202", "maintain", new InvalidOperationException("/Users/example/private-token=secret"));

        Assert.Equal("ASCOV202", diagnostic.Code);
        Assert.Equal("maintain", diagnostic.Operation);
        Assert.Equal("none", diagnostic.SideEffects);
        Assert.Equal(1, diagnostic.MaxRetries);
        Assert.Contains("retry once", diagnostic.Recovery, StringComparison.Ordinal);
        Assert.Contains("guides/coverage-storage.md#ascov202", diagnostic.Documentation, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", diagnostic.Cause, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", diagnostic.Recovery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_ReportsObservedLimitsWithoutExceptionText()
    {
        var diagnostic = CoverageArtifactDiagnostic.Failure(
            "ASCOV201", "status", new IOException("password=private")) with
        { ObservedBytes = 2048, AllowedBytes = 1024 };
        using var writer = new StringWriter();

        await diagnostic.WriteAsync(writer);

        var output = writer.ToString();
        Assert.Contains("observedBytes=2048", output, StringComparison.Ordinal);
        Assert.Contains("allowedBytes=1024", output, StringComparison.Ordinal);
        Assert.DoesNotContain("password=private", output, StringComparison.Ordinal);
    }
}

/// <summary>Exercises safe diagnostics even when the optional diagnostic destination fails.</summary>
public sealed class CoverageArtifactDiagnosticBoundaryTests
{
    [Theory]
    [InlineData("ASCOV200", "history-not-saved")]
    [InlineData("ASCOV201", "store-degraded")]
    [InlineData("ASCOV202", "operation-busy")]
    [InlineData("ASCOV203", "inspect-incomplete")]
    [InlineData("ASCOV204", "maintain-partial")]
    [InlineData("ASCOV205", "restore-failed")]
    [InlineData("ASCOV206", "pin-state")]
    [InlineData("ASCOV207", "metadata-capacity")]
    [InlineData("ASCOV208", "invalid-storage-input")]
    [InlineData("ASCOV209", "implicit-collector-risk")]
    [InlineData("ASCOV210", "storage-growth")]
    [InlineData("ASCOV211", "settings-inspection-unavailable")]
    public void EveryPublishedDiagnosticLinksItsDocumentedRecovery(string code, string anchor)
    {
        var diagnostic = new CoverageArtifactDiagnostic(code, "inspect", "safe", "safe");
        Assert.EndsWith("#" + code.ToLowerInvariant() + "-" + anchor, diagnostic.Documentation, StringComparison.Ordinal);
        Assert.Equal(["APPSURFACE_ARTIFACT_HOME"], diagnostic.EnvironmentNames);
        Assert.Equal(1, diagnostic.PolicySchemaVersion);
        Assert.NotEmpty(diagnostic.Version);
    }

    [Fact]
    public void PathLabelsUseRelativeAndHomePrefixesWithoutControls()
    {
        using var temp = TestDirectory.Create();
        var observed = Path.Join(temp.Path, "results", "line\nbreak.xml");
        Assert.Equal(Path.Join("results", "line\\u000abreak.xml"), CoverageArtifactDisplay.PathLabel(observed, temp.Path));
        Assert.EndsWith("...", CoverageArtifactDisplay.Bound(new string('x', 257)), StringComparison.Ordinal);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal("<home>", CoverageArtifactDisplay.PathLabel(home));
        Assert.Equal("<home>" + Path.DirectorySeparatorChar + "results", CoverageArtifactDisplay.PathLabel(Path.Join(home, "results")));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("disposed")]
    [InlineData("permissions")]
    public async Task UnavailableDiagnosticWriterDoesNotReplaceThePrimaryOutcome(string failure)
    {
        var diagnostic = CoverageArtifactDiagnostic.Failure("ASCOV200", "snapshot", new TimeoutException("secret-token"));
        await diagnostic.WriteAsync(new FailingWriter(failure));
        Assert.Contains("deadline", diagnostic.Cause, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedCapabilityReportsAUsableOverrideWithoutRawExceptionDetail()
    {
        var diagnostic = CoverageArtifactDiagnostic.Failure("ASCOV201", "storage", new PlatformNotSupportedException("credential=secret"));
        Assert.Contains("APPSURFACE_ARTIFACT_HOME", diagnostic.Recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", diagnostic.Cause + diagnostic.Recovery, StringComparison.Ordinal);
        Assert.Contains("supported", diagnostic.Cause, StringComparison.Ordinal);
    }

    private sealed class FailingWriter(string failure) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override Task WriteLineAsync(string? value) => throw failure switch
        {
            "io" => new IOException("private"),
            "disposed" => new ObjectDisposedException("private"),
            _ => new UnauthorizedAccessException("private")
        };
    }
}
