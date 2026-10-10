using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Xunit;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Detached cleanup diagnostic controls only; no account holder or native custody is fabricated.</summary>
public sealed class LinuxCancellationCleanupObservationTests
{
    [Fact]
    public void EncodeDetached_PreservesGenerationAndGroupWithoutAuthorityOrSuccessfulRun()
    {
        var generation = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var raw = LinuxEmptyObservationExecution.EncodeCancellationCleanupDetached(generation, 2003);
        using var parsed = JsonDocument.Parse(raw);
        var value = parsed.RootElement;
        Assert.Equal(9, value.EnumerateObject().Count());
        Assert.Equal("issue779-cancellation-root-cleanup-v1", value.GetProperty("schema").GetString());
        Assert.Equal(generation.ToString("N"), value.GetProperty("generation").GetString());
        Assert.Equal(2003u, value.GetProperty("results_gid").GetUInt32());
        foreach (var field in new[] { "accounts_closed", "root_custody_closed", "original_owners_closed", "observation_only" })
            Assert.True(value.GetProperty(field).GetBoolean());
        Assert.False(value.GetProperty("native_authority").GetBoolean());
        Assert.False(value.GetProperty("native_acceptance").GetBoolean());
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(raw), 1, 1023);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void EncodeDetached_RejectsReservedGroupData(uint gid)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxEmptyObservationExecution.EncodeCancellationCleanupDetached(Guid.NewGuid(), gid));
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void NegativeException_OptionalDiagnosticPreservesFirstFaultAndDefaultAbsence()
    {
        var latch = new EvidenceNativeObservationFailureLatch();
        latch.Capture(EvidenceNativeObservationPhase.ServerRun, new OperationCanceledException("private-canary"));
        var original = latch.Rejected();
        Assert.Null(original.CancellationCleanupJson);
        var withData = latch.Rejected(Guid.Parse("11111111-1111-1111-1111-111111111111"), 2003);
        Assert.Same(original.Failure, withData.Failure);
        Assert.Equal(original.Message, withData.Message);
        Assert.NotNull(withData.CancellationCleanupJson);
        Assert.DoesNotContain("private-canary", withData.CancellationCleanupJson, StringComparison.Ordinal);
        Assert.Null(withData.InnerException);
        Assert.Throws<ArgumentException>(() => latch.Rejected(Guid.NewGuid(), null));
        Assert.Throws<ArgumentException>(() => latch.Rejected(null, 2003));
        Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxEmptyObservationExecution.EncodeCancellationCleanupDetached(Guid.Empty, 2003));
    }
}
