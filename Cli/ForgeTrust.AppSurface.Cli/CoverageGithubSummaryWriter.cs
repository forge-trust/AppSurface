using System.Text;
using CliFx;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Appends the bounded coverage-gate Markdown artifact to a GitHub Actions step summary.
/// </summary>
/// <remarks>
/// This is command presentation, not coverage evaluation. The coverage core writes the owned Markdown artifact;
/// this CLI adapter reads that artifact only when the public <c>--github-summary</c> behavior is enabled.
/// </remarks>
internal static class CoverageGithubSummaryWriter
{
    private const int SummaryLimitBytes = 1024 * 1024;

    /// <summary>
    /// Appends the supplied Markdown artifact when GitHub provided a summary path.
    /// </summary>
    public static async Task AppendAsync(string? githubStepSummaryPath, string markdownReportPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(githubStepSummaryPath))
        {
            return;
        }

        var path = Path.GetFullPath(githubStepSummaryPath);
        var markdown = await File.ReadAllTextAsync(markdownReportPath, cancellationToken).ConfigureAwait(false);
        var sanitized = StripControls(markdown);
        var bytes = Encoding.UTF8.GetBytes(sanitized);
        if (bytes.Length > SummaryLimitBytes)
        {
            sanitized = Encoding.UTF8.GetString(bytes.AsSpan(0, SummaryLimitBytes));
        }

        try
        {
            await File.AppendAllTextAsync(path, sanitized + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CommandException($"ASCOV008 Failed to write GitHub step summary '{path}': {exception.Message}");
        }
    }

    private static string StripControls(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t'))
        {
            builder.Append(character);
        }

        return builder.ToString();
    }
}
