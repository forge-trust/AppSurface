using System.Diagnostics;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>
/// Stops and joins an explicitly enrolled set of consumer-owned processes.
/// </summary>
/// <remarks>
/// Enroll the leader and every independently surviving descendant before execution starts. The
/// consumer retains ownership of the process handles and must keep them open until stop completes.
/// This capability force-terminates each enrolled live process and its currently discoverable tree,
/// then verifies exit of every enrolled handle, even when the leader has already exited. It does
/// not discover detached descendants after their parent exits, establish a sandbox, or attest an
/// arbitrary process tree. Use OS-level containment when complete enrollment cannot be guaranteed.
/// </remarks>
public sealed class EvidenceProcessLifetime : IEvidenceExecutionLifetime
{
    private readonly Process[] _ownedProcesses;
    private readonly Action<Process> _terminateProcess;
    private readonly object _stopLock = new();
    private Task? _stop;

    /// <summary>Creates a stop capability from already-started, explicitly owned process handles.</summary>
    /// <param name="ownedProcesses">The leader and all independently surviving descendants; handles remain consumer-owned.</param>
    public EvidenceProcessLifetime(IReadOnlyCollection<Process> ownedProcesses)
        : this(ownedProcesses, static process => process.Kill(entireProcessTree: true))
    {
    }

    /// <summary>Creates a lifetime with a termination boundary for deterministic OS-failure verification.</summary>
    /// <param name="ownedProcesses">Started, consumer-owned handles to stop and join.</param>
    /// <param name="terminateProcess">Terminates a live handle or throws its termination failure.</param>
    internal EvidenceProcessLifetime(IReadOnlyCollection<Process> ownedProcesses, Action<Process> terminateProcess)
    {
        ArgumentNullException.ThrowIfNull(ownedProcesses);
        ArgumentNullException.ThrowIfNull(terminateProcess);
        if (ownedProcesses.Count == 0)
        {
            throw new ArgumentException("Enroll at least one owned process.", nameof(ownedProcesses));
        }
        if (ownedProcesses.Any(process => process is null))
        {
            throw new ArgumentException("Owned process handles cannot be null.", nameof(ownedProcesses));
        }
        _ownedProcesses = ownedProcesses.Distinct(ReferenceEqualityComparer.Instance).Cast<Process>().ToArray();
        _terminateProcess = terminateProcess;
        // Validate usable, started handles before this lease can be registered.
        foreach (var process in _ownedProcesses) _ = process.Id;
    }

    /// <summary>Force-terminates enrolled process trees and verifies every enrolled process exited.</summary>
    /// <param name="cancellationToken">The host cleanup slice. Failure or cancellation never proves successful termination.</param>
    /// <returns>A completion proving all enrolled handles exited. Repeated calls share the first stop attempt.</returns>
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Task stop;
        lock (_stopLock)
        {
            if (_stop is null)
            {
                _stop = Task.Run(() => StopOwnedAsync(cancellationToken));
                _ = _stop.ContinueWith(static faulted => { _ = faulted.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            stop = _stop;
        }
        await stop.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StopOwnedAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        // Issue termination to all owners before waiting on any single one.
        foreach (var process in _ownedProcesses.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!process.HasExited) _terminateProcess(process);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or AggregateException)
            {
                // Still attempt every other owner when one handle cannot be terminated.
                failure ??= exception;
            }
        }

        foreach (var process in _ownedProcesses)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (!process.HasExited) throw new InvalidOperationException("An enrolled evidence process did not exit.");
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                failure ??= exception;
            }
        }
        if (failure is not null) throw new InvalidOperationException("An enrolled evidence process could not be stopped and joined.", failure);
    }
}
