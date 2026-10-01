using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Release;

namespace ForgeTrust.AppSurface.EvidenceGate;

/// <summary>
/// Runs the existing read-only Release inspect authority and records its exact machine result as typed evidence.
/// </summary>
/// <remarks>
/// This producer proves only local V2 tag-bound release inspection. It does not authenticate a protected workflow
/// event, reread the remote protected tag, validate publication eligibility, or build NuGet/docs archive outputs.
/// It therefore returns no satisfied assertion and cannot close the protected release obligation on its own.
/// </remarks>
internal sealed class ProtectedReleaseEvidenceProducer : IEvidenceProducer
{
    internal const string ProducerId = "release-inspection";
    internal const string ReleaseProjectionArtifactName = "release-projection";
    internal const string ReleaseDigestIndexArtifactName = "release-digest-index";
    internal const string ProtectedReleaseAssertionId = "appsurface/release/protected-tag-and-preparation-base@1";
    internal const int MaximumProjectionBytes = 16 * 1024;
    internal const int MaximumDigestIndexBytes = 16 * 1024;

    private readonly string _repositoryRoot;
    private readonly IProtectedReleaseInvocationProvider _invocationProvider;
    private readonly IReleaseInspectMachineAuthority _inspectAuthority;

    internal ProtectedReleaseEvidenceProducer(
        string repositoryRoot,
        IProtectedReleaseInvocationProvider invocationProvider,
        IReleaseInspectMachineAuthority? inspectAuthority = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _invocationProvider = invocationProvider ?? throw new ArgumentNullException(nameof(invocationProvider));
        _inspectAuthority = inspectAuthority ?? new ReleaseInspectMachineAuthorityAdapter();
    }

    /// <inheritdoc />
    public string Id => ProducerId;

    /// <inheritdoc />
    public async ValueTask<EvidenceProducerResult> ProduceAsync(
        EvidenceProducerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(context.Producer.Id, Id, StringComparison.Ordinal)
            || context.Producer.Kind != "release-inspection"
            || context.Producer.ArtifactSlots.Count != 2
            || !context.Producer.ArtifactSlots.Any(static slot => slot.LogicalName == ReleaseProjectionArtifactName)
            || !context.Producer.ArtifactSlots.Any(static slot => slot.LogicalName == ReleaseDigestIndexArtifactName))
        {
            return Invalid("The selected release declaration does not match the closed protected-release producer contract.");
        }

        var invocation = await _invocationProvider.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        if (invocation is null)
        {
            return Unavailable("A trusted protected-release invocation identity is unavailable.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inspection = await _inspectAuthority.InspectAsync(
                _repositoryRoot,
                invocation.Version,
                invocation.Tag,
                invocation.BaseRef,
                cancellationToken).ConfigureAwait(false);
            if (!IsInvocationMatch(invocation, inspection))
            {
                return Invalid("Read-only release inspection did not match the trusted invocation identity.");
            }

            var projectionBytes = JsonSerializer.SerializeToUtf8Bytes(inspection, ReleaseJson.Options);
            if (projectionBytes.Length > MaximumProjectionBytes)
            {
                return Invalid("The validated release inspection exceeded the producer artifact bound.");
            }

            var digestIndexBytes = JsonSerializer.SerializeToUtf8Bytes(new ProtectedReleaseDigestIndex(
                Schema: "appsurface-protected-release-digest-index-v1",
                ReleaseArtifactDigests: inspection.ReleaseArtifactDigests
                    .Select(static artifact => new ProtectedReleaseDigestEntry(artifact.Path, artifact.Sha256))
                    .OrderBy(static artifact => artifact.Path, StringComparer.Ordinal)
                    .ToArray()), ReleaseJson.Options);
            if (digestIndexBytes.Length > MaximumDigestIndexBytes)
            {
                return Invalid("The validated release digest index exceeded the producer artifact bound.");
            }

            var artifacts = context.Artifacts ?? throw new InvalidOperationException("The EvidenceHost did not provide a declared artifact writer.");
            await artifacts.WriteAsync(
                ReleaseProjectionArtifactName,
                "release/projection/inspect.json",
                projectionBytes,
                cancellationToken).ConfigureAwait(false);
            await artifacts.WriteAsync(
                ReleaseDigestIndexArtifactName,
                "release/digests/index.json",
                digestIndexBytes,
                cancellationToken).ConfigureAwait(false);

            return new EvidenceProducerResult(
                Id,
                EvidenceProducerOutcome.Unavailable,
                [],
                "Local V2 release inspection was captured; protected remote-ref, release-event, and produced package/archive verification remain unavailable.",
                artifacts.WrittenArtifacts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ReleaseToolException)
        {
            return Invalid("The existing Release inspect authority rejected the tag or its V2 evidence.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or CryptographicException)
        {
            return Invalid("The protected-release inspection result could not be captured as bounded typed evidence.");
        }
    }

    private static bool IsInvocationMatch(ProtectedReleaseInvocation invocation, ReleaseInspectMachineResult result) =>
        string.Equals(result.Schema, ReleaseInspectMachineResult.CurrentSchema, StringComparison.Ordinal)
        && string.Equals(result.Version, invocation.Version, StringComparison.Ordinal)
        && string.Equals(result.Tag, invocation.Tag, StringComparison.Ordinal)
        && string.Equals(result.BaseRef, invocation.BaseRef, StringComparison.Ordinal)
        && string.Equals(result.TagObjectId, invocation.ExpectedTagObjectId, StringComparison.Ordinal)
        && string.Equals(result.PeeledCommit, invocation.ExpectedPeeledCommit, StringComparison.Ordinal);

    private EvidenceProducerResult Invalid(string diagnostic) =>
        new(Id, EvidenceProducerOutcome.Invalid, [], diagnostic);

    private EvidenceProducerResult Unavailable(string diagnostic) =>
        new(Id, EvidenceProducerOutcome.Unavailable, [], diagnostic);
}

/// <summary>Runs the Release package's existing versioned machine-inspect authority.</summary>
internal interface IReleaseInspectMachineAuthority
{
    Task<ReleaseInspectMachineResult> InspectAsync(
        string repositoryRoot,
        string version,
        string tag,
        string baseRef,
        CancellationToken cancellationToken);
}

/// <summary>Production adapter to the existing read-only Release inspect resolver.</summary>
internal sealed class ReleaseInspectMachineAuthorityAdapter : IReleaseInspectMachineAuthority
{
    public Task<ReleaseInspectMachineResult> InspectAsync(
        string repositoryRoot,
        string version,
        string tag,
        string baseRef,
        CancellationToken cancellationToken) =>
        ReleaseInspectMachineAuthority.InspectAsync(repositoryRoot, version, tag, baseRef, cancellationToken);
}

/// <summary>
/// Supplies bounded expected release identity from a separately validated trusted event context.
/// </summary>
internal interface IProtectedReleaseInvocationProvider
{
    ValueTask<ProtectedReleaseInvocation?> ReadAsync(EvidenceProducerContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Protected workflow identity expected to match the local release inspect result.
/// </summary>
internal sealed record ProtectedReleaseInvocation(
    string Version,
    string Tag,
    string BaseRef,
    string ExpectedTagObjectId,
    string ExpectedPeeledCommit);

/// <summary>Typed digest index copied from the validated release inspect result.</summary>
internal sealed record ProtectedReleaseDigestIndex(
    string Schema,
    IReadOnlyList<ProtectedReleaseDigestEntry> ReleaseArtifactDigests);

/// <summary>Digest for one release artifact validated at the captured peeled commit.</summary>
internal sealed record ProtectedReleaseDigestEntry(string Path, string Sha256);
