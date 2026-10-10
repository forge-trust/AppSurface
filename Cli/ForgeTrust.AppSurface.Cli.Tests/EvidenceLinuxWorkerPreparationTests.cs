using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Closed preparation data and original-budget arithmetic, without a peer, supervisor or admission.</summary>
public sealed class EvidenceLinuxWorkerPreparationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(12345)]
    [InlineData(3600000)]
    public void PreparationRetainsTheExactBoundedRootAllowance(long milliseconds)
    {
        using var json = JsonDocument.Parse($"{{\"ok\":true,\"phase\":\"pre-admission\",\"remaining_job_ms\":{milliseconds}}}");
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds),
            EvidenceLinuxWorkerSupervisor.ParseWorkerPreparationAllowance(json.RootElement));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"ok\":false,\"phase\":\"pre-admission\",\"remaining_job_ms\":10}")]
    [InlineData("{\"ok\":true,\"phase\":\"canary-phase\",\"remaining_job_ms\":10}")]
    [InlineData("{\"ok\":true,\"phase\":\"pre-admission\",\"remaining_job_ms\":0}")]
    [InlineData("{\"ok\":true,\"phase\":\"pre-admission\",\"remaining_job_ms\":3600001}")]
    [InlineData("{\"ok\":true,\"phase\":\"pre-admission\",\"remaining_job_ms\":1.5}")]
    [InlineData("{\"ok\":true,\"phase\":\"pre-admission\",\"remaining_job_ms\":\"canary-value\"}")]
    [InlineData("{\"ok\":true,\"phase\":\"pre-admission\"}")]
    [InlineData("{\"ok\":true,\"phase\":\"pre-admission\",\"remaining_job_ms\":10,\"descriptor\":{}}")]
    [InlineData("{\"ok\":true,\"phase\":\"pre-admission\",\"remaining_job_ms\":10,\"PHASE\":\"canary\"}")]
    [InlineData("{\"ok\":true,\"phase\":\"pre-admission\",\"remaining_job_ms\":10,\"phase\":\"pre-admission\"}")]
    public void MalformedPreparationNeverBecomesAnAllowance(string value)
    {
        using var json = JsonDocument.Parse(value);
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            EvidenceLinuxWorkerSupervisor.ParseWorkerPreparationAllowance(json.RootElement));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.Message);
    }

    [Fact]
    public void AdmissionStartIsOnlyAClosedTransition()
    {
        using var json = JsonDocument.Parse("{\"ok\":true,\"phase\":\"admission-start\"}");
        EvidenceLinuxWorkerSupervisor.ValidateWorkerAdmissionStart(json.RootElement);
    }

    [Theory]
    [InlineData("{\"ok\":false,\"phase\":\"admission-start\"}")]
    [InlineData("{\"ok\":true,\"phase\":\"pre-admission\"}")]
    [InlineData("{\"ok\":true,\"phase\":null}")]
    [InlineData("{\"ok\":true,\"phase\":\"admission-start\",\"remaining_job_ms\":10}")]
    [InlineData("{\"ok\":true,\"phase\":\"admission-start\",\"PHASE\":\"canary\"}")]
    public void InvalidTransitionHasNoDescriptorOrAuthority(string value)
    {
        using var json = JsonDocument.Parse(value);
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            EvidenceLinuxWorkerSupervisor.ValidateWorkerAdmissionStart(json.RootElement));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.Message);
    }

    [Theory]
    [InlineData(100, 0, 100)]
    [InlineData(100, 99, 1)]
    [InlineData(3600000, 3599999, 1)]
    public void PreparationSubtractsElapsedTimeInsteadOfResettingTheJob(long allowance, long elapsed, long expected)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expected), EvidenceLinuxWorkerSupervisor.WorkerPreparationRemaining(
            TimeSpan.FromMilliseconds(allowance), TimeSpan.FromMilliseconds(elapsed)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, -1)]
    [InlineData(10, 10)]
    [InlineData(10, 11)]
    [InlineData(3600001, 1)]
    public void ExpiryOrInvalidClockDataCannotExtendTheJob(long allowance, long elapsed)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() => EvidenceLinuxWorkerSupervisor.WorkerPreparationRemaining(
            TimeSpan.FromMilliseconds(allowance), TimeSpan.FromMilliseconds(elapsed)));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
    }
    [Fact]
    public void FinalValidationCannotReturnAfterTheOriginalAllowanceExpires()
    {
        EvidenceLinuxWorkerSupervisor.ValidateWorkerPreparationCompletion(default,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(99));
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            EvidenceLinuxWorkerSupervisor.ValidateWorkerPreparationCompletion(default,
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void FinalValidationPreservesTheActualCanceledAdmissionToken()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var error = Assert.ThrowsAny<OperationCanceledException>(() =>
            EvidenceLinuxWorkerSupervisor.ValidateWorkerPreparationCompletion(cancellation.Token,
                TimeSpan.FromSeconds(30), TimeSpan.Zero));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

}
