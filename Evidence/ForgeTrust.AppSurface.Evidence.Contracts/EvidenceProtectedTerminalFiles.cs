namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Writes the fixed canonical CLI/v2 SDK terminal layout through an existing protected root.</summary>
/// <remarks>This helper allocates no root, grants no admission and accepts no caller-selected path or writer.
/// Call only inside the original bounded collection callback after owned work and cleanup have joined.
/// Every exclusive write is verified against its exact bytes; disposal and root completion remain caller-owned.</remarks>
internal static class EvidenceProtectedTerminalFiles
{
    /// <summary>Writes and verifies the plan, manifest and closed informational/protected summary.</summary>
    /// <param name="root">The actual allocated Linux artifact root, retained by the original worker.</param>
    /// <param name="plan">The exact completed admission plan.</param>
    /// <param name="manifest">The completed canonical manifest; structural verification grants no authority.</param>
    /// <param name="token">The original collection token, without a replacement deadline.</param>
    /// <returns>Only after all three original writes and verification operations settle.</returns>
    internal static async ValueTask WriteAsync(EvidenceLinuxArtifactRoot root, EvidencePlan plan,
        EvidenceManifest manifest, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(root);
        token.ThrowIfCancellationRequested();
        if (!EvidenceManifestBuilder.Verify(plan, manifest))
            throw new EvidenceAdmissionException("ASEVD410", "The terminal manifest does not match its completed plan.");
        await WriteOneAsync(root, "evidence-plan.json", EvidenceCanonicalJson.Serialize(plan), token).ConfigureAwait(false);
        await WriteOneAsync(root, "evidence-manifest.json", EvidenceCanonicalJson.Serialize(manifest), token).ConfigureAwait(false);
        await WriteOneAsync(root, "evidence-summary.json", EvidenceCanonicalJson.Serialize(new
        {
            manifest.Mode,
            manifest.ClaimKind,
            manifest.Eligibility,
            manifest.ExecutionVerdict,
            manifest.EnvelopeStatus,
            Procedure = "registered-protected-producer",
            SandboxAttestation = false,
        }), token).ConfigureAwait(false);
    }

    private static async ValueTask WriteOneAsync(EvidenceLinuxArtifactRoot root, string path, byte[] bytes,
        CancellationToken token)
    {
        await root.WriteAsync(path, bytes, token).ConfigureAwait(false);
        await root.VerifyAsync(path, bytes.Length, EvidenceDigest.Sha256(bytes), token).ConfigureAwait(false);
    }
}
