namespace ForgeTrust.AppSurface.Release;

/// <summary>Read-only CLI entry point for verifying a host-selected Docs archive tuple.</summary>
[Command("verify-docs-archive-tuple", Description = "Verify a selected Docs archive against its publication plan and SHA-256 sidecar.")]
internal sealed partial class VerifyDocsArchiveTupleCommand : ICommand
{
    private const int MaximumPathCharacters = 4096;
    private readonly ReleaseExecutionContext _executionContext;

    /// <summary>Creates the command with the invocation directory used to resolve relative input paths.</summary>
    /// <param name="executionContext">CLI invocation context.</param>
    public VerifyDocsArchiveTupleCommand(ReleaseExecutionContext executionContext)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        _executionContext = executionContext;
    }

    /// <summary>Gets the expected SemVer release version without a leading <c>v</c>.</summary>
    [CommandOption("version", Description = "Expected release version without a leading v.")]
    public string? VersionText { get; set; }

    /// <summary>Gets the host-selected archive path.</summary>
    [CommandOption("archive", Description = "Host-selected appsurface-docs-v{version}.tar.gz file.")]
    public string? ArchivePath { get; set; }

    /// <summary>Gets the host-selected publication plan path.</summary>
    [CommandOption("plan", Description = "Host-selected docs-publication-plan.json file.")]
    public string? PlanPath { get; set; }

    /// <summary>Gets the host-selected SHA-256 sidecar path.</summary>
    [CommandOption("sha256", Description = "Host-selected archive .sha256 sidecar.")]
    public string? Sha256Path { get; set; }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        var cancellationToken = console.RegisterCancellationHandler();
        try
        {
            var versionText = VersionText;
            if (string.IsNullOrWhiteSpace(versionText)
                || versionText.Length > 128
                || !SemVer.TryParse(versionText, out var version)
                || !string.Equals(version.ToString(), versionText, StringComparison.Ordinal))
            {
                throw InvalidArguments();
            }

            var archivePath = ResolveInputPath(ArchivePath);
            var planPath = ResolveInputPath(PlanPath);
            var sha256Path = ResolveInputPath(Sha256Path);
            var result = await ReleaseDocsArchiveTupleVerifier.VerifyAsync(
                version,
                archivePath,
                planPath,
                sha256Path,
                cancellationToken).ConfigureAwait(false);
            // Version is at most 128 canonical ASCII characters; every other text field is
            // derived from it or is a fixed-length digest, so this result stays under 4 KiB.
            var json = JsonSerializer.Serialize(result, ReleaseJson.Options);
            await console.Output.WriteLineAsync(json).ConfigureAwait(false);
            Environment.ExitCode = 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            console.Error.WriteLine(ReleaseDiagnostic.Error(
                "release-docs-archive-tuple-cancelled",
                "Docs archive tuple verification was cancelled.",
                "Verification did not complete.",
                "Rerun verification when the selected private artifact handoff is ready.",
                "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication").Render());
            Environment.ExitCode = 1;
        }
        catch (ReleaseToolException exception)
        {
            console.Error.WriteLine(exception.Diagnostic.Render());
            Environment.ExitCode = 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            console.Error.WriteLine(ReleaseDiagnostic.Error(
                "release-docs-archive-tuple-invalid",
                "Docs archive tuple verification failed.",
                "A selected tuple input could not be read safely.",
                "Select the archive, publication plan, and generated .sha256 sidecar for the same release version, then rerun verification.",
                "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication").Render());
            Environment.ExitCode = 1;
        }
    }

    private string ResolveInputPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumPathCharacters)
        {
            throw InvalidArguments();
        }

        return Path.GetFullPath(value, _executionContext.CurrentDirectory);
    }

    private static ReleaseToolException InvalidArguments() => new(ReleaseDiagnostic.Error(
        "release-docs-archive-tuple-arguments-invalid",
        "Docs archive tuple verification requires bounded trusted inputs.",
        "The expected version or one of the selected input paths is missing or invalid.",
        "Pass a canonical --version and host-selected --archive, --plan, and --sha256 file paths.",
        "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication"));
}
