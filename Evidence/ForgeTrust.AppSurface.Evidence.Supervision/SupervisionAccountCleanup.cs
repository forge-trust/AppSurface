namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Runs the caller's fixed reverse account-cleanup commands with a physical barrier after each attempt.</summary>
/// <remarks>The caller supplies the already-reversed immutable snapshot and retains all owner, custody, NSS,
/// deadline, command-selection, and utility authority. This procedure adds no timeout, retry, detached task,
/// account lookup, or ownership decision.</remarks>
internal static class SupervisionAccountCleanup
{
    /// <summary>Attempts each original command once, captures every recoverable-or-fatal exception, and continues.</summary>
    /// <param name="originalCommands">The caller's exact reverse-cleanup snapshot, in dispatch order.</param>
    /// <param name="originalAttempt">The original native attempt, including its name check and custody checks.</param>
    /// <param name="requireSettledAfterAttempt">The original physical-settlement barrier after each command.</param>
    /// <param name="captureFailure">The caller's original first-failure capture and stage projection.</param>
    /// <returns><see langword="true"/> if at least one original attempt faulted.</returns>
    /// <exception cref="ArgumentNullException">Thrown before enumeration or dispatch when an input callback is missing.</exception>
    internal static async Task<bool> RunAsync(IReadOnlyList<LinuxRunAccountCommand> originalCommands,
        Func<LinuxRunAccountCommand, Task> originalAttempt, Action requireSettledAfterAttempt,
        Action<LinuxRunAccountCommand, Exception> captureFailure)
    {
        ArgumentNullException.ThrowIfNull(originalCommands);
        ArgumentNullException.ThrowIfNull(originalAttempt);
        ArgumentNullException.ThrowIfNull(requireSettledAfterAttempt);
        ArgumentNullException.ThrowIfNull(captureFailure);

        var hadFailure = false;
        foreach (var command in originalCommands)
        {
            try { await originalAttempt(command).ConfigureAwait(false); }
            catch (Exception error)
            {
                hadFailure = true;
                captureFailure(command, error);
            }
            requireSettledAfterAttempt();
        }
        return hadFailure;
    }
}
