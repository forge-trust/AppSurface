#if EVIDENCE_PRIVATE_N04
using System.Text;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

public sealed class LinuxN04OwnerActivationDiagnosticTests
{
    [Fact]
    public void FormatLineContainsOnlyTheFixedSchemaStageAndNonacceptanceClaim()
    {
        var stages = new (LinuxN04OwnerActivationDiagnostic.Stage Stage, string Name)[]
        {
            (LinuxN04OwnerActivationDiagnostic.Stage.InputRecheck, "input-recheck"),
            (LinuxN04OwnerActivationDiagnostic.Stage.BackendRead, "backend-read"),
            (LinuxN04OwnerActivationDiagnostic.Stage.UnitParse, "unit-parse"),
            (LinuxN04OwnerActivationDiagnostic.Stage.FactsRequire, "facts-require"),
            (LinuxN04OwnerActivationDiagnostic.Stage.IdentityCapture, "identity-capture"),
            (LinuxN04OwnerActivationDiagnostic.Stage.GuardConsume, "guard-consume"),
            (LinuxN04OwnerActivationDiagnostic.Stage.ActiveRecheck, "active-recheck"),
        };

        foreach (var (stage, name) in stages)
        {
            var line = LinuxN04OwnerActivationDiagnostic.FormatLine(stage);
            Assert.Equal($"{{\"schema\":\"issue779-n04-owner-activation-diagnostic-v1\",\"stage\":\"{name}\",\"native_acceptance\":false}}\n", line);
            Assert.DoesNotContain("canary", line, StringComparison.Ordinal);
            Assert.DoesNotContain("exception", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InvalidStageIsRejectedByFormatterAndProducesNoBestEffortOutput()
    {
        var invalid = (LinuxN04OwnerActivationDiagnostic.Stage)int.MaxValue;
        Assert.Throws<ArgumentOutOfRangeException>(() => LinuxN04OwnerActivationDiagnostic.FormatLine(invalid));

        using var output = new StringWriter();
        LinuxN04OwnerActivationDiagnostic.WriteBestEffort(output, invalid);

        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void DiagnosticWriteFailureDoesNotReplaceTheOriginalFailure()
    {
        var primary = new InvalidOperationException("primary canary failure");

        Action rethrowPrimary = () =>
        {
            try { throw primary; }
            catch
            {
                LinuxN04OwnerActivationDiagnostic.WriteBestEffort(new FailingWriteWriter(),
                    LinuxN04OwnerActivationDiagnostic.Stage.ActiveRecheck);
                throw;
            }
        };
        var observed = Assert.Throws<InvalidOperationException>(rethrowPrimary);

        Assert.Same(primary, observed);
        Assert.Equal("primary canary failure", observed.Message);
    }

    private sealed class FailingWriteWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Flush() => throw new IOException("write canary");
        public override void Write(string? value) => throw new IOException("write canary");
    }
}
#endif
