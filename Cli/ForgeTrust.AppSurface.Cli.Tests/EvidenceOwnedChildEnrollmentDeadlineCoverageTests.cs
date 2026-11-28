using System.IO.Pipes;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceOwnedChildEnrollmentDeadlineCoverageTests
{
    [Fact]
    public async Task EnrollmentWithoutAcknowledgementClosesThePipeAtItsProtocolDeadline()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pipeName = "ec-" + Guid.NewGuid().ToString("N")[..16];
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(deadline.Token);
            var begin = await EvidenceOwnedChildWire.ReadFrameAsync(server, deadline.Token);
            Assert.StartsWith("BEGIN|", begin ?? string.Empty, StringComparison.Ordinal);
            Assert.Null(await EvidenceOwnedChildWire.ReadFrameAsync(server, deadline.Token));
        }, deadline.Token);
        var workInvoked = false;

        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceOwnedChild.RunAsync(_ =>
            {
                workInvoked = true;
                return Task.CompletedTask;
            }, Channel(pipeName), CancellationToken.None).WaitAsync(deadline.Token));
            await serverTask.WaitAsync(deadline.Token);

            Assert.Contains("protocol deadline", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(workInvoked);
        }
        finally
        {
            deadline.Cancel();
            await server.DisposeAsync();
            try
            {
                await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            catch (IOException) when (deadline.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (deadline.IsCancellationRequested) { }
        }
    }

    private static IReadOnlyDictionary<string, string> Channel(string pipeName) => new Dictionary<string, string>
    {
        [EvidenceChildLifetimeSession.PipeEnvironmentName] = pipeName,
        [EvidenceChildLifetimeSession.ExecutionEnvironmentName] = Guid.NewGuid().ToString("N"),
        [EvidenceChildLifetimeSession.NonceEnvironmentName] = Guid.NewGuid().ToString("N"),
    };
}
