using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Release;

namespace ForgeTrust.AppSurface.EvidenceGate;

/// <summary>
/// Runs the existing read-only Release inspect authority and records its exact machine result as typed evidence.
/// </summary>
/// <remarks>
/// This producer proves local V2 tag-bound release inspection and matches its annotated-tag object and peeled commit
/// against a fresh read of the trusted remote tag ref. It does not authenticate the protected workflow event, repeat
/// the remote read at final verdict or publication use, validate publication eligibility, or build and hash the exact
/// NuGet/docs archive outputs. It therefore returns no satisfied assertion and cannot close the protected release
/// obligation on its own.
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
    private readonly IProtectedReleaseRemoteTagAuthority _remoteTagAuthority;

    internal ProtectedReleaseEvidenceProducer(
        string repositoryRoot,
        IProtectedReleaseInvocationProvider invocationProvider,
        IReleaseInspectMachineAuthority? inspectAuthority = null,
        IProtectedReleaseRemoteTagAuthority? remoteTagAuthority = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _invocationProvider = invocationProvider ?? throw new ArgumentNullException(nameof(invocationProvider));
        _inspectAuthority = inspectAuthority ?? new ReleaseInspectMachineAuthorityAdapter();
        _remoteTagAuthority = remoteTagAuthority ?? new GitProtectedReleaseRemoteTagAuthority();
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

            var remoteTag = await _remoteTagAuthority.ReadAsync(invocation.Tag, cancellationToken).ConfigureAwait(false);
            if (remoteTag is null
                || remoteTag.PeeledCommit is null
                || !string.Equals(remoteTag.TagObjectId, inspection.TagObjectId, StringComparison.Ordinal)
                || !string.Equals(remoteTag.PeeledCommit, inspection.PeeledCommit, StringComparison.Ordinal))
            {
                return Invalid("The fresh remote protected tag was missing, lightweight, or no longer matched the inspected tag object and peeled commit.");
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
                "Local V2 release inspection and a matching fresh remote annotated-tag identity were captured; protected release-event validation and exact produced package/archive verification remain unavailable.",
                artifacts.WrittenArtifacts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProtectedReleaseRemoteTagUnavailableException)
        {
            return Unavailable("The protected release tag could not be reread from its trusted remote.");
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

/// <summary>Reads one exact protected tag ref directly from its trusted remote without updating local refs.</summary>
internal interface IProtectedReleaseRemoteTagAuthority
{
    Task<ProtectedReleaseRemoteTagObservation?> ReadAsync(
        string tag,
        CancellationToken cancellationToken);
}

/// <summary>Object identities returned by a fresh remote tag advertisement.</summary>
internal sealed record ProtectedReleaseRemoteTagObservation(string TagObjectId, string? PeeledCommit);

/// <summary>Indicates that a remote tag read could not be completed, rather than that its identity mismatched.</summary>
internal sealed class ProtectedReleaseRemoteTagUnavailableException : Exception
{
    internal ProtectedReleaseRemoteTagUnavailableException(Exception? innerException = null)
        : base("The protected release tag remote could not be read.", innerException)
    {
    }
}

/// <summary>Uses read-only <c>git ls-remote</c> to capture the current annotated-tag and peeled-commit identities.</summary>
/// <remarks>
/// Production reads are pinned to <see cref="ProtectedRepositoryRemoteUrl"/>. The child process inherits only PATH;
/// Git prompts, credential helpers, system/global configuration, extra headers, and proxies are disabled. Standard
/// output and error are drained concurrently with an 8 KiB bound and a 15-second timeout. Local file remotes are
/// available only through the explicitly test-only fixture method.
/// </remarks>
internal sealed class GitProtectedReleaseRemoteTagAuthority : IProtectedReleaseRemoteTagAuthority
{
    internal const string ProtectedRepositoryRemoteUrl = "https://github.com/forge-trust/AppSurface.git";
    private const int MaximumOutputCharacters = 8192;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    public async Task<ProtectedReleaseRemoteTagObservation?> ReadAsync(
        string tag,
        CancellationToken cancellationToken)
    {
        return await ReadCoreAsync(ProtectedRepositoryRemoteUrl, tag, allowLocalFileUrl: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a local bare-repository fixture. This is a test-only seam and must never be used by the production producer.
    /// </summary>
    internal Task<ProtectedReleaseRemoteTagObservation?> ReadLocalFixtureForTestingAsync(
        string remoteUrl,
        string tag,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The test-only remote must be a file URL.", nameof(remoteUrl));
        }

        return ReadCoreAsync(remoteUrl, tag, allowLocalFileUrl: true, cancellationToken);
    }

    private static async Task<ProtectedReleaseRemoteTagObservation?> ReadCoreAsync(
        string remoteUrl,
        string tag,
        bool allowLocalFileUrl,
        CancellationToken cancellationToken)
    {
        if (!IsSupportedRemoteUrl(remoteUrl, allowLocalFileUrl))
        {
            throw new InvalidDataException("The protected release remote URL is outside the allowed scheme or contains unsupported URL components.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment.Clear();
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            startInfo.Environment["PATH"] = path;
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("credential.helper=");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("credential.interactive=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.askPass=");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("http.extraHeader=");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("http.proxy=");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("protocol.allow=never");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("protocol.https.allow=always");
        if (allowLocalFileUrl)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("protocol.file.allow=always");
        }

        startInfo.ArgumentList.Add("ls-remote");
        startInfo.ArgumentList.Add("--tags");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(remoteUrl);
        startInfo.ArgumentList.Add($"refs/tags/{tag}");
        startInfo.ArgumentList.Add($"refs/tags/{tag}^{{}}");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ProtectedReleaseRemoteTagUnavailableException();
            }
        }
        catch (ProtectedReleaseRemoteTagUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new ProtectedReleaseRemoteTagUnavailableException(exception);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        var standardOutput = ReadBoundedAndTerminateOnFailureAsync(process.StandardOutput, process, timeout);
        var standardError = ReadBoundedAndTerminateOnFailureAsync(process.StandardError, process, timeout);
        try
        {
            var exitTask = process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(exitTask, standardOutput, standardError).ConfigureAwait(false);
            var output = await standardOutput.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new ProtectedReleaseRemoteTagUnavailableException();
            }

            return ParseRemoteTagAdvertisement(output, tag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException exception)
        {
            TryKill(process);
            throw new ProtectedReleaseRemoteTagUnavailableException(exception);
        }
        catch (ProtectedReleaseRemoteTagUnavailableException)
        {
            TryKill(process);
            throw;
        }
        catch (IOException exception)
        {
            TryKill(process);
            throw new ProtectedReleaseRemoteTagUnavailableException(exception);
        }
    }

    private static bool IsSupportedRemoteUrl(string? remoteUrl, bool allowLocalFileUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)
            || !Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0)
        {
            return false;
        }

        return (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(uri.Host))
            || (allowLocalFileUrl
                && string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase));
    }

    internal static ProtectedReleaseRemoteTagObservation? ParseRemoteTagAdvertisement(string output, string tag)
    {
        var expectedRef = $"refs/tags/{tag}";
        string? tagObjectId = null;
        string? peeledCommit = null;
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            var separator = line.IndexOf('\t');
            if (separator <= 0)
            {
                throw new InvalidDataException("The protected release remote returned a malformed tag advertisement.");
            }

            var objectId = line[..separator];
            var reference = line[(separator + 1)..];
            var isTagObject = string.Equals(reference, expectedRef, StringComparison.Ordinal);
            var isPeeledCommit = string.Equals(reference, expectedRef + "^{}", StringComparison.Ordinal);
            if (!isTagObject && !isPeeledCommit)
            {
                continue;
            }

            if (!IsCanonicalGitObjectId(objectId))
            {
                throw new InvalidDataException("The protected release remote returned a noncanonical Git object ID.");
            }

            if (isTagObject)
            {
                if (tagObjectId is not null)
                {
                    throw new InvalidDataException("The protected release remote returned the exact tag ref more than once.");
                }

                tagObjectId = objectId;
            }
            else
            {
                if (peeledCommit is not null)
                {
                    throw new InvalidDataException("The protected release remote returned the peeled tag ref more than once.");
                }

                peeledCommit = objectId;
            }
        }

        return tagObjectId is null ? null : new ProtectedReleaseRemoteTagObservation(tagObjectId, peeledCommit);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return builder.ToString();
            }

            if (builder.Length + count > MaximumOutputCharacters)
            {
                throw new InvalidDataException("The protected release remote response exceeded its output bound.");
            }

            builder.Append(buffer, 0, count);
        }
    }

    private static async Task<string> ReadBoundedAndTerminateOnFailureAsync(
        StreamReader reader,
        Process process,
        CancellationTokenSource timeout)
    {
        try
        {
            return await ReadBoundedAsync(reader, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            timeout.Cancel();
            throw;
        }
    }

    private static bool IsCanonicalGitObjectId(string value) =>
        value.Length is 40 or 64
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
