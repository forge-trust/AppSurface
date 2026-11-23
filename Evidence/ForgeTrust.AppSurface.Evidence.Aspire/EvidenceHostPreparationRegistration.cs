using System.Collections.ObjectModel;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Describes the preparation decision for one selected producer before resource startup.</summary>
public enum EvidenceReuseStatus
{
    /// <summary>A verified local receipt can satisfy the producer informationally.</summary>
    Hit,

    /// <summary>The producer is eligible but needs a fresh execution.</summary>
    Miss,

    /// <summary>The producer cannot be keyed safely and needs an ordinary fresh execution.</summary>
    Ineligible,

    /// <summary>Safe execution ownership could not be established; no resource factory or producer may run.</summary>
    Unavailable,
}

/// <summary>Provides a bounded, secret-safe explanation of a producer's local receipt decision.</summary>
/// <param name="ProducerId">Selected producer identifier.</param>
/// <param name="Status">Whether a receipt is usable or fresh execution is required.</param>
/// <param name="Reason">Stable <c>reuse.*</c> diagnostic reason.</param>
/// <param name="Diagnostic">Secret-safe explanation and recovery guidance.</param>
/// <param name="ProducerKey">Content key when preparation completed; otherwise null.</param>
/// <param name="OriginCompletedAt">Original completion time for a verified hit; reading does not renew it.</param>
/// <param name="OriginElapsedMilliseconds">Historical producer duration, never a current invocation timing sample.</param>
public sealed record EvidenceReuseDecision(
    string ProducerId,
    EvidenceReuseStatus Status,
    string Reason,
    string Diagnostic,
    string? ProducerKey = null,
    DateTimeOffset? OriginCompletedAt = null,
    long? OriginElapsedMilliseconds = null);

/// <summary>Reports safe native key-wait progress without exposing receipt paths, commands or environment values.</summary>
/// <param name="ProducerKeyPrefix">Bounded prefix of the content key currently waiting for ownership.</param>
/// <param name="Elapsed">Cumulative elapsed acquisition time across all keys and permitted reprepare.</param>
/// <param name="Remaining">Remaining invocation-wide acquisition budget.</param>
public sealed record EvidenceReuseWaitProgress(string ProducerKeyPrefix, TimeSpan Elapsed, TimeSpan Remaining);

/// <summary>Registers producer implementations and resource identities without starting a resource topology.</summary>
/// <remarks>
/// Use this registration in <c>EvidenceHostBootstrap.PrepareAsync</c>. Registration and key-material
/// callbacks must be local, side-effect-free, and cancellation-aware. Constructing a producer must not start
/// a process, container, browser, or application. A producer's asynchronous disposal must join every child it owns.
/// Register exactly one implementation for every selected producer and an immutable identity for every selected
/// resource, including resources no producer uses. Resource identities are metadata only; readiness instances are
/// supplied later by the prepared run's resource factory.
/// </remarks>
public sealed class EvidenceHostPreparationRegistration
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly Dictionary<string, PreparedProducerRegistration> _producers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _resources = new(StringComparer.Ordinal);
    private bool _frozen;

    /// <summary>Gets the explicitly registered resource identities without readiness instances.</summary>
    public IReadOnlyDictionary<string, string> ResourceIdentities => new ReadOnlyDictionary<string, string>(_resources);

    /// <summary>Gets the optional trusted execution-envelope verifier.</summary>
    public IEvidenceExecutionEnvelopeVerifier? EnvelopeVerifier { get; private set; }

    /// <summary>Registers one side-effect-free producer with a stable execution identity.</summary>
    /// <param name="producer">Typed producer whose identifier matches the selected declaration.</param>
    /// <param name="implementationIdentity">Non-secret, explicit implementation identity, at most 256 UTF-8 bytes.</param>
    /// <param name="toolIdentity">Optional non-secret effective tool versions and argument identities.</param>
    /// <remarks>
    /// Change the implementation identity whenever undeclared implementation behavior changes. Declare every
    /// effective file, environment, tool, fixture and external-state identity; otherwise omit Inputs and execute fresh.
    /// Tool identity is copied now and only its hashes are persisted. It does not authorize launching a tool here.
    /// </remarks>
    public void AddProducer(
        IEvidenceProducer producer,
        string implementationIdentity,
        IReadOnlyDictionary<string, string>? toolIdentity = null)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(producer);
        ValidateIdentity(implementationIdentity, nameof(implementationIdentity), 256);
        var tools = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in toolIdentity ?? new Dictionary<string, string>())
        {
            ValidateIdentity(pair.Key, nameof(toolIdentity), 128);
            ValidateIdentity(pair.Value, nameof(toolIdentity), 4096);
            if (tools.Count >= 64 || !tools.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException("Effective tool identity permits at most 64 unique ordinal keys.", nameof(toolIdentity));
        }

        if (!_producers.TryAdd(producer.Id, new PreparedProducerRegistration(
            producer, implementationIdentity, new ReadOnlyDictionary<string, string>(tools))))
            throw new InvalidOperationException($"Evidence producer '{producer.Id}' is already registered.");
    }

    /// <summary>Registers an immutable effective fixture or image identity before any readiness instance exists.</summary>
    /// <param name="resourceId">Declared resource identifier.</param>
    /// <param name="identity">Non-secret immutable identity, at most 4096 UTF-8 bytes.</param>
    /// <remarks>
    /// Preparation requires exactly one identity for every selected profile resource, including unused resources.
    /// A mutable image tag, service name, or endpoint alone does not identify the state read by a producer.
    /// </remarks>
    public void AddResource(string resourceId, string identity)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ValidateIdentity(identity, nameof(identity), 4096);
        if (!_resources.TryAdd(resourceId, identity))
            throw new InvalidOperationException($"Evidence resource '{resourceId}' is already registered.");
    }

    /// <summary>Sets the single explicit execution-envelope verifier.</summary>
    /// <param name="verifier">Consumer-owned verifier, invoked before any resource factory or producer.</param>
    public void SetEnvelopeVerifier(IEvidenceExecutionEnvelopeVerifier verifier)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(verifier);
        if (EnvelopeVerifier is not null)
            throw new InvalidOperationException("EvidenceHost accepts exactly one execution-envelope verifier.");
        EnvelopeVerifier = verifier;
    }

    /// <summary>Freezes the explicit registrations before asynchronous preparation can observe them.</summary>
    /// <returns>The frozen producer map.</returns>
    internal IReadOnlyDictionary<string, PreparedProducerRegistration> Freeze()
    {
        _frozen = true;
        return new ReadOnlyDictionary<string, PreparedProducerRegistration>(_producers);
    }

    private void EnsureMutable()
    {
        if (_frozen)
            throw new InvalidOperationException("Evidence preparation registrations are frozen.");
    }

    private static void ValidateIdentity(string value, string parameterName, int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Identity must contain valid Unicode.", parameterName, exception);
        }

        if (byteCount > maximumBytes || value.Any(char.IsControl))
            throw new ArgumentException($"Identity must contain no control characters and at most {maximumBytes} UTF-8 bytes.", parameterName);
    }
}

/// <summary>Captures one explicitly identified, side-effect-free producer registration.</summary>
/// <param name="Producer">Owned producer instance.</param>
/// <param name="ImplementationIdentity">Bounded explicit implementation identity.</param>
/// <param name="ToolIdentity">Frozen effective tool identities; raw values are never persisted.</param>
internal sealed record PreparedProducerRegistration(
    IEvidenceProducer Producer,
    string ImplementationIdentity,
    IReadOnlyDictionary<string, string> ToolIdentity);
