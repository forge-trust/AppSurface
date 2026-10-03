using System.Text;
using ForgeTrust.AppSurface.Durable.PostgreSql;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Exercises the intentionally exposed safe result and connection-budget contracts.</summary>
public sealed class DurablePreflightResultTests
{
    private static DurablePreflightRequest Request() => new(DurableRoleManifest.Parse(Encoding.UTF8.GetBytes(
        """{"version":1,"pairs":[{"dispatcher":"first_dispatcher","runtime":"first_runtime","dispatcher_profile":"full"},{"dispatcher":"second_dispatcher","runtime":"second runtime","dispatcher_profile":"work_only"}]}""")), "owner");

    [Theory]
    [InlineData(null)]
    [InlineData(42)]
    [InlineData("secret server output")]
    public void Unexpected_catalog_shapes_fail_closed(object? value) =>
        Assert.Equal(["catalog_result"], DurableSchemaPreflightVerifier.MapCatalogResult(value, Request()));

    [Fact]
    public void Unknown_duplicate_null_and_out_of_range_categories_cannot_pass()
    {
        foreach (var values in new string[][] { ["server-secret"], ["forced_rls", "forced_rls"], [null!], ["runtime_role:0"], ["runtime_role:3"], new string[27] })
        {
            Assert.Equal(["catalog_result"], DurableSchemaPreflightVerifier.MapCatalogResult(values, Request()));
        }
        Assert.Throws<ArgumentNullException>(() => DurableSchemaPreflightVerifier.MapCatalogResult(Array.Empty<string>(), null!));
    }

    [Fact]
    public void Check_order_and_pair_identity_are_deterministic_and_copied()
    {
        var raw = new[] { "runtime_role:2", "runtime_table_privileges:1", "function_acl", "forced_rls" };
        var result = DurableSchemaPreflightVerifier.MapCatalogResult(raw, Request());
        Assert.Equal(["forced_rls", "function_acl", "runtime_table_privileges[pair=1,role=\"first_runtime\"]", "runtime_role[pair=2,role=\"second runtime\"]"], result);
        raw[0] = "server-secret";
        Assert.DoesNotContain("server-secret", result);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result)[0] = "mutated");
        Assert.Empty(DurableSchemaPreflightVerifier.MapCatalogResult(Array.Empty<string>(), Request()));
    }

    [Fact]
    public void Result_copies_status_and_failures_and_preserves_nullable_epoch()
    {
        var pending = new[] { 11 };
        var failures = new[] { "forced_rls" };
        var storeId = Guid.NewGuid();
        var result = new DurablePreflightResult(new(DurableRuntimeSchemaCompatibility.UpgradeRequired, 10, 11, pending),
            storeId, null, "owner-diagnostic", "owner", null, failures);
        pending[0] = 12;
        failures[0] = "mutated";
        Assert.Equal([11], result.Status.PendingVersions);
        Assert.Equal(["forced_rls"], result.FailedChecks);
        Assert.Equal(storeId, result.StoreId);
        Assert.Null(result.ActiveRuntimeEpoch);
        Assert.Null(result.PairIndex);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)result.Status.PendingVersions)[0] = 13);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.FailedChecks)[0] = "mutated");
        Assert.Throws<ArgumentNullException>(() => new DurablePreflightResult(null!, storeId, null, "runtime", "role", 1, []));
        Assert.Throws<ArgumentNullException>(() => new DurablePreflightResult(result.Status, storeId, null, "runtime", "role", 1, null!));
    }

    [Fact]
    public void Incomplete_success_evidence_cannot_cross_the_service_boundary()
    {
        var request = Request();
        var status = new DurableSchemaStatusView(DurableRuntimeSchemaCompatibility.Compatible, 11, 11, []);
        var id = Guid.NewGuid();
        foreach (var invalid in new DurablePreflightResult?[]
        {
            null,
            new(status, Guid.Empty, null, "runtime", "first_runtime", 1, []),
            new(status, id, null, "unresolved", null, null, []),
            new(status, id, null, "unresolved", null, null, ["uncontrolled server secret"]),
            new(status, id, null, "unresolved", null, null, ["caller_role", "caller_role"]),
            new(status, id, null, "unresolved", null, null, [null!]),
            new(status, id, null, "unresolved", null, null, new string[30]),
            new(status, id, null, "runtime", "second runtime", 1, []),
            new(status, id, null, "runtime", "first_runtime", 0, []),
            new(status, id, null, "runtime", "first_runtime", 3, []),
            new(status, id, null, "owner-diagnostic", "owner", 1, []),
            new(status, id, null, "owner-diagnostic", "wrong", null, []),
            new(status with { PendingVersions = new[] { 11 } }, id, null, "runtime", "first_runtime", 1, []),
            new(status with { InstalledVersion = 10 }, id, null, "runtime", "first_runtime", 1, []),
            new(status with { RequiredVersion = 0 }, id, null, "runtime", "first_runtime", 1, []),
            new(status with { InstalledVersion = -1 }, id, null, "runtime", "first_runtime", 1, []),
            new(status with { RequiredVersion = -1 }, id, null, "runtime", "first_runtime", 1, []),
            new(status with { Compatibility = (DurableRuntimeSchemaCompatibility)99 }, id, null, "runtime", "first_runtime", 1, [])
        })
        {
            var exception = Assert.Throws<DurablePreflightException>(() => DurableSchemaPreflightVerifier.ValidateEvidenceResult(invalid, request));
            Assert.Equal("catalog_result", exception.Category);
        }
        DurableSchemaPreflightVerifier.ValidateEvidenceResult(new(status, id, null, "runtime", "first_runtime", 1, []), request);
        DurableSchemaPreflightVerifier.ValidateEvidenceResult(new(status, id, Guid.NewGuid(), "owner-diagnostic", "owner", null, []), request);
        DurableSchemaPreflightVerifier.ValidateEvidenceResult(new(status, id, null, "unresolved", null, null, ["caller_identity"]), request);
        DurableSchemaPreflightVerifier.ValidateEvidenceResult(new(status with { Compatibility = DurableRuntimeSchemaCompatibility.Missing }, Guid.Empty, null, "unresolved", null, null, []), request);
        Assert.Throws<ArgumentNullException>(() => DurableSchemaPreflightVerifier.ValidateEvidenceResult(null, null!));
    }

    [Theory]
    [InlineData("connection_input")]
    [InlineData("database_operation")]
    [InlineData("timeout")]
    [InlineData("cleanup")]
    [InlineData("catalog_result")]
    [InlineData("server-secret-marker")]
    public void Diagnostic_categories_are_bounded_and_include_safe_recovery(string category)
    {
        var message = DurableSchemaDiagnostics.PreflightOperationFailure(category);
        Assert.StartsWith("Problem: durable preflight ", message, StringComparison.Ordinal);
        Assert.Contains("Cause:", message, StringComparison.Ordinal);
        Assert.Contains("Fix:", message, StringComparison.Ordinal);
        Assert.Contains("Docs: https://github.com/forge-trust/AppSurface/blob/main/Durable/heartbeat-retention-operations.md#deploy-schema-11", message, StringComparison.Ordinal);
        Assert.DoesNotContain("server-secret-marker", message, StringComparison.Ordinal);
        Assert.Contains(category == "server-secret-marker" ? "catalog_result" : category, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 28)]
    [InlineData(5, 5)]
    [InlineData(30, 28)]
    public void Session_settings_preserve_shorter_timeouts_and_override_unsafe_lifetimes(int timeout, int expected)
    {
        var settings = DurableSchemaPreflightVerifier.CreateSessionSettings(
            $"Host=localhost;Username=runtime;Pooling=true;Enlist=true;Multiplexing=true;Maximum Pool Size=1;Timeout={timeout};Command Timeout={timeout};Cancellation Timeout=9000;Keepalive=1");
        Assert.False(settings.Pooling);
        Assert.False(settings.Enlist);
        Assert.False(settings.Multiplexing);
        Assert.Equal(1, settings.MaxPoolSize);
        Assert.Equal(expected, settings.Timeout);
        Assert.Equal(expected, settings.CommandTimeout);
        Assert.Equal(-1, settings.CancellationTimeout);
        Assert.Equal(0, settings.KeepAlive);
    }
}
