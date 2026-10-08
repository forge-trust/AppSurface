using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Supervision;
using System.Text;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Runs reserved Evidence roles without ordinary CLI discovery or service configuration.</summary>
/// <remarks>
/// Role parsing is data only. The worker still authenticates the root peer and exact runtime binding.
/// Supervisor execution invokes the internal guarded empty Observation composition, without a supplied
/// owner, backend or service graph. Actual root Linux, protected deployment, OS bootstrap and native
/// owner checks remain mandatory. Wiring this checkpoint candidate establishes no native acceptance
/// or Trusted qualification, and there is no Python fallback.
/// </remarks>
internal static class EvidenceProcessEntryPoint
{
    /// <summary>Dispatches fixed help, the authenticated worker or the guarded root checkpoint and returns its process outcome.</summary>
    /// <param name="arguments">Unmodified executable arguments.</param>
    /// <param name="console">Process console; tests may capture it without replacing runtime authority.</param>
    /// <returns>Zero for successful role work/help; one for a closed control or admission failure.</returns>
    /// <remarks>
    /// Supervisor success emits only canonical Mode, ClaimKind, Eligibility, ExecutionVerdict and
    /// CleanupCompleted fields from the actual returned manifest, after native execution and cleanup
    /// return. Root execution/cleanup rejection emits one closed four-field diagnostic on stderr, then
    /// the unchanged fixed ASEVD410 message. Unsupported/unprivileged entry and clean caller cancellation
    /// keep their original ASEVD402 behavior without this packet. Capturing this console cannot replace
    /// the protected execution or inject a positive result.
    /// </remarks>
    internal static async Task<int> RunAsync(string[] arguments, IConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        var supervisorSelected = false;
        try
        {
            var selected = EvidenceProcessRoleParser.Parse(arguments);
            if (selected.Help)
            {
                await console.Output.WriteLineAsync(selected.Role == EvidenceProcessRole.Worker
                    ? "Usage: appsurface evidence worker --control <protected-unix-socket>"
                    : "Usage: appsurface evidence supervise --request <protected-request-file>");
                return 0;
            }

            if (selected.Role == EvidenceProcessRole.Supervisor)
            {
                supervisorSelected = true;
                var manifest = await LinuxEmptyObservationExecution.RunAsync(selected.Path!,
                    console.RegisterCancellationHandler()).ConfigureAwait(false);
                var summary = EvidenceCanonicalJson.Serialize(new
                {
                    manifest.Mode,
                    manifest.ClaimKind,
                    manifest.Eligibility,
                    manifest.ExecutionVerdict,
                    CleanupCompleted = manifest.Metrics.CleanupCompleted
                });
                await console.Output.WriteLineAsync(Encoding.UTF8.GetString(summary));
                return 0;
            }

            await new EvidenceWorkerCommand { ControlChannel = selected.Path }.ExecuteAsync(console).ConfigureAwait(false);
            return 0;
        }
        catch (EvidenceNativeObservationException error) when (supervisorSelected)
        {
            try { await console.Error.WriteLineAsync(error.Failure.ToJson()); }
            catch (Exception) { } // Diagnostic output cannot replace the original fixed negative outcome.
            await console.Error.WriteLineAsync(error.Message);
            return 1;
        }
        catch (EvidenceAdmissionException error)
        {
            await console.Error.WriteLineAsync(error.Message);
            return 1;
        }
        catch (CommandException error)
        {
            await console.Error.WriteLineAsync(error.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            await console.Error.WriteLineAsync("ASEVD402: Protected process execution was cancelled.");
            return 1;
        }
    }
}
