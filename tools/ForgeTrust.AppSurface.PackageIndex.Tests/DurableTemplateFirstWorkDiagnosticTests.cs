using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class DurableTemplateFirstWorkDiagnosticTests
{
    private const string Secret = "private-first-work-credential";

    [Theory]
    [InlineData("postgresql-fixture-setup")]
    [InlineData("host-read-only-startup")]
    [InlineData("read-only-startup-catalog")]
    [InlineData("initial-readiness")]
    [InlineData("typed-work-acceptance")]
    [InlineData("authorization-before-wake-body")]
    [InlineData("authenticated-work-activation")]
    [InlineData("persisted-terminal-work")]
    [InlineData("healthy-readiness-transition")]
    [InlineData("sdk-export-flush")]
    public void Known_failure_phases_are_retained_with_leading_whitespace(string phase)
    {
        var result = DurableTemplateFirstWorkDiagnostic.Read("ordinary output\r\n \t" + Marker(phase: phase) + "\r\nmore output");

        Assert.Equal(new DurableTemplateFirstWorkDiagnostic(phase, "TimeoutException", "none"), result);
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("OperationCanceledException")]
    [InlineData("TaskCanceledException")]
    [InlineData("TimeoutException")]
    [InlineData("IOException")]
    [InlineData("UnauthorizedAccessException")]
    [InlineData("SocketException")]
    [InlineData("HttpRequestException")]
    [InlineData("PostgresException")]
    [InlineData("NpgsqlException")]
    [InlineData("EqualException")]
    [InlineData("TrueException")]
    [InlineData("FalseException")]
    [InlineData("IsTypeException")]
    [InlineData("NullException")]
    [InlineData("NotNullException")]
    [InlineData("SingleException")]
    [InlineData("ContainsException")]
    [InlineData("DoesNotContainException")]
    [InlineData("InRangeException")]
    [InlineData("EmptyException")]
    [InlineData("NotEmptyException")]
    [InlineData("MissingCheckpoint")]
    [InlineData("CleanupFailed")]
    public void Known_error_categories_are_retained_exactly(string code)
    {
        var result = Assert.IsType<DurableTemplateFirstWorkDiagnostic>(DurableTemplateFirstWorkDiagnostic.Read(Marker(code: code)));

        Assert.Equal(code, result.Code);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("owned-cleanup-unfinished")]
    [InlineData("owned-cleanup-total-budget-exhausted")]
    [InlineData("owned-cleanup-unfinished,owned-cleanup-total-budget-exhausted")]
    public void Known_cleanup_observations_are_retained_exactly(string cleanup)
    {
        var result = Assert.IsType<DurableTemplateFirstWorkDiagnostic>(DurableTemplateFirstWorkDiagnostic.Read(Marker(cleanup: cleanup)));

        Assert.Equal(cleanup, result.Cleanup);
    }

    [Fact]
    public void Serialization_retains_only_closed_fields_and_discards_raw_output()
    {
        var output = Secret + " before\n" + Marker(code: Secret, cleanup: Secret,
            location: "/private/" + Secret, cause: "password=" + Secret, retry: Secret + "; success=true") + "\n" + Secret + " after";

        var result = Assert.IsType<DurableTemplateFirstWorkDiagnostic>(DurableTemplateFirstWorkDiagnostic.Read(output));
        var json = JsonSerializer.Serialize(result);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(new DurableTemplateFirstWorkDiagnostic("initial-readiness", "Other", "unknown"), result);
        Assert.Equal(3, document.RootElement.EnumerateObject().Count());
        Assert.Equal("initial-readiness", document.RootElement.GetProperty("Phase").GetString());
        Assert.Equal("Other", document.RootElement.GetProperty("Code").GetString());
        Assert.Equal("unknown", document.RootElement.GetProperty("Cleanup").GetString());
        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("success", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("timeoutException")]
    [InlineData("System.TimeoutException")]
    [InlineData("TimeoutException-with-private-data")]
    [InlineData("")]
    public void Unknown_error_categories_are_closed_to_other(string code)
    {
        var result = Assert.IsType<DurableTemplateFirstWorkDiagnostic>(DurableTemplateFirstWorkDiagnostic.Read(Marker(code: code)));

        Assert.Equal("Other", result.Code);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("none,owned-cleanup-unfinished")]
    [InlineData("owned-cleanup-unfinished-with-private-data")]
    [InlineData("")]
    public void Unknown_cleanup_observations_are_closed_to_unknown(string cleanup)
    {
        var result = Assert.IsType<DurableTemplateFirstWorkDiagnostic>(DurableTemplateFirstWorkDiagnostic.Read(Marker(cleanup: cleanup)));

        Assert.Equal("unknown", result.Cleanup);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("ordinary failed assertion without a marker")]
    [InlineData("prefix [FirstDurableWork] failure phase=initial-readiness code=TimeoutException at=x cause=x; cleanup=none; retry=x")]
    [InlineData("[FirstDurableWork] success phase=initial-readiness code=TimeoutException at=x cause=x; cleanup=none; retry=x")]
    public void Absent_or_nonfailure_markers_are_withheld(string? output) =>
        Assert.Null(DurableTemplateFirstWorkDiagnostic.Read(output));

    [Theory]
    [InlineData("unknown-phase")]
    [InlineData("Initial-readiness")]
    [InlineData("initial-readiness-private-data")]
    [InlineData("")]
    public void Unknown_phases_withhold_the_entire_diagnostic(string phase) =>
        Assert.Null(DurableTemplateFirstWorkDiagnostic.Read(Marker(phase: phase)));

    [Theory]
    [InlineData(" code=")]
    [InlineData(" at=")]
    [InlineData(" cause=")]
    [InlineData("; cleanup=")]
    [InlineData("; retry=")]
    public void Missing_required_field_delimiters_withhold_the_entire_diagnostic(string delimiter) =>
        Assert.Null(DurableTemplateFirstWorkDiagnostic.Read(Marker().Replace(delimiter, " broken=", StringComparison.Ordinal)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Multiple_markers_are_ambiguous_even_when_identical(bool different)
    {
        var output = Marker() + "\n\t" + Marker(phase: different ? "sdk-export-flush" : "initial-readiness");

        Assert.Null(DurableTemplateFirstWorkDiagnostic.Read(output));
    }

    [Fact]
    public void Whole_output_character_limit_is_inclusive()
    {
        var marker = Marker();
        var atLimit = marker + "\n" + new string('x', 1024 * 1024 - marker.Length - 1);

        Assert.NotNull(DurableTemplateFirstWorkDiagnostic.Read(atLimit));
        Assert.Null(DurableTemplateFirstWorkDiagnostic.Read(atLimit + "x"));
    }

    [Fact]
    public void Marker_line_character_limit_is_inclusive()
    {
        var marker = Marker();
        var atLimit = Marker(cause: Secret + new string('x', 4096 - marker.Length));

        Assert.Equal(4096, atLimit.Length);
        Assert.NotNull(DurableTemplateFirstWorkDiagnostic.Read(atLimit));
        Assert.Null(DurableTemplateFirstWorkDiagnostic.Read(atLimit + "x"));
    }

    [Fact]
    public void Leading_whitespace_cannot_bypass_the_marker_line_limit()
    {
        var marker = Marker();
        var atLimit = new string(' ', 4096 - marker.Length) + marker;

        Assert.NotNull(DurableTemplateFirstWorkDiagnostic.Read(atLimit));
        Assert.Null(DurableTemplateFirstWorkDiagnostic.Read(" " + atLimit));
    }

    private static string Marker(string phase = "initial-readiness", string code = "TimeoutException", string cleanup = "none",
        string location = Secret, string cause = Secret, string retry = Secret) =>
        $"[FirstDurableWork] failure phase={phase} code={code} at={location} cause={cause}; cleanup={cleanup}; retry={retry}";
}
