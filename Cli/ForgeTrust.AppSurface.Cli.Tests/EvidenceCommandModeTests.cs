using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceCommandModeTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1", false)]
    [InlineData("production-canary", false)]
    [InlineData("trusted", true)]
    public void MissingUnknownOrConflictingModeRejectsWithoutEcho(string? mode, bool legacy)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() => EvidenceModeSelection.Select(mode, legacy));
        Assert.Equal("ASEVD401", error.Code);
        Assert.DoesNotContain("production-canary", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("trusted", false, EvidenceExecutionMode.Trusted)]
    [InlineData("TRUSTED", false, EvidenceExecutionMode.Trusted)]
    [InlineData("observation", false, EvidenceExecutionMode.Observation)]
    [InlineData("observation", true, EvidenceExecutionMode.Observation)]
    [InlineData(null, true, EvidenceExecutionMode.Observation)]
    public void ExplicitModeOrLegacyTrueHasOnlyItsDeclaredMeaning(string? mode, bool legacy, EvidenceExecutionMode expected) =>
        Assert.Equal(expected, EvidenceModeSelection.Select(mode, legacy));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task WorkerCommandRequiresAnAuthenticatedControlChannelBeforeConsoleOutput(string? controlChannel)
    {
        using var console = new CliFx.Infrastructure.FakeInMemoryConsole();
        var command = new EvidenceWorkerCommand { ControlChannel = controlChannel! };

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(() => command.ExecuteAsync(console).AsTask());

        Assert.StartsWith("ASEVD402: An authenticated independent worker control channel is required.", error.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.", error.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, console.ReadOutputString());
        Assert.Equal(string.Empty, console.ReadErrorString());
    }
}
