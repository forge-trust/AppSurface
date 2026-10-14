using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Cli;
using Xunit;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Pure first-fault data controls; no allocator, lifecycle, native owner or fatal path executes.</summary>
public sealed class EvidenceN07PrecleanupFaultTests
{
    [Fact]
    public void ActualClassificationDropsCanaryAndDoesNotInventCleanupOrTerminal()
    {
        var data = EvidenceN07PrecleanupFault.Encode(EvidenceLinuxArtifactAllocationOperation.CheckParentIdentity,
            new IOException("private-precleanup-canary"));
        using var json = JsonDocument.Parse(data);
        Assert.Equal("CheckParentIdentity", json.RootElement.GetProperty("operation").GetString());
        Assert.Equal("Io", json.RootElement.GetProperty("error_family").GetString());
        Assert.False(json.RootElement.GetProperty("terminal_observed").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("cleanup_started", out _));
        Assert.False(json.RootElement.GetProperty("native_authority").GetBoolean());
        Assert.DoesNotContain("private-precleanup-canary", Encoding.UTF8.GetString(data));
        Assert.InRange(data.Length, 1, EvidenceN07PrecleanupFault.MaximumJsonBytes);
    }

    [Fact]
    public void LaterCleanupErrorCannotReplaceFirstCopiedAllocationFault()
    {
        var latch = new EvidenceN07PrecleanupFault();
        latch.Capture(EvidenceLinuxArtifactAllocationOperation.CheckParentIdentity, new IOException("first"));
        var first = latch.Bytes!;
        latch.Capture(EvidenceLinuxArtifactAllocationOperation.None, new InvalidOperationException("later"));
        Assert.Equal(first, latch.Bytes);
        Array.Fill(first, (byte)0);
        Assert.NotEqual(first, latch.Bytes);
    }

    [Fact]
    public void UnknownOperationIsClosedAndNoExceptionReferenceSurvives()
    {
        using var json = JsonDocument.Parse(EvidenceN07PrecleanupFault.Encode(
            (EvidenceLinuxArtifactAllocationOperation)int.MaxValue, new Exception("unknown-canary")));
        Assert.Equal("None", json.RootElement.GetProperty("operation").GetString());
        Assert.Equal("Unknown", json.RootElement.GetProperty("error_family").GetString());
    }

    [Fact]
    public void ActualParentIdentityIoEncodingMatchesClosedObserverFrame()
    {
        var error = new IOException("private-parent-identity-canary");
        var diagnostic = EvidenceProtectedCliExecution.CreateAllocationFailureDiagnostic(
            EvidenceAllocationPhase.Allocation, EvidenceLinuxArtifactAllocationOperation.CheckParentIdentity,
            EvidenceWorkerStageOutcome.Failed, EvidenceWorkerTerminalCode.None, error);
        Assert.Equal(EvidenceAllocationErrorClass.Io, diagnostic.ErrorClass);
        var bytes = EvidenceN07PrecleanupFault.Encode(EvidenceLinuxArtifactAllocationOperation.CheckParentIdentity, error);
        // Same closed wire bytes consumed by SupervisionN07OwnedCheckpointTests.Frame.
        // The existing test assemblies have separate friends; neither receives additional access.
        const string expected = "{\"schema\":\"issue779-n07-precleanup-allocation-fault-v1\",\"phase\":\"Allocation\",\"operation\":\"CheckParentIdentity\",\"error_family\":\"Io\",\"capture_point\":\"AllocateCatchBeforeCallbackRethrow\",\"terminal_observed\":false,\"observation_only\":true,\"native_authority\":false,\"native_acceptance\":false}";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), bytes);
        Assert.DoesNotContain("private-parent-identity-canary", Encoding.UTF8.GetString(bytes));
    }
}
