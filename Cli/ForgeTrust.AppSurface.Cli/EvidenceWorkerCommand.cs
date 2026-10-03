using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Cli;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
#if EVIDENCE_PRIVATE_QUALIFICATION_HOST
            var manifest = await EvidencePrivateQualificationHostEntry.RunAsync(ControlChannel, console.RegisterCancellationHandler()).ConfigureAwait(false);
#else
            var manifest = await EvidenceProtectedCliExecution.RunAsync(ControlChannel, console.RegisterCancellationHandler(),
                diagnostic => WriteAllocationDiagnostic(console, diagnostic)).ConfigureAwait(false);
#endif
            if (manifest.ClaimKind == EvidenceClaimKind.None && !EvidencePrivateQualificationBinding.IsCompletedResult(manifest))
                throw new CommandException("ASEVD211: Evidence execution was incomplete. Inspect the protected failure manifest and use a fresh supervised run after owned exit.");
        }
        catch (EvidenceAdmissionException exception) { throw new CommandException(exception.Message); }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or UnauthorizedAccessException
            or ArgumentException or KeyNotFoundException or FormatException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            throw new CommandException("ASEVD402: Protected worker control or input validation failed. Fix: inspect the launcher and use a fresh supervised run. See start-here/evidencehost.md.");
        }
    }

    /// <summary>Writes one fixed-schema, bounded private worker-journal line, never an exception or path.</summary>
    internal static void WriteAllocationDiagnostic(IConsole console, EvidenceAllocationFailureDiagnostic diagnostic)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter<EvidenceAllocationPhase>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<EvidenceLinuxArtifactAllocationOperation>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<EvidenceWorkerStageOutcome>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<EvidenceWorkerTerminalCode>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<EvidenceAllocationErrorClass>(allowIntegerValues: false));
        var line = JsonSerializer.Serialize(diagnostic, options);
        if (Encoding.UTF8.GetByteCount(line) + 1 <= 1024) console.Error.WriteLine(line);
    }
}
