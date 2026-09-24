using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Durable.Testing;

/// <summary>Builds a typed Work request by delegating encoding and identity to its definition.</summary>
/// <typeparam name="TWork">Typed input.</typeparam>
/// <typeparam name="TResult">Successful result.</typeparam>
public sealed class DurableWorkRequestBuilder<TWork, TResult> where TWork : notnull
{
    private readonly DurableWorkDefinition<TWork, TResult> _definition;
    private DurableScopeId _scope = new("test-scope");
    private DurableCommandId _command = new("test-command");
    private string _key = "test-key";
    private TWork? _work;
    private bool _hasWork;
    private DurableWorkRetryPolicy? _retry;
    private DateTimeOffset? _due;

    /// <summary>Creates a builder for the required definition.</summary>
    public DurableWorkRequestBuilder(DurableWorkDefinition<TWork, TResult> definition)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    /// <summary>Sets owning scope; the default is <c>test-scope</c>.</summary>
    public DurableWorkRequestBuilder<TWork, TResult> WithScope(DurableScopeId value) { _scope = value; return this; }
    /// <summary>Sets command identity; the default is <c>test-command</c>.</summary>
    public DurableWorkRequestBuilder<TWork, TResult> WithCommand(DurableCommandId value) { _command = value; return this; }
    /// <summary>Sets idempotency key.</summary>
    public DurableWorkRequestBuilder<TWork, TResult> WithIdempotencyKey(string value) { _key = value; return this; }
    /// <summary>Sets typed work input.</summary>
    public DurableWorkRequestBuilder<TWork, TResult> WithWork(TWork value) { _work = value; _hasWork = true; return this; }
    /// <summary>Sets a retry override.</summary>
    public DurableWorkRequestBuilder<TWork, TResult> WithRetryPolicy(DurableWorkRetryPolicy? value) { _retry = value; return this; }
    /// <summary>Sets initial eligibility time.</summary>
    public DurableWorkRequestBuilder<TWork, TResult> WithDueAtUtc(DateTimeOffset? value) { _due = value; return this; }
    /// <summary>Creates the request through <see cref="DurableWorkDefinition{TWork,TResult}.CreateRequest"/>.</summary>
    public DurableWorkRequest Build()
    {
        if (!_hasWork) throw new InvalidOperationException("A typed Work payload must be supplied before building.");
        return _definition.CreateRequest(_scope, _command, _key, _work!, _retry, _due);
    }
}

/// <summary>Builds native worker envelopes while retaining execution and fence identity.</summary>
/// <typeparam name="TPayload">Optional typed outcome payload.</typeparam>
public sealed class DurableWorkerEnvelopeBuilder<TPayload>
{
    private DurableWorkerProjectionOutcome _outcome;
    private string _reason = "test-result";
    private DurableWorkerRetryability _retryability;
    private DurableWorkerCorrelation? _correlation;
    private DurableWorkerExecutionIdentity? _identity;
    private TPayload? _payload;
    /// <summary>Sets outcome.</summary>
    public DurableWorkerEnvelopeBuilder<TPayload> WithOutcome(DurableWorkerProjectionOutcome value) { _outcome = value; return this; }
    /// <summary>Sets safe reason code.</summary>
    public DurableWorkerEnvelopeBuilder<TPayload> WithReasonCode(string value) { _reason = value; return this; }
    /// <summary>Sets retry classification.</summary>
    public DurableWorkerEnvelopeBuilder<TPayload> WithRetryability(DurableWorkerRetryability value) { _retryability = value; return this; }
    /// <summary>Sets correlation identity.</summary>
    public DurableWorkerEnvelopeBuilder<TPayload> WithCorrelation(DurableWorkerCorrelation value) { _correlation = value; return this; }
    /// <summary>Sets required native execution and fencing identity.</summary>
    public DurableWorkerEnvelopeBuilder<TPayload> WithExecutionIdentity(DurableWorkerExecutionIdentity value) { _identity = value; return this; }
    /// <summary>Sets optional typed payload.</summary>
    public DurableWorkerEnvelopeBuilder<TPayload> WithPayload(TPayload? value) { _payload = value; return this; }
    /// <summary>Creates the envelope through <see cref="DurableWorkerEnvelope{TPayload}.CreateNative"/>.</summary>
    public DurableWorkerEnvelope<TPayload> Build() => DurableWorkerEnvelope<TPayload>.CreateNative(
        _outcome, _reason, _retryability, _correlation!, _identity!, _payload);
}
