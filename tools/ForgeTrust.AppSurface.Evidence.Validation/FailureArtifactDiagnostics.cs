using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Retains best-effort failure proofs without replacing the producer's primary failure.</summary>
internal static class FailureArtifactDiagnostics
{
    /// <summary>
    /// Attempts each closed producer artifact independently and returns a diagnostic suffix containing
    /// only logical names and exception types. Exception messages and producer output are withheld.
    /// </summary>
    internal static async Task<string> WriteAsync(EvidenceArtifactWriter writer, IReadOnlyList<(string Name, byte[] Contents)> artifacts)
    {
        List<string> unavailable = [];
        foreach (var (name, contents) in artifacts)
        {
            try
            {
                await writer.WriteAsync(name, $"proof/{name}.json", contents, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                unavailable.Add($"{name} ({exception.GetType().Name})");
            }
        }

        return unavailable.Count == 0 ? string.Empty : " Failure artifacts unavailable: " + string.Join(", ", unavailable) + ".";
    }
}
