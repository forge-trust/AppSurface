using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Planner;

/// <summary>
/// Identifies the pull request and subject job expected by the trusted controller.
/// </summary>
/// <param name="Repository">Canonical base repository in <c>owner/name</c> form.</param>
/// <param name="EventName">Validated workflow event name.</param>
/// <param name="WorkflowId">Controller-owned workflow identity.</param>
/// <param name="SubjectJobId">Controller-owned subject job identity.</param>
/// <param name="RunIdentity">Controller-owned numeric repository, PR, run, and attempt identity.</param>
public sealed record EvidencePullRequestGateExpectedIdentity(
    string Repository,
    string EventName,
    string WorkflowId,
    string SubjectJobId,
    EvidencePullRequestRunIdentity RunIdentity);

/// <summary>
/// Reports one fresh PR view and the authoritative provenance of its subject job.
/// </summary>
/// <param name="Repository">Base repository containing the pull request.</param>
/// <param name="EventName">Event that created the workflow run.</param>
/// <param name="BaseRevision">Current target-branch commit ID.</param>
/// <param name="HeadRevision">Current pull request head commit ID.</param>
/// <param name="WorkflowId">Workflow associated with the subject job.</param>
/// <param name="RunIdentity">Authoritative PR, repository, run, and attempt identity.</param>
/// <param name="SubjectJobId">Subject job identity.</param>
/// <param name="SubjectJobHeadRevision">Commit checked out by the subject job.</param>
/// <param name="SubjectJobConclusion">Authoritative terminal job conclusion.</param>
/// <param name="SubjectEnvelopeAttested">Whether an independent trusted observation confirmed the subject job's isolation envelope.</param>
public sealed record EvidencePullRequestGateAuthoritySnapshot(
    string Repository,
    string EventName,
    string BaseRevision,
    string HeadRevision,
    string WorkflowId,
    EvidencePullRequestRunIdentity RunIdentity,
    string SubjectJobId,
    string SubjectJobHeadRevision,
    string SubjectJobConclusion,
    bool SubjectEnvelopeAttested = false);

/// <summary>Describes one matched trusted policy rule in a bounded gate result.</summary>
/// <param name="Id">Trusted policy rule identifier, or the bounded conservative-fallback identifier.</param>
/// <param name="Pattern">Trusted policy pattern, or a fixed conservative-fallback description.</param>
/// <param name="ProfileId">Trusted profile selected by the rule.</param>
public sealed record EvidencePullRequestGateRuleSummary(string Id, string Pattern, string ProfileId);

/// <summary>Describes why one trusted obligation was selected.</summary>
/// <param name="Id">Trusted obligation identifier.</param>
/// <param name="RiskClass">Trusted obligation risk class.</param>
/// <param name="Rationale">Trusted policy rationale, bounded for rendering.</param>
public sealed record EvidencePullRequestGateObligationRationale(string Id, string RiskClass, string Rationale);

/// <summary>
/// Bounded rendering data derived from the plan independently verified against trusted policy and Git.
/// </summary>
/// <param name="ProfileId">Selected profile identifier from the verified plan.</param>
/// <param name="SelectionRationale">Fixed explanation of direct-rule or conservative-fallback selection.</param>
/// <param name="MatchedRules">Bounded matched-rule details from trusted policy.</param>
/// <param name="SelectedObligationIds">Bounded identifiers selected by the verified profile.</param>
/// <param name="ClosedObligationIds">Bounded verified closure identifiers that are also selected by the verified profile.</param>
/// <param name="MissingObligationIds">Bounded selected identifiers not independently closed by validated producer assertions.</param>
/// <param name="ObligationRationales">Bounded trusted rationale for selected obligations.</param>
/// <param name="BaseRevisionShort">First 12 characters of the verified base object ID.</param>
/// <param name="HeadRevisionShort">First 12 characters of the verified head object ID.</param>
/// <param name="OmittedMatchedRuleCount">Number of matched rules omitted from this rendering summary.</param>
/// <param name="OmittedSelectedObligationCount">Number of selected obligation IDs omitted from this summary.</param>
/// <param name="OmittedClosedObligationCount">Number of closed obligation IDs omitted from this summary.</param>
/// <param name="OmittedMissingObligationCount">Number of missing obligation IDs omitted from this summary.</param>
/// <param name="OmittedObligationRationaleCount">Number of obligation rationales omitted from this summary.</param>
/// <param name="ProfileHasEvidence">Whether the verified profile selects a resource, producer, or obligation.</param>
public sealed record EvidencePullRequestGateSummary(
    string ProfileId,
    string SelectionRationale,
    IReadOnlyList<EvidencePullRequestGateRuleSummary> MatchedRules,
    IReadOnlyList<string> SelectedObligationIds,
    IReadOnlyList<string> ClosedObligationIds,
    IReadOnlyList<string> MissingObligationIds,
    IReadOnlyList<EvidencePullRequestGateObligationRationale> ObligationRationales,
    string BaseRevisionShort,
    string HeadRevisionShort,
    int OmittedMatchedRuleCount,
    int OmittedSelectedObligationCount,
    int OmittedClosedObligationCount,
    int OmittedMissingObligationCount,
    int OmittedObligationRationaleCount,
    bool ProfileHasEvidence);

/// <summary>
/// Provides an authoritative, fresh PR revision and subject-job provenance observation.
/// </summary>
public interface IEvidencePullRequestGateAuthorityProvider
{
    /// <summary>
    /// Reads the current PR target/head and the run/job association for the expected controller context.
    /// </summary>
    /// <param name="expectedIdentity">Identity captured by the trusted controller.</param>
    /// <param name="cancellationToken">Cancellation for the authority lookup.</param>
    /// <returns>A fresh observation, or <see langword="null"/> when it is unavailable.</returns>
    Task<EvidencePullRequestGateAuthoritySnapshot?> ReadFreshAsync(
        EvidencePullRequestGateExpectedIdentity expectedIdentity,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Verifies the bytes behind manifest artifact metadata using the trusted extracted-artifact root.
/// </summary>
/// <remarks>
/// Implementations must open artifacts without following links, enforce the declared artifact roots,
/// and hash the bytes actually read. Returning <see langword="true"/> asserts that every artifact
/// record in the manifest matches its declared logical slot, media type, length, and digest. The gate
/// never treats manifest metadata alone as byte proof.
/// </remarks>
public interface IEvidencePullRequestGateArtifactVerifier
{
    /// <summary>Verifies artifact bytes associated with one independently verified plan.</summary>
    /// <param name="trustedArtifactHandoffRootPath">Controller-owned extracted-artifact handoff directory.</param>
    /// <param name="verifiedPlan">Plan already recreated from trusted policy and exact Git objects.</param>
    /// <param name="candidateManifest">Bounded manifest whose artifact bytes must be checked.</param>
    /// <param name="cancellationToken">Cancellation for bounded no-follow reads.</param>
    /// <returns><see langword="true"/> only when all artifact records are confirmed from bytes.</returns>
    Task<bool> VerifyArtifactsAsync(
        string trustedArtifactHandoffRootPath,
        EvidencePlan verifiedPlan,
        EvidenceManifest candidateManifest,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Verifies declared artifact bytes by opening every path beneath a trusted handoff root without following links.
/// </summary>
/// <remarks>
/// This implementation requires Linux x64 or arm64 with <c>openat2</c> and <c>statx</c>. It fails closed on other
/// platforms or when those kernel facilities are unavailable. The supplied root must be a controller-owned
/// directory containing one subdirectory per producer, matching the EvidenceHost artifact layout.
/// </remarks>
public sealed class EvidencePullRequestGateNoFollowArtifactVerifier : IEvidencePullRequestGateArtifactVerifier
{
    private const long OpenAt2SyscallNumber = 437;
    private const ulong OpenReadOnly = 0;
    private const ulong OpenCloseOnExec = 0x0008_0000;
    private const ulong OpenNoFollowX64 = 0x0002_0000;
    private const ulong OpenNoFollowArm64 = 0x0000_8000;
    private const ulong OpenNonBlocking = 0x0000_0800;
    private const ulong OpenNoControllingTerminal = 0x0000_0100;
    private const ulong OpenDirectoryX64 = 0x0001_0000;
    private const ulong OpenDirectoryArm64 = 0x0000_4000;
    private static ulong OpenNoFollow => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? OpenNoFollowArm64 : OpenNoFollowX64;
    private static ulong OpenDirectory => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? OpenDirectoryArm64 : OpenDirectoryX64;
    private const ulong ResolveNoMagicLinks = 0x02;
    private const ulong ResolveNoSymlinks = 0x04;
    private const ulong ResolveBeneath = 0x08;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x07ff;
    private const uint StatxRequiredMask = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 6) | (1 << 7) | (1 << 8) | (1 << 9);
    private const ushort FileTypeMask = 0xf000;
    private const ushort RegularFileType = 0x8000;
    private const ushort DirectoryFileType = 0x4000;
    private const int MaximumFiles = EvidenceNoFollowArtifactExtractionLimits.MaximumAllowedFileCount;
    private const long MaximumTotalBytes = EvidenceNoFollowArtifactExtractionLimits.MaximumAllowedTotalBytes;
    private const int StreamBufferSize = 81_920;

    /// <inheritdoc />
    public async Task<bool> VerifyArtifactsAsync(
        string trustedArtifactHandoffRootPath,
        EvidencePlan verifiedPlan,
        EvidenceManifest candidateManifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedArtifactHandoffRootPath);
        ArgumentNullException.ThrowIfNull(verifiedPlan);
        ArgumentNullException.ThrowIfNull(candidateManifest);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return false;
        }

        try
        {
            using var rootHandle = OpenTrustedRoot(trustedArtifactHandoffRootPath);
            var rootSnapshot = ReadSnapshot(rootHandle);
            if ((rootSnapshot.Mode & FileTypeMask) != DirectoryFileType)
            {
                return false;
            }

            var producerResults = candidateManifest.ProducerResults
                .GroupBy(static result => result.ProducerId, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
            if (producerResults.Count != candidateManifest.ProducerResults.Count)
            {
                return false;
            }

            var verifiedFiles = 0;
            long totalBytes = 0;
            var identities = new HashSet<FileIdentity>();
            foreach (var producer in verifiedPlan.Profile.Producers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!producerResults.TryGetValue(producer.Id, out var result)
                    || !EvidenceArtifactValidation.AreValid(producer, result.Artifacts))
                {
                    return false;
                }

                foreach (var artifact in result.Artifacts ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++verifiedFiles > MaximumFiles
                        || !TryGetDeclaredSlot(producer, artifact, out var slot)
                        || !TryGetCanonicalArtifactPath(producer.Id, artifact.RelativePath, out var relativePath))
                    {
                        return false;
                    }

                    using var fileHandle = OpenRelative(rootHandle, relativePath);
                    var before = ReadSnapshot(fileHandle);
                    if ((before.Mode & FileTypeMask) != RegularFileType
                        || before.LinkCount != 1
                        || before.Size > long.MaxValue
                        || (long)before.Size != artifact.LengthBytes
                        || artifact.LengthBytes > slot.MaximumBytes
                        || !identities.Add(before.Identity)
                        || artifact.LengthBytes > MaximumTotalBytes - totalBytes)
                    {
                        return false;
                    }

                    totalBytes += artifact.LengthBytes;
                    var actualDigest = await HashExactBytesAsync(fileHandle, artifact.LengthBytes, cancellationToken).ConfigureAwait(false);
                    var after = ReadSnapshot(fileHandle);
                    if (!before.Equals(after)
                        || !string.Equals(actualDigest, artifact.Sha256, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return producerResults.Keys.All(producerId => verifiedPlan.Profile.Producers.Any(producer => producer.Id == producerId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryGetDeclaredSlot(
        EvidenceProducerDeclaration producer,
        EvidenceArtifactResult artifact,
        out EvidenceArtifactSlot slot)
    {
        slot = producer.ArtifactSlots.FirstOrDefault(candidate => candidate.LogicalName == artifact.LogicalName)!;
        if (slot is null
            || artifact.LengthBytes < 0
            || artifact.LengthBytes > slot.MaximumBytes
            || !string.Equals(artifact.MediaType, slot.MediaType, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            EvidenceArtifactValidation.ValidatePathForSlot(slot, artifact.RelativePath);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryGetCanonicalArtifactPath(string producerId, string relativePath, out string artifactPath)
    {
        artifactPath = string.Empty;
        if (producerId.Length == 0
            || relativePath.Length == 0
            || producerId.Contains('\\')
            || relativePath.Contains('\\')
            || producerId.Any(char.IsControl)
            || relativePath.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            artifactPath = $"{producerId}/{relativePath}";
            var normalized = EvidenceArtifactValidation.NormalizeRelativePath(artifactPath);
            return string.Equals(normalized, artifactPath, StringComparison.Ordinal)
                && System.Text.Encoding.UTF8.GetByteCount(artifactPath) <= EvidenceGitChangeCapture.MaximumPathBytes;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<string> HashExactBytesAsync(
        SafeFileHandle fileHandle,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[StreamBufferSize];
        long total = 0;
        while (true)
        {
            var read = await RandomAccess.ReadAsync(fileHandle, buffer.AsMemory(), total, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (read > expectedLength - total)
            {
                throw new InvalidDataException("Artifact bytes exceed the declared length.");
            }

            total += read;
            hash.AppendData(buffer, 0, read);
        }

        if (total != expectedLength)
        {
            throw new InvalidDataException("Artifact bytes do not match the declared length.");
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static SafeFileHandle OpenTrustedRoot(string rootPath)
    {
        var fullPath = Path.GetFullPath(rootPath);
        if (!Path.IsPathFullyQualified(fullPath) || !fullPath.StartsWith(Path.DirectorySeparatorChar))
        {
            throw new ArgumentException("The artifact handoff root must be an absolute Linux path.", nameof(rootPath));
        }

        var anchorDescriptor = OpenDirectoryPath("/");
        using var anchor = new SafeFileHandle(new IntPtr(anchorDescriptor), ownsHandle: true);
        var relativeRoot = fullPath[1..];
        if (relativeRoot.Length == 0)
        {
            return OpenDirectoryPathHandle("/");
        }

        var how = new LinuxOpenHow
        {
            Flags = OpenReadOnly | OpenCloseOnExec | OpenNoFollow | OpenNonBlocking | OpenNoControllingTerminal | OpenDirectory,
            Resolve = ResolveBeneath | ResolveNoSymlinks | ResolveNoMagicLinks,
        };
        return new SafeFileHandle(new IntPtr(InvokeOpenAt2(anchor.DangerousGetHandle().ToInt32(), relativeRoot, ref how)), ownsHandle: true);
    }

    private static SafeFileHandle OpenRelative(SafeFileHandle rootHandle, string relativePath)
    {
        var how = new LinuxOpenHow
        {
            Flags = OpenReadOnly | OpenCloseOnExec | OpenNoFollow | OpenNonBlocking | OpenNoControllingTerminal,
            Resolve = ResolveBeneath | ResolveNoSymlinks | ResolveNoMagicLinks,
        };
        return new SafeFileHandle(new IntPtr(InvokeOpenAt2(rootHandle.DangerousGetHandle().ToInt32(), relativePath, ref how)), ownsHandle: true);
    }

    private static int OpenDirectoryPath(string path)
    {
        var descriptor = OpenDirectoryNative(path, OpenReadOnly | OpenCloseOnExec | OpenNoFollow | OpenDirectory);
        if (descriptor < 0)
        {
            throw new IOException("The trusted artifact root could not be opened.");
        }

        return descriptor;
    }

    private static SafeFileHandle OpenDirectoryPathHandle(string path) =>
        new(new IntPtr(OpenDirectoryPath(path)), ownsHandle: true);

    private static int InvokeOpenAt2(int rootDescriptor, string relativePath, ref LinuxOpenHow how)
    {
        var pathPointer = Marshal.StringToCoTaskMemUTF8(relativePath);
        try
        {
            var result = Syscall(OpenAt2SyscallNumber, rootDescriptor, pathPointer, ref how, (nuint)Marshal.SizeOf<LinuxOpenHow>());
            if (result >= 0 && result <= int.MaxValue)
            {
                return (int)result;
            }

            throw new IOException("Linux refused the no-follow artifact path.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    private static FileSnapshot ReadSnapshot(SafeFileHandle handle)
    {
        if (Statx(handle.DangerousGetHandle().ToInt32(), string.Empty, AtEmptyPath, StatxBasicStats, out var statx) != 0
            || (statx.Mask & StatxRequiredMask) != StatxRequiredMask)
        {
            throw new IOException("Linux could not inspect the opened artifact safely.");
        }

        return new FileSnapshot(
            statx.DeviceMajor,
            statx.DeviceMinor,
            statx.Inode,
            statx.Mode,
            statx.LinkCount,
            statx.Size,
            statx.ModifyTime.Seconds,
            statx.ModifyTime.Nanoseconds,
            statx.ChangeTime.Seconds,
            statx.ChangeTime.Nanoseconds);
    }

    private static int OpenDirectoryNative(string path, ulong flags) => OpenNative(path, checked((int)flags));

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long Syscall(long number, int directoryDescriptor, IntPtr path, ref LinuxOpenHow how, nuint size);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out LinuxStatx statx);

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxOpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        private int _reserved;
    }

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct LinuxStatx
    {
        public uint Mask;
        private uint _blockSize;
        private ulong _attributes;
        public uint LinkCount;
        private uint _userId;
        private uint _groupId;
        public ushort Mode;
        private ushort _spare0;
        public ulong Inode;
        public ulong Size;
        private ulong _blocks;
        private ulong _attributesMask;
        private LinuxStatxTimestamp _accessTime;
        private LinuxStatxTimestamp _birthTime;
        public LinuxStatxTimestamp ChangeTime;
        public LinuxStatxTimestamp ModifyTime;
        private uint _rdevMajor;
        private uint _rdevMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
        private ulong _mountId;
        private uint _dioMemoryAlignment;
        private uint _dioOffsetAlignment;
        private ulong _spare1;
        private ulong _spare2;
        private ulong _spare3;
        private ulong _spare4;
        private ulong _spare5;
        private ulong _spare6;
        private ulong _spare7;
        private ulong _spare8;
        private ulong _spare9;
        private ulong _spare10;
        private ulong _spare11;
        private ulong _spare12;
    }

    private readonly record struct FileIdentity(uint DeviceMajor, uint DeviceMinor, ulong Inode);

    private readonly record struct FileSnapshot(
        uint DeviceMajor,
        uint DeviceMinor,
        ulong Inode,
        ushort Mode,
        uint LinkCount,
        ulong Size,
        long ModifySeconds,
        uint ModifyNanoseconds,
        long ChangeSeconds,
        uint ChangeNanoseconds)
    {
        public FileIdentity Identity => new(DeviceMajor, DeviceMinor, Inode);
    }
}

/// <summary>
/// Returns a bounded PR-gate result without including untrusted manifest or provider diagnostics.
/// </summary>
/// <param name="IsEligible">Whether the current PR may consume the evidence as a passing gate.</param>
/// <param name="Code">Stable diagnostic code.</param>
/// <param name="Diagnostic">Fixed, secret-safe diagnostic text.</param>
/// <param name="Summary">Bounded summary after independent plan verification; absent on pre-plan failures.</param>
public sealed record EvidencePullRequestGateVerificationResult(
    bool IsEligible,
    string Code,
    string Diagnostic,
    EvidencePullRequestGateSummary? Summary = null);

/// <summary>
/// Independently verifies revision-bound PR evidence at the trusted controller boundary.
/// </summary>
/// <remarks>
/// The policy and Git object store are trusted inputs supplied separately from the untrusted plan
/// and manifest. The verifier recreates the v2 plan from exact Git objects, checks manifest
/// self-consistency and every selected obligation, then reads authoritative PR and job state just
/// before returning. It never uses the plan's policy snapshot or revision values as authority.
/// </remarks>
public static class EvidencePullRequestGateVerifier
{
    /// <summary>Maximum canonical plan or manifest size accepted by this verifier.</summary>
    public const int MaximumContractBytes = 4 * 1024 * 1024;

    /// <summary>Maximum aggregate UTF-16 text units traversed in an input contract.</summary>
    public const int MaximumContractTextUnits = 1 * 1024 * 1024;

    /// <summary>Maximum time allowed for the final authoritative provider lookup.</summary>
    public const int MaximumAuthorityReadSeconds = 10;

    /// <summary>Maximum time allowed for trusted no-follow artifact byte verification.</summary>
    public const int MaximumArtifactVerificationSeconds = 120;

    /// <summary>Maximum number of identifiers or rationale entries returned in a rendering summary.</summary>
    public const int MaximumSummaryItems = 64;

    private static readonly EvidencePullRequestGateVerificationResult InvalidIdentity =
        Failure("ASEVG001", "The trusted controller identity is invalid.");
    private static readonly EvidencePullRequestGateVerificationResult InvalidPlan =
        Failure("ASEVG002", "The supplied plan is missing, oversized, or is not revision-bound v2 evidence.");
    private static readonly EvidencePullRequestGateVerificationResult PlanMismatch =
        Failure("ASEVG003", "The plan does not match the trusted policy and exact Git change.");
    private static readonly EvidencePullRequestGateVerificationResult InvalidManifest =
        Failure("ASEVG004", "The manifest is invalid, incomplete, or does not close every selected obligation and cleanup requirement.");
    private static readonly EvidencePullRequestGateVerificationResult NotGateEligible =
        Failure("ASEVG005", "The evidence claim is observational, out of scope, or not eligible for a pull-request gate.");
    private static readonly EvidencePullRequestGateVerificationResult AuthorityUnavailable =
        Failure("ASEVG006", "Fresh pull-request or subject-job authority could not be verified.");
    private static readonly EvidencePullRequestGateVerificationResult AuthorityMismatch =
        Failure("ASEVG007", "The current pull request or subject-job provenance does not match this evidence run.");
    private static readonly EvidencePullRequestGateVerificationResult Cancelled =
        Failure("ASEVG008", "Pull-request evidence verification was cancelled.");
    private static readonly EvidencePullRequestGateVerificationResult ArtifactVerifierUnavailable =
        Failure("ASEVG009", "Trusted artifact byte verification is unavailable for this nonempty evidence profile.");
    private static readonly EvidencePullRequestGateVerificationResult ArtifactVerificationFailed =
        Failure("ASEVG010", "One or more extracted evidence artifacts could not be verified from bytes.");
    private static readonly EvidencePullRequestGateVerificationResult Eligible =
        new(true, "ASEVG000", "The current pull request has complete, revision-bound, gate-eligible evidence.");

    /// <summary>
    /// Verifies untrusted plan and manifest values against controller-owned policy, Git objects,
    /// and a final authoritative observation of the pull request and subject job.
    /// </summary>
    /// <param name="planner">Trusted evidence planner.</param>
    /// <param name="trustedBaseOwnedPolicy">Policy loaded from the protected base revision.</param>
    /// <param name="trustedGitObjectStorePath">Trusted object store containing the exact PR commits.</param>
    /// <param name="expectedIdentity">PR, workflow run, and subject-job identity captured by the controller.</param>
    /// <param name="authorityProvider">Provider for fresh current PR revisions and job provenance.</param>
    /// <param name="trustedArtifactHandoffRootPath">Trusted extracted-artifact root, or <see langword="null"/> only for an empty profile.</param>
    /// <param name="artifactVerifier">Trusted no-follow artifact verifier; required for nonempty profiles.</param>
    /// <param name="untrustedPlan">Plan received through the subject-job handoff.</param>
    /// <param name="untrustedManifest">Manifest received through the subject-job handoff.</param>
    /// <param name="cancellationToken">Cancellation for Git and authority operations.</param>
    /// <returns>A bounded result with fixed diagnostics and no raw untrusted output.</returns>
    public static async Task<EvidencePullRequestGateVerificationResult> VerifyAsync(
        EvidencePlanner planner,
        EvidencePolicy trustedBaseOwnedPolicy,
        string trustedGitObjectStorePath,
        EvidencePullRequestGateExpectedIdentity expectedIdentity,
        IEvidencePullRequestGateAuthorityProvider authorityProvider,
        string? trustedArtifactHandoffRootPath,
        IEvidencePullRequestGateArtifactVerifier? artifactVerifier,
        EvidencePlan untrustedPlan,
        EvidenceManifest untrustedManifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(trustedBaseOwnedPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedGitObjectStorePath);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ArgumentNullException.ThrowIfNull(authorityProvider);

        if (!HasValidExpectedIdentity(expectedIdentity))
        {
            return InvalidIdentity;
        }

        try
        {
            if (untrustedPlan is null
                || untrustedManifest is null
                || !string.Equals(untrustedPlan.ContractVersion, "2.0", StringComparison.Ordinal)
                || !HasValidRevision(untrustedPlan.BaseRevision)
                || !HasValidRevision(untrustedPlan.HeadRevision)
                || untrustedPlan.BaseRevision!.Length != untrustedPlan.HeadRevision!.Length
                || !IsBoundedPlan(untrustedPlan)
                || !IsBoundedManifest(untrustedManifest))
            {
                return InvalidPlan;
            }
        }
        catch (Exception)
        {
            return InvalidPlan;
        }

        if (!Equals(untrustedPlan.PullRequestRunIdentity, expectedIdentity.RunIdentity))
        {
            return AuthorityMismatch;
        }

        if (!Equals(untrustedManifest.PullRequestRunIdentity, expectedIdentity.RunIdentity))
        {
            return InvalidManifest;
        }

        try
        {
            await EvidenceRevisionPlanBuilder.VerifyAsync(
                planner,
                trustedBaseOwnedPolicy,
                trustedGitObjectStorePath,
                untrustedPlan,
                cancellationToken,
                expectedIdentity.RunIdentity).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled;
        }
        catch (Exception)
        {
            return PlanMismatch;
        }

        bool manifestIsConsistent;
        try
        {
            manifestIsConsistent = EvidenceManifestBuilder.Verify(untrustedPlan, untrustedManifest);
        }
        catch (Exception)
        {
            return InvalidManifest;
        }

        if (!manifestIsConsistent)
        {
            return WithSummary(InvalidManifest, untrustedPlan, trustedBaseOwnedPolicy, untrustedManifest, false, false);
        }

        if (untrustedManifest.ClaimKind == EvidenceClaimKind.ObservationOnly)
        {
            return WithSummary(NotGateEligible, untrustedPlan, trustedBaseOwnedPolicy, untrustedManifest, true, false);
        }

        var hasEmptyProfile = IsEmptyProfile(untrustedPlan.Profile);
        var artifactsVerified = hasEmptyProfile;
        if (!hasEmptyProfile)
        {
            if (string.IsNullOrWhiteSpace(trustedArtifactHandoffRootPath))
            {
                return WithSummary(ArtifactVerifierUnavailable, untrustedPlan, trustedBaseOwnedPolicy, untrustedManifest, true, false);
            }

            if (artifactVerifier is null)
            {
                return WithSummary(ArtifactVerifierUnavailable, untrustedPlan, trustedBaseOwnedPolicy, untrustedManifest, true, false);
            }

            using var artifactDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            artifactDeadline.CancelAfter(TimeSpan.FromSeconds(MaximumArtifactVerificationSeconds));
            try
            {
                artifactsVerified = await artifactVerifier.VerifyArtifactsAsync(
                        trustedArtifactHandoffRootPath,
                        untrustedPlan,
                        untrustedManifest,
                        artifactDeadline.Token)
                    .WaitAsync(TimeSpan.FromSeconds(MaximumArtifactVerificationSeconds), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return WithSummary(Cancelled, untrustedPlan, trustedBaseOwnedPolicy, untrustedManifest, true, false);
            }
            catch (Exception)
            {
                return WithSummary(ArtifactVerificationFailed, untrustedPlan, trustedBaseOwnedPolicy, untrustedManifest, true, false);
            }

            if (!artifactsVerified)
            {
                return WithSummary(ArtifactVerificationFailed, untrustedPlan, trustedBaseOwnedPolicy, untrustedManifest, true, false);
            }
        }

        if (!HasCompleteGateEvidence(untrustedPlan, untrustedManifest))
        {
            return WithSummary(InvalidManifest, untrustedPlan, trustedBaseOwnedPolicy, untrustedManifest, true, artifactsVerified);
        }

        EvidencePullRequestGateAuthoritySnapshot? authority;
        using (var authorityDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            authorityDeadline.CancelAfter(TimeSpan.FromSeconds(MaximumAuthorityReadSeconds));
            try
            {
                authority = await authorityProvider.ReadFreshAsync(expectedIdentity, authorityDeadline.Token)
                    .WaitAsync(TimeSpan.FromSeconds(MaximumAuthorityReadSeconds), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Cancelled;
            }
            catch (Exception)
            {
                return WithSummary(AuthorityUnavailable, untrustedPlan, trustedBaseOwnedPolicy, untrustedManifest, true, artifactsVerified);
            }
        }

        if (authority is null)
        {
            return WithSummary(AuthorityUnavailable, untrustedPlan, trustedBaseOwnedPolicy, untrustedManifest, true, artifactsVerified);
        }

        return WithSummary(
            MatchesCurrentAuthority(expectedIdentity, authority, untrustedPlan, untrustedManifest) ? Eligible : AuthorityMismatch,
            untrustedPlan,
            trustedBaseOwnedPolicy,
            untrustedManifest,
            true,
            artifactsVerified);
    }

    private static EvidencePullRequestGateVerificationResult WithSummary(
        EvidencePullRequestGateVerificationResult result,
        EvidencePlan verifiedPlan,
        EvidencePolicy trustedPolicy,
        EvidenceManifest candidateManifest,
        bool manifestIsConsistent,
        bool artifactsVerified) =>
        result with
        {
            Summary = CreateSummary(verifiedPlan, trustedPolicy, candidateManifest, manifestIsConsistent, artifactsVerified),
        };

    private static EvidencePullRequestGateSummary CreateSummary(
        EvidencePlan verifiedPlan,
        EvidencePolicy trustedPolicy,
        EvidenceManifest candidateManifest,
        bool manifestIsConsistent,
        bool artifactsVerified)
    {
        var selected = verifiedPlan.Profile.Obligations
            .Select(static obligation => obligation.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var closed = manifestIsConsistent && artifactsVerified
            ? verifiedPlan.Profile.Obligations
                .Where(obligation => obligation.RequiredProducerIds.All(producerId =>
                    candidateManifest.ProducerResults.Any(result => result.ProducerId == producerId
                        && result.Outcome == EvidenceProducerOutcome.Passed
                        && result.SatisfiedAssertionIds.Contains(obligation.RequiredAssertionId, StringComparer.Ordinal))))
                .Select(static obligation => obligation.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray()
            : [];
        var missing = selected.Except(closed, StringComparer.Ordinal).OrderBy(static id => id, StringComparer.Ordinal).ToArray();

        var matchedRules = verifiedPlan.MatchedRuleIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .Select(id => CreateRuleSummary(id, trustedPolicy))
            .Where(static rule => rule is not null)
            .Select(static rule => rule!)
            .ToArray();
        var rationales = verifiedPlan.Profile.Obligations
            .OrderBy(static obligation => obligation.Id, StringComparer.Ordinal)
            .Select(static obligation => new EvidencePullRequestGateObligationRationale(
                obligation.Id,
                obligation.RiskClass,
                Truncate(obligation.Rationale, 256)))
            .ToArray();

        var fallbackId = $"conservative:{trustedPolicy.ConservativeProfileId}";
        return new EvidencePullRequestGateSummary(
            verifiedPlan.Profile.Id,
            verifiedPlan.MatchedRuleIds.Contains(fallbackId, StringComparer.Ordinal) ? "conservative-fallback" : "matched-policy-rules",
            TakeBounded(matchedRules, out var omittedRules),
            TakeBounded(selected, out var omittedSelected),
            TakeBounded(closed, out var omittedClosed),
            TakeBounded(missing, out var omittedMissing),
            TakeBounded(rationales, out var omittedRationales),
            verifiedPlan.BaseRevision![..12],
            verifiedPlan.HeadRevision![..12],
            omittedRules,
            omittedSelected,
            omittedClosed,
            omittedMissing,
            omittedRationales,
            !IsEmptyProfile(verifiedPlan.Profile));
    }

    private static EvidencePullRequestGateRuleSummary? CreateRuleSummary(string ruleId, EvidencePolicy trustedPolicy)
    {
        var rule = trustedPolicy.Rules.FirstOrDefault(candidate => string.Equals(candidate.Id, ruleId, StringComparison.Ordinal));
        if (rule is not null)
        {
            return new EvidencePullRequestGateRuleSummary(rule.Id, Truncate(rule.Pattern, 256), rule.ProfileId);
        }

        var fallbackId = $"conservative:{trustedPolicy.ConservativeProfileId}";
        return string.Equals(ruleId, fallbackId, StringComparison.Ordinal)
            ? new EvidencePullRequestGateRuleSummary(ruleId, "conservative fallback", trustedPolicy.ConservativeProfileId)
            : null;
    }

    private static IReadOnlyList<TValue> TakeBounded<TValue>(IReadOnlyList<TValue> values, out int omittedCount)
    {
        omittedCount = Math.Max(0, values.Count - MaximumSummaryItems);
        return Array.AsReadOnly(values.Take(MaximumSummaryItems).ToArray());
    }

    private static string Truncate(string value, int maximumLength) => value.Length <= maximumLength
        ? value
        : value[..maximumLength];

    private static bool IsEmptyProfile(EvidenceProfile profile) =>
        profile.Resources.Count == 0 && profile.Producers.Count == 0 && profile.Obligations.Count == 0;

    private static bool HasCompleteGateEvidence(EvidencePlan plan, EvidenceManifest manifest)
    {
        if (plan.Profile.Scope != EvidenceProfileScope.Targeted
            || manifest.ContractVersion != "2.0"
            || manifest.ExecutionVerdict != EvidenceExecutionVerdict.Passed
            || manifest.Eligibility != EvidenceClaimEligibility.PullRequestGate
            || manifest.EnvelopeStatus is EvidenceEnvelopeStatus.Unavailable or EvidenceEnvelopeStatus.Invalid
            || !manifest.Metrics.CleanupCompleted
            || manifest.Metrics.PlanningMilliseconds < 0
            || manifest.Metrics.ResourceReadinessMilliseconds < 0
            || manifest.Metrics.ProducerMilliseconds < 0
            || manifest.Metrics.CleanupMilliseconds < 0
            || manifest.Metrics.TotalMilliseconds < 0)
        {
            return false;
        }

        var noEvidenceRequired = plan.Profile.Resources.Count == 0
            && plan.Profile.Producers.Count == 0
            && plan.Profile.Obligations.Count == 0;
        if (manifest.EnvelopeStatus != (noEvidenceRequired
                ? EvidenceEnvelopeStatus.NotRequired
                : EvidenceEnvelopeStatus.ValidatedNotAttested))
        {
            return false;
        }

        var requiredClaim = noEvidenceRequired ? EvidenceClaimKind.NoEvidenceRequired : EvidenceClaimKind.TargetedComplete;
        if (manifest.ClaimKind != requiredClaim)
        {
            return false;
        }

        var expectedObligations = plan.Profile.Obligations
            .Select(static obligation => obligation.Id)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        if (!manifest.SelectedObligationIds.SequenceEqual(expectedObligations, StringComparer.Ordinal)
            || !manifest.ClosedObligationIds.SequenceEqual(expectedObligations, StringComparer.Ordinal)
            || manifest.UnmediatedObligationIds.Count != 0)
        {
            return false;
        }

        var resourceResults = manifest.ResourceResults
            .GroupBy(static result => result.ResourceId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        if (resourceResults.Count != plan.Profile.Resources.Count
            || plan.Profile.Resources.Any(resource =>
                !resourceResults.TryGetValue(resource.Id, out var result)
                || result.Outcome != EvidenceResourceOutcome.Ready
                || result.ElapsedMilliseconds < 0))
        {
            return false;
        }

        var producerResults = manifest.ProducerResults
            .GroupBy(static result => result.ProducerId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        if (producerResults.Count != plan.Profile.Producers.Count
            || plan.Profile.Producers.Any(producer =>
                !producerResults.TryGetValue(producer.Id, out var result)
                || result.Outcome != EvidenceProducerOutcome.Passed
                || result.ElapsedMilliseconds < 0
                || !EvidenceArtifactValidation.AreValid(producer, result.Artifacts)))
        {
            return false;
        }

        return plan.Profile.Obligations.All(obligation => obligation.RequiredProducerIds.All(producerId =>
            producerResults.TryGetValue(producerId, out var result)
            && result.Outcome == EvidenceProducerOutcome.Passed
            && result.SatisfiedAssertionIds.Contains(obligation.RequiredAssertionId, StringComparer.Ordinal)));
    }

    private static bool MatchesCurrentAuthority(
        EvidencePullRequestGateExpectedIdentity expected,
        EvidencePullRequestGateAuthoritySnapshot current,
        EvidencePlan plan,
        EvidenceManifest manifest) =>
        current.RunIdentity is not null
        && current.RunIdentity.HeadRepositoryId == current.RunIdentity.RepositoryId
        && string.Equals(current.Repository, expected.Repository, StringComparison.OrdinalIgnoreCase)
        && string.Equals(current.EventName, expected.EventName, StringComparison.Ordinal)
        && Equals(current.RunIdentity, expected.RunIdentity)
        && Equals(plan.PullRequestRunIdentity, current.RunIdentity)
        && Equals(plan.PullRequestRunIdentity, expected.RunIdentity)
        && current.BaseRevision == plan.BaseRevision
        && current.HeadRevision == plan.HeadRevision
        && string.Equals(current.WorkflowId, expected.WorkflowId, StringComparison.Ordinal)
        && string.Equals(current.SubjectJobId, expected.SubjectJobId, StringComparison.Ordinal)
        && current.SubjectJobHeadRevision == plan.HeadRevision
        && (IsEmptyProfile(plan.Profile) || current.SubjectEnvelopeAttested)
        && string.Equals(current.SubjectJobConclusion, "success", StringComparison.OrdinalIgnoreCase);

    private static bool HasValidExpectedIdentity(EvidencePullRequestGateExpectedIdentity identity) =>
        identity.RunIdentity is not null
        && identity.RunIdentity.RepositoryId > 0
        && identity.RunIdentity.HeadRepositoryId > 0
        && identity.RunIdentity.PullRequestNumber > 0
        && identity.RunIdentity.WorkflowRunId > 0
        && identity.RunIdentity.WorkflowRunAttempt > 0
        && IsSafeIdentityText(identity.RunIdentity.TargetBranch, 128)
        && IsSafeIdentityText(identity.Repository, 255)
        && identity.Repository.Count(static character => character == '/') == 1
        && (identity.EventName is "pull_request" or "pull_request_target")
        && IsSafeIdentityText(identity.WorkflowId, 256)
        && IsSafeIdentityText(identity.SubjectJobId, 128);

    private static bool IsSafeIdentityText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl);

    private static bool HasValidRevision(string? revision) =>
        revision is { Length: 40 or 64 }
            && revision.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsBoundedPlan(EvidencePlan plan)
    {
        var budget = new TextBudget();
        return plan.Profile is not null
            && plan.PullRequestRunIdentity is not null
            && IsBoundedRunIdentity(plan.PullRequestRunIdentity, budget)
            && plan.PolicySnapshot is not null
            && plan.ChangedPaths is { Count: <= EvidenceGitChangeCapture.MaximumChangedRecords }
            && plan.MatchedRuleIds is { Count: <= EvidenceGitChangeCapture.MaximumChangedRecords * 2 }
            && IsBoundedPolicy(plan.PolicySnapshot, budget)
            && AddText(budget, plan.ContractVersion, plan.PolicyId, plan.PolicyDigest, plan.DiffDigest, plan.PlanDigest,
                plan.BaseRevision, plan.HeadRevision, plan.SourceDiffDigest, plan.NameStatusDigest)
            && AddMany(budget, plan.MatchedRuleIds, 128)
            && plan.ChangedPaths.All(path => path is not null
                && AddText(budget, path.Path, path.Kind, path.PreviousPath)
                && path.Path.Length <= EvidenceGitChangeCapture.MaximumPathBytes
                && (path.PreviousPath is null || path.PreviousPath.Length <= EvidenceGitChangeCapture.MaximumPathBytes))
            && IsBoundedProfile(plan.Profile, budget)
            && EvidenceCanonicalJson.Serialize(plan).Length <= MaximumContractBytes;
    }

    private static bool IsBoundedManifest(EvidenceManifest manifest)
    {
        var budget = new TextBudget();
        if (manifest.PullRequestRunIdentity is null
            || !IsBoundedRunIdentity(manifest.PullRequestRunIdentity, budget)
            || manifest.ResourceResults is not { Count: <= EvidenceProfileLimits.MaximumResources }
            || manifest.SelectedObligationIds is not { Count: <= EvidenceProfileLimits.MaximumObligations }
            || manifest.ClosedObligationIds is not { Count: <= EvidenceProfileLimits.MaximumObligations }
            || manifest.UnmediatedObligationIds is not { Count: <= EvidenceProfileLimits.MaximumObligations }
            || manifest.ProducerResults is not { Count: <= EvidenceProfileLimits.MaximumProducers }
            || manifest.Metrics is null)
        {
            return false;
        }

        return AddText(budget, manifest.ContractVersion, manifest.PlanDigest, manifest.ManifestDigest,
                manifest.BaseRevision, manifest.HeadRevision, manifest.SourceDiffDigest, manifest.NameStatusDigest,
                manifest.Metrics.CleanupDiagnostic)
            && manifest.ResourceResults.All(result => result is not null
                && AddText(budget, result.ResourceId, result.Diagnostic)
                && result.Diagnostic is null or { Length: <= 512 })
            && AddMany(budget, manifest.SelectedObligationIds, 128)
            && AddMany(budget, manifest.ClosedObligationIds, 128)
            && AddMany(budget, manifest.UnmediatedObligationIds, 128)
            && manifest.ProducerResults.All(result => result is not null
                && AddText(budget, result.ProducerId, result.Diagnostic)
                && result.Diagnostic is null or { Length: <= 512 }
                && result.SatisfiedAssertionIds is { Count: <= 256 }
                && result.Artifacts is { Count: <= 128 }
                && AddMany(budget, result.SatisfiedAssertionIds, 128)
                && result.Artifacts.All(artifact => artifact is not null
                    && AddText(budget, artifact.LogicalName, artifact.RelativePath, artifact.MediaType, artifact.Sha256)))
            && EvidenceCanonicalJson.Serialize(manifest).Length <= MaximumContractBytes;
    }

    private static bool IsBoundedPolicy(EvidencePolicy policy, TextBudget budget) =>
        policy.Profiles is { Count: > 0 and <= 128 }
        && policy.Rules is { Count: <= 10_000 }
        && AddText(budget, policy.Id, policy.Version, policy.ConservativeProfileId)
        && policy.Profiles.All(profile => profile is not null && IsBoundedProfile(profile, budget))
        && policy.Rules.All(rule => rule is not null && AddText(budget, rule.Id, rule.Pattern, rule.ProfileId));

    private static bool IsBoundedRunIdentity(EvidencePullRequestRunIdentity identity, TextBudget budget) =>
        identity.RepositoryId > 0
        && identity.HeadRepositoryId > 0
        && identity.PullRequestNumber > 0
        && identity.WorkflowRunId > 0
        && identity.WorkflowRunAttempt > 0
        && identity.TargetBranch is { Length: > 0 and <= 128 }
        && !identity.TargetBranch.Any(char.IsControl)
        && budget.TryAdd(identity.TargetBranch);

    private static bool IsBoundedProfile(EvidenceProfile profile, TextBudget budget) =>
        profile is not null
        && profile.Resources is { Count: <= EvidenceProfileLimits.MaximumResources }
        && profile.Producers is { Count: <= EvidenceProfileLimits.MaximumProducers }
        && profile.Obligations is { Count: <= EvidenceProfileLimits.MaximumObligations }
        && AddText(budget, profile.Id)
        && profile.Resources.All(resource => resource is not null
            && resource.Requires is { Count: <= EvidenceProfileLimits.MaximumResources }
            && AddText(budget, resource.Id, resource.Readiness)
            && AddMany(budget, resource.Requires, 128))
        && profile.Producers.All(producer => producer is not null
            && producer.RequiredResources is { Count: <= EvidenceProfileLimits.MaximumResources }
            && producer.AssertionIds is { Count: <= 256 }
            && producer.ArtifactSlots is { Count: <= 128 }
            && AddText(budget, producer.Id, producer.Kind, producer.Version)
            && AddMany(budget, producer.RequiredResources, 128)
            && AddMany(budget, producer.AssertionIds, 128)
            && producer.ArtifactSlots.All(slot => slot is not null && AddText(budget, slot.LogicalName, slot.RelativeRoot, slot.MediaType)))
        && profile.Obligations.All(obligation => obligation is not null
            && obligation.RequiredProducerIds is { Count: <= EvidenceProfileLimits.MaximumProducers }
            && AddText(budget, obligation.Id, obligation.RiskClass, obligation.Rationale, obligation.RequiredAssertionId)
            && AddMany(budget, obligation.RequiredProducerIds, 128));

    private static bool AddMany(TextBudget budget, IEnumerable<string>? values, int maximumItemLength)
    {
        if (values is null)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value is null || value.Length > maximumItemLength || !budget.TryAdd(value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AddText(TextBudget budget, params string?[] values)
    {
        foreach (var value in values)
        {
            if (value is not null && (value.Length > EvidenceGitChangeCapture.MaximumPathBytes || !budget.TryAdd(value)))
            {
                return false;
            }
        }

        return true;
    }

    private static EvidencePullRequestGateVerificationResult Failure(string code, string diagnostic) =>
        new(false, code, diagnostic);

    private sealed class TextBudget
    {
        private int _used;

        public bool TryAdd(string value)
        {
            if (value.Length > MaximumContractTextUnits - _used)
            {
                return false;
            }

            _used += value.Length;
            return true;
        }
    }
}
