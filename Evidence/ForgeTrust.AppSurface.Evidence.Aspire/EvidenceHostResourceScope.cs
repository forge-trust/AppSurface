using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Transfers a prepared run's exact readiness bindings and active application ownership to EvidenceHost.</summary>
/// <remarks>
/// Construct this scope only after the resource factory successfully starts the requested topology. The factory
/// owns and must clean up partial construction before returning a scope. On successful return, the framework owns
/// the scope until all producer tasks, readiness tasks, and cleanup actually settle. Disposal joins the same task;
/// a bounded wait elsewhere never releases this ownership. See the prepared-host recipe in the Evidence reuse guide.
/// </remarks>
public sealed class EvidenceHostResourceScope : IAsyncDisposable
{
    private readonly IAsyncDisposable _applicationOwner;
    private readonly object _sync = new();
    private Task? _cleanup;

    /// <summary>Creates a transfer scope containing exactly the requested readiness registrations.</summary>
    /// <param name="resources">Distinct readiness registrations for the requested resource identifiers.</param>
    /// <param name="applicationOwner">Owner whose disposal stops and joins the whole started application.</param>
    /// <remarks>If construction throws, ownership remains with the caller, which must dispose partial resources.</remarks>
    public EvidenceHostResourceScope(
        IReadOnlyList<IEvidenceResourceReadiness> resources,
        IAsyncDisposable applicationOwner)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(applicationOwner);
        var bindings = new Dictionary<string, IEvidenceResourceReadiness>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            ArgumentNullException.ThrowIfNull(resource);
            ArgumentException.ThrowIfNullOrWhiteSpace(resource.Id);
            if (!bindings.TryAdd(resource.Id, resource))
                throw new ArgumentException($"Resource '{resource.Id}' has duplicate readiness bindings.", nameof(resources));
        }

        Resources = new ReadOnlyDictionary<string, IEvidenceResourceReadiness>(bindings);
        _applicationOwner = applicationOwner;
    }

    /// <summary>Gets the frozen readiness bindings transferred by the resource factory.</summary>
    public IReadOnlyDictionary<string, IEvidenceResourceReadiness> Resources { get; }

    /// <summary>Stops and joins readiness ownership and the application exactly once.</summary>
    /// <returns>The actual cleanup task; concurrent callers join the same operation.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
            return new ValueTask(_cleanup ??= CleanAsync());
    }

    private async Task CleanAsync()
    {
        Exception? firstFailure = null;
        Exception? fatalFailure = null;
        var owned = Resources.Values.Reverse().Cast<object>()
            .Append(_applicationOwner)
            .Distinct(ReferenceEqualityComparer.Instance);
        foreach (var owner in owned)
        {
            try
            {
                if (owner is IAsyncDisposable asynchronous)
                    await asynchronous.DisposeAsync().ConfigureAwait(false);
                else if (owner is IDisposable synchronous)
                    synchronous.Dispose();
            }
            catch (Exception exception) when (EvidenceHostBootstrap.IsNonFatalException(exception))
            {
                firstFailure ??= exception;
            }
            catch (Exception exception)
            {
                fatalFailure ??= exception;
            }
        }

        if (fatalFailure is not null)
            ExceptionDispatchInfo.Capture(fatalFailure).Throw();
        if (firstFailure is not null)
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
    }
}
