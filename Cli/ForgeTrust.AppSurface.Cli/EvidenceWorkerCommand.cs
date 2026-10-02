using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Cli;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Runs the protected worker selected by the independently armed Linux launcher.</summary>
[Command("evidence worker", Description = "Protected Linux launcher entry. Requires an authenticated independent control channel.")]
internal sealed partial class EvidenceWorkerCommand : ICommand
{
    /// <summary>Gets or sets the root-owned launcher's control socket.</summary>
    [CommandOption("control", Description = "Absolute protected Unix control socket supplied by the launcher.")]
    public string ControlChannel { get; set; } = string.Empty;

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IConsole console)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ControlChannel))
                throw new EvidenceAdmissionException("ASEVD402", "An authenticated independent worker control channel is required.");
            var manifest = await EvidenceProtectedCliExecution.RunAsync(ControlChannel, console.RegisterCancellationHandler()).ConfigureAwait(false);
            if (manifest.ClaimKind == EvidenceClaimKind.None)
                throw new CommandException("ASEVD211: Evidence execution was incomplete. Inspect the protected failure manifest and use a fresh supervised run after owned exit.");
        }
        catch (EvidenceAdmissionException exception) { throw new CommandException(exception.Message); }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or UnauthorizedAccessException
            or ArgumentException or KeyNotFoundException or FormatException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            throw new CommandException("ASEVD402: Protected worker control or input validation failed. Fix: inspect the launcher and use a fresh supervised run. See start-here/evidencehost.md.");
        }
    }
}
