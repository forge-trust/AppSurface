using ForgeTrust.AppSurface.Durable;
using Microsoft.Extensions.DependencyInjection;

namespace AppSurfaceDurableWorker.Work;

/// <summary>Accepts typed sample Work through the public Durable client without coupling producers to activation HTTP.</summary>
public sealed class SampleWorkProducer
{
    private readonly IDurableWorkClient _client;

    /// <summary>Creates a producer that uses the host's registered public Durable work client.</summary>
    /// <param name="client">Client used to persist the typed Work request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    public SampleWorkProducer(IDurableWorkClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Persists one caller-identified sample request.</summary>
    /// <param name="work">Validated typed input.</param>
    /// <param name="commandId">Stable command identity for the logical caller operation.</param>
    /// <param name="idempotencyKey">Caller-owned duplicate-submission key; reuse it only for a retry of the same intent.</param>
    /// <param name="cancellationToken">Cancels the persistence request.</param>
    /// <returns>The provider's typed acceptance result; acceptance does not mean the Work reached a terminal state.</returns>
    /// <remarks>
    /// The producer uses the one registered definition, its captured codecs and retry policy, and the fixed sample scope.
    /// Activation remains a separate payload-free HTTP wake. The application owns command identity and idempotency-key
    /// lifetime; changing either on retry can create a distinct durable request.
    /// </remarks>
    public ValueTask<DurableOperationResult<DurableWorkAcceptance>> AcceptAsync(
        SampleWork work,
        DurableCommandId commandId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var request = SampleWorkDefinition.Definition.CreateRequest(
            new DurableScopeId(SampleWorkDefinition.Scope),
            commandId,
            idempotencyKey,
            work);
        return _client.EnqueueAsync(request, cancellationToken);
    }
}
