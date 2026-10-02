using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceLinuxControlProtocolTests
{
    [Fact]
    public async Task ConnectAsync_rejects_invalid_control_paths_without_disclosing_them()
    {
        var paths = new[] { "relative-control-protocol-secret.sock", "/" + new string('a', 101) };
        foreach (var socketPath in paths)
        {
            var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(
                () => EvidenceLinuxWorkerSupervisor.ConnectAsync(socketPath, CancellationToken.None));

            Assert.Equal("ASEVD402", exception.Code);
            Assert.DoesNotContain(socketPath, exception.Message);
            Assert.Contains(OperatingSystem.IsLinux()
                ? "control channel is invalid"
                : "unavailable on this platform", exception.Message);
        }
    }
}
