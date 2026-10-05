using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.EvidenceGate;

/// <summary>Loads bounded trusted verifier inputs and renders the independent PR gate result.</summary>
internal static class EvidenceGateVerifier
{
    private const int MaximumIdentityBytes = 16 * 1024;
    private const int MaximumSummaryBytes = 128 * 1024;

    /// <summary>Verifies a handed-off plan and manifest using trusted controller inputs.</summary>
    public static async Task<int> ExecuteAsync(
        string planPath,
        string manifestPath,
        string policyPath,
        string repositoryPath,
        string identityPath,
        string outputDirectory,
        string? artifactHandoffRootPath,
        IEvidencePullRequestGateAuthorityProvider authorityProvider,
        IEvidencePullRequestGateArtifactVerifier? artifactVerifier,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorityProvider);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        string? outputRoot = null;
        try
        {
            outputRoot = Path.GetFullPath(outputDirectory);
            EnsureOutputTargetsAreNew(outputRoot);
            cancellationToken.ThrowIfCancellationRequested();

            var planBytes = await ReadBoundedAsync(planPath, EvidencePullRequestGateVerifier.MaximumContractBytes, cancellationToken).ConfigureAwait(false);
            var manifestBytes = await ReadBoundedAsync(manifestPath, EvidencePullRequestGateVerifier.MaximumContractBytes, cancellationToken).ConfigureAwait(false);
            var policyBytes = await ReadBoundedAsync(policyPath, EvidencePullRequestGateVerifier.MaximumContractBytes, cancellationToken).ConfigureAwait(false);
            var identityBytes = await ReadBoundedAsync(identityPath, MaximumIdentityBytes, cancellationToken).ConfigureAwait(false);

            var plan = DeserializeCanonical<EvidencePlan>(planBytes);
            var manifest = DeserializeCanonical<EvidenceManifest>(manifestBytes);
            // The base-owned policy is a reviewed source file, not an artifact handoff.
            // Parse its value while retaining byte-canonical requirements on untrusted inputs.
            var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(policyBytes);
            var identity = DeserializeCanonical<EvidencePullRequestGateExpectedIdentity>(identityBytes);

            var result = await EvidencePullRequestGateVerifier.VerifyAsync(
                new EvidencePlanner(),
                policy,
                repositoryPath,
                identity,
                authorityProvider,
                artifactHandoffRootPath,
                artifactVerifier,
                plan,
                manifest,
                cancellationToken).ConfigureAwait(false);

            await standardOutput.WriteLineAsync(EvidenceGateResultRenderer.RenderTerminal(result)).ConfigureAwait(false);
            if (!result.IsEligible)
            {
                await standardError.WriteLineAsync($"{result.Code}: {result.Diagnostic}").ConfigureAwait(false);
            }

            await WriteResultOutputsAsync(outputRoot, result, CancellationToken.None).ConfigureAwait(false);

            return result.IsEligible ? 0 : 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await EmitFailureAsync(
                outputRoot,
                new EvidencePullRequestGateVerificationResult(false, "ASEVG008", "Pull-request evidence verification was cancelled."),
                standardOutput,
                standardError,
                130).ConfigureAwait(false);
        }
        catch (EvidenceGateInputException exception)
        {
            return await EmitFailureAsync(
                outputRoot,
                InputFailure(exception.Code),
                standardOutput,
                standardError,
                2).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        {
            return await EmitFailureAsync(outputRoot, InputFailure("ASEGG102"), standardOutput, standardError, 2).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException)
        {
            return await EmitFailureAsync(outputRoot, InputFailure("ASEGG199"), standardOutput, standardError, 2).ConfigureAwait(false);
        }
    }

    private static EvidencePullRequestGateVerificationResult InputFailure(string code) =>
        new(false, code, "Trusted gate input or output could not be processed safely.");

    private static async Task<int> EmitFailureAsync(
        string? outputRoot,
        EvidencePullRequestGateVerificationResult result,
        TextWriter standardOutput,
        TextWriter standardError,
        int exitCode)
    {
        if (outputRoot is not null)
        {
            try
            {
                await WriteResultOutputsAsync(outputRoot, result, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                and not StackOverflowException
                and not AccessViolationException
                and not AppDomainUnloadedException)
            {
                // A machine result cannot be persisted when the trusted output target is unavailable.
                // Keep the process response fixed and secret-safe in that case.
            }
        }

        await standardOutput.WriteLineAsync(EvidenceGateResultRenderer.RenderTerminal(result)).ConfigureAwait(false);
        await standardError.WriteLineAsync($"{result.Code}: {result.Diagnostic}").ConfigureAwait(false);
        return exitCode;
    }

    private static async Task WriteResultOutputsAsync(
        string outputRoot,
        EvidencePullRequestGateVerificationResult result,
        CancellationToken cancellationToken)
    {
        EnsureOutputTargetsAreNew(outputRoot);
        var resultBytes = EvidenceCanonicalJson.Serialize(result);
        var summaryBytes = EvidenceCanonicalJson.Serialize(new
        {
            eligible = result.IsEligible,
            code = result.Code,
            diagnostic = result.Diagnostic,
            summary = result.Summary,
        });
        var markdownBytes = System.Text.Encoding.UTF8.GetBytes(EvidenceGateResultRenderer.RenderMarkdown(result));
        if (resultBytes.Length > EvidencePullRequestGateVerifier.MaximumContractBytes
            || summaryBytes.Length > MaximumSummaryBytes
            || markdownBytes.Length > MaximumSummaryBytes)
        {
            throw new EvidenceGateInputException("ASEGG106");
        }

        Directory.CreateDirectory(outputRoot);
        await WriteNewAsync(Path.Join(outputRoot, "evidence-gate-summary.json"), summaryBytes, cancellationToken).ConfigureAwait(false);
        await WriteNewAsync(Path.Join(outputRoot, "evidence-gate-summary.md"), markdownBytes, cancellationToken).ConfigureAwait(false);
        // Publish the authoritative machine verdict last. Earlier output or
        // presentation failures must never leave an eligible result behind.
        await WriteNewAsync(Path.Join(outputRoot, "evidence-gate-verification.json"), resultBytes, cancellationToken).ConfigureAwait(false);
    }

    private static TValue DeserializeCanonical<TValue>(byte[] bytes)
    {
        try
        {
            var value = EvidenceCanonicalJson.Deserialize<TValue>(bytes);
            if (!EvidenceCanonicalJson.Serialize(value).AsSpan().SequenceEqual(bytes))
            {
                throw new EvidenceGateInputException("ASEGG103");
            }

            return value;
        }
        catch (JsonException)
        {
            throw new EvidenceGateInputException("ASEGG103");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (output.Length + read > maximumBytes)
                {
                    throw new EvidenceGateInputException("ASEGG107");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (FileNotFoundException)
        {
            throw new EvidenceGateInputException("ASEGG102");
        }
        catch (DirectoryNotFoundException)
        {
            throw new EvidenceGateInputException("ASEGG102");
        }
    }

    private static void EnsureOutputTargetsAreNew(string outputRoot)
    {
        foreach (var fileName in new[] { "evidence-gate-verification.json", "evidence-gate-summary.json", "evidence-gate-summary.md" })
        {
            if (File.Exists(Path.Join(outputRoot, fileName)) || Directory.Exists(Path.Join(outputRoot, fileName)))
            {
                throw new EvidenceGateInputException("ASEGG108");
            }
        }
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class EvidenceGateInputException(string code) : Exception
    {
        public string Code { get; } = code;
    }
}
