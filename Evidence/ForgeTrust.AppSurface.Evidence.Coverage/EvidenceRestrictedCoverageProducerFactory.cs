using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Creates metadata for the fixed first-party restricted coverage producer.</summary>
/// <remarks>
/// Creation performs no execution and grants no admission. Production execution requires a callback-scoped
/// lease internally bound to an activated protected artifact writer by the CLI or Aspire host. Public/local
/// writers, caller-selected paths and public transport injection cannot supply that lease. The production
/// consumer acceptance registry remains closed; this factory does not establish Trusted or native acceptance.
/// </remarks>
public static class EvidenceRestrictedCoverageProducerFactory
{
    /// <summary>Snapshots a coverage 1.0.0 declaration and returns its fixed implementation as producer metadata.</summary>
    /// <param name="declaration">Complete selected declaration; its bytes must match the protected lease at execution.</param>
    /// <returns>A producer that can execute only through a matching internally bound protected writer.</returns>
    /// <exception cref="ArgumentNullException">The declaration is null.</exception>
    /// <exception cref="EvidenceAdmissionException">ASEVD404 for unsupported or malformed producer metadata.</exception>
    public static EvidenceRestrictedCoverageRegistration Create(EvidenceProducerDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        try
        {
            if (!BoundedText(declaration.Id, 96)
                || declaration.Kind != "coverage" || declaration.Version != "1.0.0"
                || declaration.RequiredResources is null || declaration.RequiredResources.Count > EvidenceProfileLimits.MaximumResources
                || declaration.RequiredResources.Any(static item => !BoundedText(item, 96))
                || declaration.AssertionIds is null || declaration.AssertionIds.Count > 128
                || declaration.AssertionIds.Any(static item => !BoundedText(item, 256))
                || declaration.ArtifactSlots is null || declaration.ArtifactSlots.Count > 64
                || declaration.ArtifactSlots.Any(static item => item is null || !BoundedText(item.LogicalName, 96)
                    || !BoundedText(item.RelativeRoot, 4095) || !BoundedText(item.MediaType, 128)
                    || item.MaximumBytes is < 1 or > EvidenceArtifactWriter.MaximumTotalArtifactBytes)
                || declaration.TimeoutSeconds is < 1 or > 1800)
                throw InvalidDeclaration();
            var bytes = EvidenceCanonicalJson.Serialize(declaration);
            if (bytes.Length > EvidenceCanonicalJson.MaximumInputBytes) throw InvalidDeclaration();
            return new EvidenceRestrictedCoverageRegistration(EvidenceCanonicalJson.Deserialize<EvidenceProducerDeclaration>(bytes));
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or ArgumentException or NullReferenceException)
        {
            throw InvalidDeclaration();
        }
    }

    private static EvidenceAdmissionException InvalidDeclaration() => new("ASEVD404", "Restricted coverage producer metadata is invalid or unsupported.");
    private static bool BoundedText(string? text, int maximum) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= maximum && !text.Any(char.IsControl);
}

/// <summary>Inspectable immutable metadata for the one fixed restricted coverage implementation.</summary>
/// <remarks>
/// The sealed type and full declaration let the host reject implementation/declaration substitution before
/// application startup. Neither this type nor matching declaration bytes grant admission or a producer lease.
/// Only the metadata factory creates it; execution requires an internally bound protected writer.
/// </remarks>
public sealed class EvidenceRestrictedCoverageRegistration : IEvidenceProducer
{
    /// <summary>Snapshots metadata only; creates no transport, worker or runtime capability.</summary>
    internal EvidenceRestrictedCoverageRegistration(EvidenceProducerDeclaration declaration)
    {
        Declaration = declaration with
        {
            RequiredResources = Array.AsReadOnly(declaration.RequiredResources.ToArray()),
            AssertionIds = Array.AsReadOnly(declaration.AssertionIds.ToArray()),
            ArtifactSlots = Array.AsReadOnly(declaration.ArtifactSlots.ToArray()),
        };
    }

    /// <summary>Gets the complete deep-readonly declaration snapshot used for exact host registration matching.</summary>
    public EvidenceProducerDeclaration Declaration { get; }
    /// <inheritdoc />
    public string Id => Declaration.Id;

    /// <summary>Runs the fixed procedure through this context's one internally bound protected producer lease.</summary>
    /// <param name="context">Exact admitted plan/declaration and protected writer; public/local writers reject.</param>
    /// <param name="cancellationToken">Additional caller cancellation, linked with the actual host stage token.</param>
    /// <returns>The owned procedure task; ignoring it cannot remove it from lifecycle stop/join ownership.</returns>
    /// <exception cref="EvidenceAdmissionException">ASEVD410 for missing, stale, mismatched or reused binding.</exception>
    public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
    {
        if (context?.Artifacts is not { } writer) throw EvidenceRestrictedProducerLease.Failure();
        var lease = writer.GetRestrictedProducerLease();
        return new ValueTask<EvidenceProducerResult>(lease.RunAsync(context, Declaration, async (binding, token) =>
        {
            var bytes = binding.CopyDiffBytes();
            var diff = bytes is null ? null : new EvidenceRestrictedCoverageDiffSnapshot(bytes, "protected-diff", EvidenceDigest.Sha256(bytes));
            var producer = new EvidenceRestrictedCoverageProducer(new EvidenceRestrictedCoverageTransport(binding.Worker, binding.OutputQuota),
                new CoverageMergeWorkflow(new EvidenceProtectedReportGenerator(binding.Worker, binding.OutputQuota), TimeProvider.System));
            return await producer.RunAsync(Declaration, binding.Worker.Descriptor.Solution, diff, writer, token).ConfigureAwait(false);
        }, cancellationToken));
    }
}

/// <summary>Bounded immutable diff bytes used only by the restricted coverage procedure's patch analysis.</summary>
/// <remarks>The production factory obtains bytes from the lease; this internal data type creates no execution authority.</remarks>
internal sealed class EvidenceRestrictedCoverageDiffSnapshot
{
    private readonly byte[] _bytes;

    /// <summary>Copies bounded captured bytes and verifies their supplied digest without reopening a source path.</summary>
    /// <param name="bytes">The captured UTF-8 diff, at most the bounded JSON input limit.</param>
    /// <param name="label">Safe nonempty source label.</param>
    /// <param name="sha256">Exact lower-case SHA-256 computed from those bytes.</param>
    internal EvidenceRestrictedCoverageDiffSnapshot(byte[] bytes, string label, string sha256)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > EvidenceCanonicalJson.MaximumInputBytes || string.IsNullOrWhiteSpace(label)
            || label.Length > 128 || label.Any(char.IsControl) || EvidenceDigest.Sha256(bytes) != sha256)
            throw new EvidenceAdmissionException("ASEVD403", "Restricted coverage diff metadata is invalid.");
        _bytes = bytes.ToArray();
        Label = label;
        Sha256 = sha256;
    }
    /// <summary>Gets the copied bytes reused by patch analysis.</summary>
    internal ReadOnlyMemory<byte> Bytes => _bytes;
    /// <summary>Gets the bounded source label.</summary>
    internal string Label { get; }
    /// <summary>Gets the verified digest of the copied bytes.</summary>
    internal string Sha256 { get; }
}
