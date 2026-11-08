using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Auth.Keycloak;
/// <summary>Bounded public phases emitted by one recipe invocation.</summary>
public enum KeycloakStagePhase
{
    /// <summary>Verified input capture.</summary>
    Capture,
    /// <summary>Offline context materialization.</summary>
    Materialization,
    /// <summary>Host Docker prerequisite verification.</summary>
    DockerPreflight,
    /// <summary>Host-owned image build.</summary>
    Build,
    /// <summary>Immutable image and runtime inspection.</summary>
    Inspection,
    /// <summary>Atomic observation publication.</summary>
    Evidence,
    /// <summary>Inspection completed; separate release gates remain.</summary>
    ReleasePending
}

/// <summary>Outcome of a bounded stage event.</summary>
public enum KeycloakStageOutcome
{
    /// <summary>The stage began.</summary>
    Started,
    /// <summary>The stage completed its declared work.</summary>
    Succeeded,
    /// <summary>The stage failed or did not complete.</summary>
    Rejected
}

/// <summary>A safe, bounded observation of a recipe stage. It contains no paths, process output, or exception text.</summary>
public sealed record KeycloakStageEvent(string CorrelationId, KeycloakStagePhase Phase, KeycloakStageOutcome Outcome, long ElapsedMilliseconds, int? MaterialCount, long? MaterialBytes, int? CheckCount, KeycloakEffectState Effect, string? RecipeDigest, string? PolicyDigest, string? PlanDigest, string? Profile, string? OperationId, string? CheckId, string? SafeCode, bool Truncated, DateTimeOffset OccurredAtUtc);
/// <summary>Publishes structured stage events for diagnostics and bounded host progress displays.</summary>
public static class KeycloakStageEvents
{
    /// <summary>The BCL activity source used when an OpenTelemetry listener is installed.</summary>
    public static readonly ActivitySource ActivitySource = new("ForgeTrust.AppSurface.Auth.Keycloak");
    private static readonly AsyncLocal<ObserverContext?> Current = new();
    /// <summary>Subscribes to safe stage events. Observer failures are isolated from the recipe workflow.</summary>
    public static IDisposable Subscribe(Action<KeycloakStageEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var context = Current.Value ??= new ObserverContext();
        context.Add(observer);
        return new Subscription(context, observer);
    }

    /// <summary>Starts one correlated stage scope; disposing an unfinished scope emits a rejection.</summary>
    public static KeycloakStageScope Begin(KeycloakStagePhase phase, string? recipeDigest = null, string? policyDigest = null, string? profile = null, string? planDigest = null, string? operationId = null, string? checkId = null) => new(phase, recipeDigest, policyDigest, profile, planDigest, operationId, checkId);
    internal static void Publish(KeycloakStageEvent value)
    {
        var context = Current.Value;
        if (context is null)
            return;
        var observers = context.Copy();
        foreach (var observer in observers)
        {
            try
            {
                observer(value);
            }
            catch
            { /* diagnostics must never change workflow outcome */
            }
        }
    }

    private sealed class ObserverContext
    {
        private readonly object gate = new();
        private ImmutableArray<Action<KeycloakStageEvent>> observers = ImmutableArray<Action<KeycloakStageEvent>>.Empty;
        internal void Add(Action<KeycloakStageEvent> observer)
        {
            lock (gate)
                observers = observers.Add(observer);
        }

        internal void Remove(Action<KeycloakStageEvent> observer)
        {
            lock (gate)
                observers = observers.Remove(observer);
        }

        internal Action<KeycloakStageEvent>[] Copy()
        {
            lock (gate)
                return observers.ToArray();
        }
    }

    private sealed class Subscription(ObserverContext context, Action<KeycloakStageEvent> observer) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            context.Remove(observer);
        }
    }
}

/// <summary>Disposable stage scope that emits exactly one start and one terminal event.</summary>
public sealed class KeycloakStageScope : IDisposable
{
    private static readonly Regex Digest = new("^[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Activity? activity;
    private int terminal;
    private bool truncated;
    private int? materialCount;
    private long? materialBytes;
    private int? checkCount;
    private string? recipeDigest;
    private string? policyDigest;
    private string? planDigest;
    private string? profile;
    private string? operationId;
    private string? checkId;
    internal KeycloakStageScope(KeycloakStagePhase phase, string? recipeDigest, string? policyDigest, string? profile, string? planDigest, string? operationId, string? checkId)
    {
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase));
        Phase = phase;
        CorrelationId = ActivityTraceId.CreateRandom().ToString();
        activity = KeycloakStageEvents.ActivitySource.StartActivity("keycloak.stage", ActivityKind.Internal);
        if (activity is not null)
            CorrelationId = activity.TraceId.ToString();
        SetIdentity(recipeDigest, policyDigest, profile);
        SetContext(planDigest, operationId, checkId);
        Emit(KeycloakStageOutcome.Started, KeycloakEffectState.None, null);
    }

    /// <summary>Stable correlation identity shared by this stage's start and terminal events.</summary>
    public string CorrelationId { get; }
    /// <summary>Stage represented by this scope.</summary>
    public KeycloakStagePhase Phase { get; }
    /// <summary>The most recently selected bounded check identity, when a stage has one.</summary>
    public string? CheckId => checkId;

    /// <summary>Sets safe content identities and a bounded profile key.</summary>
    public void SetIdentity(string? recipeDigest, string? policyDigest, string? profile)
    {
        this.recipeDigest = NormalizeDigest(recipeDigest);
        this.policyDigest = NormalizeDigest(policyDigest);
        this.profile = NormalizeProfile(profile);
    }

    /// <summary>Sets optional bounded plan, operation, and check identities.</summary>
    public void SetContext(string? planDigest = null, string? operationId = null, string? checkId = null)
    {
        this.planDigest = NormalizeDigest(planDigest);
        this.operationId = NormalizeKey(operationId, 64);
        this.checkId = NormalizeKey(checkId, 96);
    }

    /// <summary>Sets bounded counters; negative values are rejected before publication.</summary>
    public void SetCounts(int? materialCount = null, long? materialBytes = null, int? checkCount = null)
    {
        if (materialCount is < 0 || materialBytes is < 0 || checkCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(materialCount));
        this.materialCount = materialCount;
        this.materialBytes = materialBytes;
        this.checkCount = checkCount;
    }

    /// <summary>Publishes a successful terminal event.</summary>
    public void Complete(KeycloakEffectState effect = KeycloakEffectState.Committed) => Terminal(KeycloakStageOutcome.Succeeded, effect, null);
    /// <summary>Publishes a typed rejection using only stable diagnostic fields.</summary>
    public void Reject(KeycloakSecurityException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        operationId = NormalizeKey(failure.OperationId, 64);
        Terminal(KeycloakStageOutcome.Rejected, failure.Effect, failure.Code.ToString());
    }

    /// <summary>Publishes a safe rejection for a host boundary that has no typed package failure.</summary>
    public void Reject(string safeCode, KeycloakEffectState effect = KeycloakEffectState.Unknown) => Terminal(KeycloakStageOutcome.Rejected, effect, NormalizeCode(safeCode));
    /// <summary>Ends an unfinished scope with a nonpassing outcome and releases its activity.</summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref terminal, 1, 0) == 0)
            Emit(KeycloakStageOutcome.Rejected, KeycloakEffectState.Unknown, "UNCOMPLETED");
        activity?.Stop();
        GC.SuppressFinalize(this);
    }

    private void Terminal(KeycloakStageOutcome outcome, KeycloakEffectState effect, string? code)
    {
        if (Interlocked.CompareExchange(ref terminal, 1, 0) != 0)
            return;
        Emit(outcome, effect, code);
        activity?.Stop();
    }

    private void Emit(KeycloakStageOutcome outcome, KeycloakEffectState effect, string? code) => KeycloakStageEvents.Publish(new(CorrelationId, Phase, outcome, Math.Max(0, clock.ElapsedMilliseconds), materialCount, materialBytes, checkCount, effect, recipeDigest, policyDigest, planDigest, profile, operationId, checkId, code, truncated, DateTimeOffset.UtcNow));
    private string? NormalizeDigest(string? value)
    {
        if (value is null)
            return null;
        if (Digest.IsMatch(value))
            return value;
        truncated = true;
        return null;
    }

    private string? NormalizeProfile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var source = value.Length > 64 ? value[..64] : value;
        var safe = new string(source.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' ? c : '_').ToArray());
        truncated |= safe.Length != value.Length || !safe.Equals(value, StringComparison.Ordinal);
        return safe;
    }

    private string NormalizeCode(string? value)
    {
        var source = value ?? string.Empty;
        if (source.Length > 48)
        {
            source = source[..48];
            truncated = true;
        }

        var safe = new string(source.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? char.ToUpperInvariant(c) : '_').ToArray());
        truncated |= safe.Length != value?.Length || !safe.Equals(value, StringComparison.Ordinal);
        return safe.Length == 0 ? "UNSPECIFIED" : safe;
    }

    private string? NormalizeKey(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var source = value.Length > maximum ? value[..maximum] : value;
        var safe = new string(source.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' ? c : '_').ToArray());
        truncated |= safe.Length != value.Length || !safe.Equals(value, StringComparison.Ordinal);
        return safe;
    }
}
