using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>Creates immutable typed Work contracts without activating storage or execution.</summary>
public static class DurableWork
{
    /// <summary>Defines Work identity, codec facts, provider safety and an explicit default retry policy once.</summary>
    /// <typeparam name="TWork">Work input type.</typeparam>
    /// <typeparam name="TResult">Successful result type.</typeparam>
    /// <param name="workName">Stable Work name, at most 200 characters.</param>
    /// <param name="workVersion">Immutable Work version, at most 100 characters.</param>
    /// <param name="workCodec">Explicitly approved input codec.</param>
    /// <param name="resultCodec">Explicitly approved result codec; its identity may differ from Work and input.</param>
    /// <param name="providerSafety">Declared provider-effect ambiguity policy.</param>
    /// <param name="defaultRetryPolicy">Required request default; use <see cref="DurableWorkRetryPolicy.Default"/> for ordinary policy.</param>
    /// <returns>A passive definition with stable guarded codec views.</returns>
    /// <exception cref="ArgumentNullException">A codec or retry default is null.</exception>
    /// <exception cref="ArgumentException">An identifier or declared codec type is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Safety or classification is undefined.</exception>
    /// <remarks>Metadata is captured once per source. Custom getter exceptions propagate; no serializer or executor runs.</remarks>
    public static DurableWorkDefinition<TWork, TResult> Define<TWork, TResult>(
        string workName, string workVersion, IDurablePayloadCodec<TWork> workCodec,
        IDurablePayloadCodec<TResult> resultCodec, DurableProviderSafety providerSafety,
        DurableWorkRetryPolicy defaultRetryPolicy)
    {
        ArgumentNullException.ThrowIfNull(workCodec);
        ArgumentNullException.ThrowIfNull(resultCodec);
        ArgumentNullException.ThrowIfNull(defaultRetryPolicy);
        return new(DurableWorkContractSnapshot<TWork, TResult>.Create(workName, workVersion, providerSafety,
            workCodec, resultCodec, defaultRetryPolicy));
    }
}

/// <summary>Owns immutable Work contract facts and builds equivalent ordinary durable requests.</summary>
/// <typeparam name="TWork">Work input type.</typeparam>
/// <typeparam name="TResult">Successful result type.</typeparam>
/// <remarks>
/// Safe to reuse statically when custom codecs and their dependencies support concurrent calls. Frozen metadata does
/// not freeze application behavior. Changing defaults affects future requests only; accepted Work retains its facts.
/// </remarks>
public sealed class DurableWorkDefinition<TWork, TResult>
{
    /// <summary>Creates a definition from one closed, validated contract.</summary>
    internal DurableWorkDefinition(DurableWorkContractSnapshot<TWork, TResult> snapshot)
    {
        Snapshot = snapshot;
    }

    /// <summary>Gets the authoritative internal contract; binding and registration never reconstruct it.</summary>
    internal DurableWorkContractSnapshot<TWork, TResult> Snapshot { get; }
    /// <summary>Gets the stable Work name.</summary>
    public string WorkName => Snapshot.Identity.WorkName;
    /// <summary>Gets the immutable Work version.</summary>
    public string WorkVersion => Snapshot.Identity.WorkVersion;
    /// <summary>Gets the exact Work registry identity.</summary>
    public DurableWorkContractIdentity ContractIdentity => Snapshot.Identity;
    /// <summary>Gets the definition-owned guarded input codec with captured metadata.</summary>
    public IDurablePayloadCodec<TWork> WorkCodec => Snapshot.WorkView;
    /// <summary>Gets the definition-owned guarded successful result codec with captured metadata.</summary>
    public IDurablePayloadCodec<TResult> ResultCodec => Snapshot.ResultView;
    /// <summary>Gets the declared provider-effect ambiguity policy.</summary>
    public DurableProviderSafety ProviderSafety => Snapshot.ProviderSafety;
    /// <summary>Gets the explicit retry policy used when a request omits an override.</summary>
    public DurableWorkRetryPolicy DefaultRetryPolicy => Snapshot.DefaultRetryPolicy;

    /// <summary>Validates caller choices, encodes input once and creates the existing immutable request.</summary>
    /// <param name="scopeId">Trusted owning scope.</param>
    /// <param name="commandId">Caller command identity.</param>
    /// <param name="idempotencyKey">Explicit duplicate-submission key, at most 200 characters.</param>
    /// <param name="work">Non-null input approved by the codec.</param>
    /// <param name="retryPolicy">Optional override; null selects this definition's default.</param>
    /// <param name="dueAtUtc">Optional initial eligibility time, normalized to UTC; null means immediately eligible.</param>
    /// <returns>A request with the same fields and fingerprint as equivalent direct construction.</returns>
    /// <exception cref="ArgumentException">Caller identities are invalid or the codec rejects the input.</exception>
    /// <exception cref="ArgumentNullException">Input is null.</exception>
    /// <exception cref="InvalidOperationException">Encoded output disagrees with captured codec metadata.</exception>
    /// <remarks>All caller identity checks precede encoding. Codec exceptions propagate. No registration or storage is required.</remarks>
    public DurableWorkRequest CreateRequest(DurableScopeId scopeId, DurableCommandId commandId,
        string idempotencyKey, TWork work, DurableWorkRetryPolicy? retryPolicy = null, DateTimeOffset? dueAtUtc = null)
    {
        DurableIdentifier.Require(scopeId.Value, nameof(scopeId), 200);
        DurableIdentifier.Require(commandId.Value, nameof(commandId), 200);
        DurableIdentifier.Require(idempotencyKey, nameof(idempotencyKey), 200);
        ArgumentNullException.ThrowIfNull(work);
        return new(scopeId, commandId, idempotencyKey, WorkName, WorkVersion, WorkCodec.Encode(work),
            ProviderSafety, retryPolicy ?? DefaultRetryPolicy, dueAtUtc);
    }

    /// <summary>Binds an ordinary transient executor; reconcile-before-retry still requires a reconciler before registration.</summary>
    /// <typeparam name="TExecutor">Class implementing the exact Work/result executor contract.</typeparam>
    /// <returns>A passive binding referencing this exact definition.</returns>
    public DurableWorkBinding<TWork, TResult, TExecutor> ExecutedBy<TExecutor>()
        where TExecutor : class, IDurableWorkerExecutor<TWork, TResult> => new(this);

    /// <summary>Binds an exit-aware transient executor for ProviderKeyed Work.</summary>
    /// <typeparam name="TExecutor">Class implementing the exact Work/result exit contract.</typeparam>
    /// <returns>A complete passive exit binding.</returns>
    /// <exception cref="InvalidOperationException">The definition does not declare ProviderKeyed safety.</exception>
    public DurableExitWorkBinding<TWork, TResult, TExecutor> ExecutedByExit<TExecutor>()
        where TExecutor : class, IDurableWorkExitExecutor<TWork, TResult>
    {
        if (ProviderSafety != DurableProviderSafety.ProviderKeyed)
        {
            throw new InvalidOperationException("Exit-aware Work requires ProviderKeyed safety. Define a ProviderKeyed contract before binding an exit executor.");
        }

        return new(this);
    }
}

/// <summary>Shared closed Work facts used by all built-in registrations and definitions.</summary>
internal class DurableWorkContractSnapshot
{
    /// <summary>Initializes facts already validated by the capture operation.</summary>
    protected DurableWorkContractSnapshot(DurableWorkContractIdentity identity, DurableProviderSafety safety,
        DurableWorkRetryPolicy retry, DurablePayloadCodecSnapshot work, DurablePayloadCodecSnapshot result)
    {
        Identity = identity;
        ProviderSafety = safety;
        DefaultRetryPolicy = retry;
        Work = work;
        Result = result;
    }

    /// <summary>Exact validated Work name/version.</summary>
    internal DurableWorkContractIdentity Identity { get; }
    /// <summary>Declared provider-effect ambiguity policy.</summary>
    internal DurableProviderSafety ProviderSafety { get; }
    /// <summary>Request construction default, never a replacement for accepted policy.</summary>
    internal DurableWorkRetryPolicy DefaultRetryPolicy { get; }
    /// <summary>Captured input source and metadata.</summary>
    internal DurablePayloadCodecSnapshot Work { get; }
    /// <summary>Captured successful-result source and metadata.</summary>
    internal DurablePayloadCodecSnapshot Result { get; }

    /// <summary>Validates common facts without invoking consumer codec behavior.</summary>
    protected static DurableWorkContractIdentity Validate(string workName, string workVersion, DurableProviderSafety providerSafety,
        DurableWorkRetryPolicy defaultRetryPolicy)
    {
        var identity = new DurableWorkContractIdentity(workName, workVersion);
        if (!Enum.IsDefined(providerSafety))
        {
            throw new ArgumentOutOfRangeException(nameof(providerSafety));
        }

        ArgumentNullException.ThrowIfNull(defaultRetryPolicy);
        return identity;
    }

    /// <summary>Closes external subclasses of the legacy abstract registration without exposing a new public seam.</summary>
    internal static DurableWorkContractSnapshot Capture(string name, string version, DurableProviderSafety safety,
        IDurablePayloadCodec work, IDurablePayloadCodec result)
    {
        var identity = Validate(name, version, safety, DurableWorkRetryPolicy.Default);
        var (input, output) = CaptureCodecs(work, result,
            DurablePayloadCodecSnapshot.Capture, DurablePayloadCodecSnapshot.Capture);
        return new(identity, safety, DurableWorkRetryPolicy.Default, input, output);
    }

    /// <summary>Captures each distinct source once, reuses supplied views and rejects conflicting facts for one source.</summary>
    /// <remarks>Capture delegates preserve typed invocation when closing generic contracts and untyped legacy subclasses.</remarks>
    protected static (DurablePayloadCodecSnapshot Work, DurablePayloadCodecSnapshot Result) CaptureCodecs<TWorkCodec, TResultCodec>(
        TWorkCodec work, TResultCodec result, Func<TWorkCodec, DurablePayloadCodecSnapshot> captureWork,
        Func<TResultCodec, DurablePayloadCodecSnapshot> captureResult)
        where TWorkCodec : IDurablePayloadCodec
        where TResultCodec : IDurablePayloadCodec
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(result);
        var resultSnapshot = DurablePayloadCodecSnapshot.GetSnapshot(result);
        var input = resultSnapshot is not null && ReferenceEquals(resultSnapshot.Source, work)
            ? resultSnapshot : captureWork(work);
        var output = ReferenceEquals(input.Source, resultSnapshot?.Source ?? result)
            ? resultSnapshot ?? input : captureResult(result);
        if (ReferenceEquals(input.Source, output.Source) && input.Facts != output.Facts)
        {
            throw new InvalidOperationException("A durable payload codec source was contributed with conflicting contract metadata.");
        }

        return (input, ReferenceEquals(input.Source, output.Source) ? input : output);
    }
}

/// <summary>Typed projection of the shared contract; views are created once and never recapture metadata.</summary>
internal sealed class DurableWorkContractSnapshot<TWork, TResult> : DurableWorkContractSnapshot
{
    private DurableWorkContractSnapshot(DurableWorkContractIdentity identity, DurableProviderSafety safety,
        DurableWorkRetryPolicy retry, DurablePayloadCodecSnapshot work, DurablePayloadCodecSnapshot result)
        : base(identity, safety, retry, work, result)
    {
        WorkView = (IDurablePayloadCodec<TWork>)work.CreateView();
        ResultView = ReferenceEquals(work, result)
            ? (IDurablePayloadCodec<TResult>)WorkView : (IDurablePayloadCodec<TResult>)result.CreateView();
    }

    /// <summary>Stable typed input projection used by definition and invocation.</summary>
    internal IDurablePayloadCodec<TWork> WorkView { get; }
    /// <summary>Stable typed result projection used by definition and invocation.</summary>
    internal IDurablePayloadCodec<TResult> ResultView { get; }

    /// <summary>Captures each unique source once, with independent exact generic-type checks.</summary>
    internal static DurableWorkContractSnapshot<TWork, TResult> Create(string name, string version,
        DurableProviderSafety safety, IDurablePayloadCodec<TWork> work, IDurablePayloadCodec<TResult> result,
        DurableWorkRetryPolicy retry)
    {
        var identity = Validate(name, version, safety, retry);
        var (input, output) = CaptureCodecs(work, result,
            DurablePayloadCodecSnapshot.Capture<TWork>, DurablePayloadCodecSnapshot.Capture<TResult>);
        if (input.Facts.PayloadType != typeof(TWork))
        {
            throw new ArgumentException("The durable input codec must declare the exact generic payload type.", nameof(work));
        }

        if (output.Facts.PayloadType != typeof(TResult))
        {
            throw new ArgumentException("The durable result codec must declare the exact generic payload type.", nameof(result));
        }

        return new(identity, safety, retry, input, output);
    }
}
