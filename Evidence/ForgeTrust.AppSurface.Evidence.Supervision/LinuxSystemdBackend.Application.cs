namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Typed application adapter retaining the exact original systemd connection.</summary>
internal sealed partial class LinuxSystemdBackend
{
    /// <summary>Sends cooperative TERM to only the generated application main; it starts no stop-job timer.</summary>
    internal async Task TerminateApplicationMainAsync(LinuxUnitName unit, CancellationToken token)
    {
        if (unit is null || !unit.Value.StartsWith("appsurface-evidence-application-", StringComparison.Ordinal)) throw InvalidControl();
        _ = await CallAsync(ManagerRequest("KillUnit", "ssi", unit.Value, "main", signal: 15), string.Empty,
            static _ => true, token).ConfigureAwait(false);
    }

    /// <summary>Starts only the closed application recipe; reply data does not prove owned startup.</summary>
    internal Task<string> StartApplicationAsync(LinuxApplicationUnit recipe, CancellationToken token) =>
        StartOwnedUnitAsync(recipe.Unit, recipe.Arguments, recipe.Properties, recipe.StandardOutput, recipe.StandardError, token);
}
