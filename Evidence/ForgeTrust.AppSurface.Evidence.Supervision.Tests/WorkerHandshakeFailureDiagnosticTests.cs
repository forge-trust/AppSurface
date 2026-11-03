#if EVIDENCE_PRIVATE_C3_SDK || EVIDENCE_QUALIFICATION_DATA_TESTS
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Closed private diagnostic data controls; these tests do not run a worker handshake.</summary>
public sealed class WorkerHandshakeFailureDiagnosticTests
{
    [Theory]
    [InlineData((int)WorkerHandshakeFailureOutcome.InitialAdmissionDeadline, "InitialAdmissionDeadline")]
    [InlineData((int)WorkerHandshakeFailureOutcome.PreparationOrAdmissionDeadline, "PreparationOrAdmissionDeadline")]
    [InlineData((int)WorkerHandshakeFailureOutcome.AdmissionRejected, "AdmissionRejected")]
    [InlineData((int)WorkerHandshakeFailureOutcome.MalformedResponse, "MalformedResponse")]
    [InlineData((int)WorkerHandshakeFailureOutcome.TransportFailure, "TransportFailure")]
    public void WritesOnlyTheClosedFailureProjection(int outcomeValue, string expectedOutcome)
    {
        using var writer = new StringWriter();

        WorkerHandshakeFailureDiagnostic.TryWrite(writer, (WorkerHandshakeFailureOutcome)outcomeValue,
            TimeProvider.System.GetTimestamp());

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.Equal(new[] { "elapsed_ms", "native_acceptance", "outcome", "schema" },
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("issue779-c3sdk-worker-handshake-failure-v1", root.GetProperty("schema").GetString());
        Assert.Equal(expectedOutcome, root.GetProperty("outcome").GetString());
        Assert.InRange(root.GetProperty("elapsed_ms").GetInt64(), 0, 3_600_000);
        Assert.False(root.GetProperty("native_acceptance").GetBoolean());
        Assert.DoesNotContain("canary", writer.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidOutcomeAndNullWriterProduceNoOutput()
    {
        using var writer = new StringWriter();

        WorkerHandshakeFailureDiagnostic.TryWrite(writer, (WorkerHandshakeFailureOutcome)int.MaxValue,
            TimeProvider.System.GetTimestamp());
        WorkerHandshakeFailureDiagnostic.TryWrite(null, WorkerHandshakeFailureOutcome.AdmissionRejected,
            TimeProvider.System.GetTimestamp());
        WorkerHandshakeFailureDiagnostic.TryWrite(writer, WorkerHandshakeFailureOutcome.AdmissionRejected,
            long.MaxValue);

        Assert.Equal(string.Empty, writer.ToString());
    }

#if EVIDENCE_QUALIFICATION_DATA_TESTS && !EVIDENCE_PRIVATE_C3_SDK
    [Fact]
    public void OrdinaryContractsAssemblyDoesNotContainThePrivateDiagnostic()
    {
        var contractsAssembly = typeof(EvidencePolicy).Assembly;

        Assert.Null(contractsAssembly.GetType(
            "ForgeTrust.AppSurface.Evidence.Contracts.WorkerHandshakeFailureDiagnostic", throwOnError: false));
        Assert.Null(contractsAssembly.GetType(
            "ForgeTrust.AppSurface.Evidence.Contracts.WorkerHandshakeFailureOutcome", throwOnError: false));
    }
#endif

    [Fact]
    public void DiagnosticWriterFailureCannotReplaceTheOriginalHandshakeException()
    {
        var original = new IOException("original handshake failure");
        var writer = new ThrowingWriter();

        Action handshake = () =>
        {
            try
            {
                throw original;
            }
            catch (IOException)
            {
                WorkerHandshakeFailureDiagnostic.TryWrite(writer,
                    WorkerHandshakeFailureOutcome.TransportFailure, TimeProvider.System.GetTimestamp());
                throw;
            }
        };
        var observed = Assert.Throws<IOException>(handshake);

        Assert.Same(original, observed);
        Assert.Equal(1, writer.WriteAttempts);
    }

    private sealed class ThrowingWriter : StringWriter
    {
        public int WriteAttempts { get; private set; }

        public override void WriteLine(string? value)
        {
            WriteAttempts++;
            throw new IOException("diagnostic output failure");
        }
    }
}
#endif
