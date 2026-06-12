using ProductReadinessLab.Verifier;

namespace ProductReadinessLab.Tests;

public sealed class ProductReadinessAppHostVerifierTests
{
    [Fact]
    public void Verify_WithAppHostBackedRows_Succeeds()
    {
        var result = ProductReadinessAppHostVerifier.Verify(Report(
            Row("startup-routing", "proven-locally"),
            Row("config-summary", "proven-locally"),
            Row("proof-auth", "unsafe-to-copy"),
            Row("workflow", "proven-locally"),
            Row("postgres-product-state", "proven-locally"),
            Row("in-process-host-shape", "proven-locally"),
            Row("durabletask-backend-boundary", "host-owned"),
            Row("deployment", "deferred"),
            Row("secrets", "host-owned")));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Verify_WhenPostgresProductStateBlocked_Fails()
    {
        var result = ProductReadinessAppHostVerifier.Verify(Report(
            Row("startup-routing", "proven-locally"),
            Row("config-summary", "proven-locally"),
            Row("proof-auth", "unsafe-to-copy"),
            Row("workflow", "proven-locally"),
            Row("postgres-product-state", "blocked", "Postgres product-state proof is not complete."),
            Row("in-process-host-shape", "proven-locally"),
            Row("durabletask-backend-boundary", "host-owned"),
            Row("deployment", "deferred"),
            Row("secrets", "host-owned")));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure => failure.Contains("postgres-product-state expected proven-locally", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("postgres-product-state is blocked", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_WhenDurableTaskBackendMarkedProven_Fails()
    {
        var result = ProductReadinessAppHostVerifier.Verify(Report(
            Row("startup-routing", "proven-locally"),
            Row("config-summary", "proven-locally"),
            Row("proof-auth", "unsafe-to-copy"),
            Row("workflow", "proven-locally"),
            Row("postgres-product-state", "proven-locally"),
            Row("in-process-host-shape", "proven-locally"),
            Row("durabletask-backend-boundary", "proven-locally"),
            Row("deployment", "deferred"),
            Row("secrets", "host-owned")));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure => failure.Contains("durabletask-backend-boundary expected host-owned", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_WhenRowsMissing_FailsWithoutThrowing()
    {
        var result = ProductReadinessAppHostVerifier.Verify(Report());

        Assert.False(result.Succeeded);
        Assert.Contains("readiness report has no rows.", result.Failures);
    }

    [Fact]
    public void Verify_WhenRowsAreDuplicated_FailsWithoutThrowing()
    {
        var result = ProductReadinessAppHostVerifier.Verify(Report(
            Row("startup-routing", "proven-locally"),
            Row("startup-routing", "proven-locally")));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure => failure.Contains("startup-routing row is duplicated", StringComparison.Ordinal));
    }

    [Fact]
    public void Options_Parse_UsesEnvironmentTargetAndBoundedTimeout()
    {
        var options = ProductReadinessVerifierOptions.Parse(
            ["--timeout-seconds", "30"],
            name => name == "PRODUCT_READINESS_TARGET_URL" ? "http://127.0.0.1:5061" : null);

        Assert.True(options.IsValid);
        Assert.Equal(new Uri("http://127.0.0.1:5061"), options.TargetUri);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5061", "http://127.0.0.1:5061/readiness")]
    [InlineData("http://127.0.0.1:5061/lab", "http://127.0.0.1:5061/lab/readiness")]
    [InlineData("http://127.0.0.1:5061/lab/", "http://127.0.0.1:5061/lab/readiness")]
    public void BuildReadinessUri_PreservesBasePath(string target, string expected)
    {
        var readinessUri = ProductReadinessAppHostVerifier.BuildReadinessUri(new Uri(target));

        Assert.Equal(new Uri(expected), readinessUri);
    }

    [Fact]
    public void Options_Parse_RejectsMissingTarget()
    {
        var options = ProductReadinessVerifierOptions.Parse([], _ => null);

        Assert.False(options.IsValid);
        Assert.Contains("PRODUCT_READINESS_TARGET_URL", options.Error, StringComparison.Ordinal);
    }

    private static ProductReadinessProbeDocument Report(params ProductReadinessProbeRow[] rows) => new(rows);

    private static ProductReadinessProbeRow Row(string area, string status, string problem = "None.") =>
        new(area, status, problem);
}
