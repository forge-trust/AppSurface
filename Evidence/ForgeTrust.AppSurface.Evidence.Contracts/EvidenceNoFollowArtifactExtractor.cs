using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>
/// Identifies one untrusted scratch file and its declared evidence artifact destination.
/// </summary>
/// <param name="LogicalName">Declared logical artifact slot name.</param>
/// <param name="SourceRelativePath">Canonical slash-separated path relative to the trusted scratch directory.</param>
/// <param name="DestinationRelativePath">Canonical slash-separated path beneath the evidence writer root and declared slot.</param>
public sealed record EvidenceNoFollowArtifact(
    string LogicalName,
    string SourceRelativePath,
    string DestinationRelativePath);

/// <summary>
/// Lowers the extractor's built-in file, byte, and elapsed-time ceilings for one extraction call.
/// </summary>
/// <remarks>
/// Limits may be reduced by the caller but cannot exceed 64 files, the evidence writer's 256 MiB
/// per-producer total, or two minutes. Each individual artifact is also bounded by its declared slot.
/// </remarks>
public sealed record EvidenceNoFollowArtifactExtractionLimits
{
    /// <summary>The absolute maximum number of source files in one extraction call.</summary>
    public const int MaximumAllowedFileCount = 64;

    /// <summary>The absolute maximum aggregate source size in one extraction call.</summary>
    public const long MaximumAllowedTotalBytes = EvidenceArtifactWriter.MaximumTotalArtifactBytes;

    /// <summary>The absolute maximum wall-clock duration of one extraction call.</summary>
    public static readonly TimeSpan MaximumAllowedDuration = TimeSpan.FromMinutes(2);

    /// <summary>Gets the maximum number of files accepted by one extraction call.</summary>
    public int MaximumFileCount { get; init; } = MaximumAllowedFileCount;

    /// <summary>Gets the maximum aggregate source size accepted by one extraction call.</summary>
    public long MaximumTotalBytes { get; init; } = MaximumAllowedTotalBytes;

    /// <summary>Gets the maximum wall-clock duration of one extraction call.</summary>
    public TimeSpan MaximumDuration { get; init; } = MaximumAllowedDuration;
}

/// <summary>
/// Copies explicitly selected scratch files into declared evidence artifact slots using Linux no-follow opens.
/// </summary>
/// <remarks>
/// The caller must provide an already-open handle to the trusted scratch directory. The extractor requires Linux
/// x64 or arm64 with the <c>openat2</c> syscall and <c>statx</c>; it fails closed when either facility or a required
/// resolution flag is unavailable. It never falls back to caller-path-based source reads.
/// </remarks>
public static class EvidenceNoFollowArtifactExtractor
{
    private const int StreamBufferSize = 81_920;
    private const int MaximumRelativePathBytes = 4_096;
    private const int MaximumSegmentBytes = 255;

    private const long OpenAt2SyscallNumber = 437;

    private const ulong OpenReadOnly = 0;
    private const ulong OpenCloseOnExec = 0x0008_0000;
    private const ulong OpenNoFollowX64 = 0x0002_0000;
    private const ulong OpenNoFollowArm64 = 0x0000_8000;
    private static ulong OpenNoFollow => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? OpenNoFollowArm64 : OpenNoFollowX64;
    private const ulong OpenNonBlocking = 0x0000_0800;
    private const ulong OpenNoControllingTerminal = 0x0000_0100;
    private const ulong ResolveNoMagicLinks = 0x02;
    private const ulong ResolveNoSymlinks = 0x04;
    private const ulong ResolveBeneath = 0x08;

    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x07ff;
    private const uint StatxType = 1 << 0;
    private const uint StatxMode = 1 << 1;
    private const uint StatxLinkCount = 1 << 2;
    private const uint StatxModifyTime = 1 << 6;
    private const uint StatxChangeTime = 1 << 7;
    private const uint StatxInode = 1 << 8;
    private const uint StatxSize = 1 << 9;
    private const uint StatxRequiredMask = StatxType | StatxMode | StatxLinkCount | StatxModifyTime | StatxChangeTime | StatxInode | StatxSize;
    private const ushort FileTypeMask = 0xf000;
    private const ushort RegularFileType = 0x8000;
    private const ushort DirectoryFileType = 0x4000;

    private const int ErrorInvalidArgument = 22;
    private const int ErrorNoSys = 38;
    private const int ErrorTooLarge = 7;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Opens each listed source beneath <paramref name="trustedScratchRoot"/> without following symlinks and streams
    /// its bytes through <see cref="EvidenceArtifactWriter.WriteAsync(string, string, Stream, long, CancellationToken)"/>.
    /// </summary>
    /// <param name="trustedScratchRoot">Already-open handle to the trusted scratch directory. The extractor borrows it for this call.</param>
    /// <param name="writer">Writer that owns the declared destination slots and artifact root.</param>
    /// <param name="artifacts">Explicit source-to-destination mappings. Directories are never enumerated.</param>
    /// <param name="limits">Optional stricter file-count, aggregate-byte, and duration limits.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>The metadata for artifacts completed by this call, in input order.</returns>
    /// <exception cref="PlatformNotSupportedException">The operating system, architecture, kernel, or libc lacks required enforcement.</exception>
    /// <exception cref="ArgumentException">A source or destination path is not canonical and root-relative, or the input contains duplicates.</exception>
    /// <exception cref="InvalidDataException">A source is not a single-link regular file, changes while being copied, or exceeds the selected limits.</exception>
    /// <exception cref="IOException">A source cannot be opened or inspected through the required Linux descriptor APIs.</exception>
    /// <exception cref="TimeoutException">The extraction exceeds its configured duration.</exception>
    /// <remarks>
    /// Source paths must use forward slashes and contain no empty, dot, or parent segments. The extractor checks each
    /// opened file's type, link count, identity, size, modification time, and change time. It also hashes a second
    /// read through the same open descriptor and compares it with the bytes streamed to the writer before promotion.
    /// A later failure does not roll back artifacts already completed earlier in the same call because the writer
    /// exposes no batch transaction.
    /// </remarks>
    public static async Task<IReadOnlyList<EvidenceArtifactResult>> ExtractAsync(
        SafeFileHandle trustedScratchRoot,
        EvidenceArtifactWriter writer,
        IReadOnlyList<EvidenceNoFollowArtifact> artifacts,
        EvidenceNoFollowArtifactExtractionLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trustedScratchRoot);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(artifacts);

        var effectiveLimits = limits ?? new EvidenceNoFollowArtifactExtractionLimits();
        ValidateLimits(effectiveLimits);

        var requestedArtifacts = artifacts.ToArray();
        if (requestedArtifacts.Length > effectiveLimits.MaximumFileCount)
        {
            throw new InvalidDataException("The artifact list exceeds the configured file-count limit.");
        }

        var requests = ValidateRequests(requestedArtifacts);
        EnsureLinuxEnforcementAvailable();
        cancellationToken.ThrowIfCancellationRequested();

        var startedTimestamp = Stopwatch.GetTimestamp();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(effectiveLimits.MaximumDuration);
        var operationToken = operationCancellation.Token;

        var rootHandleAdded = false;
        var openedFiles = new List<OpenedArtifact>(requests.Count);
        try
        {
            trustedScratchRoot.DangerousAddRef(ref rootHandleAdded);
            if (!rootHandleAdded || trustedScratchRoot.IsInvalid || trustedScratchRoot.IsClosed)
            {
                throw new ArgumentException("The trusted scratch root must be an open directory handle.", nameof(trustedScratchRoot));
            }

            var rootSnapshot = ReadSnapshot(trustedScratchRoot);
            if ((rootSnapshot.Mode & FileTypeMask) != DirectoryFileType)
            {
                throw new ArgumentException("The trusted scratch root handle must refer to a directory.", nameof(trustedScratchRoot));
            }

            var rootDescriptor = trustedScratchRoot.DangerousGetHandle().ToInt32();
            var identities = new HashSet<FileIdentity>();
            long totalBytes = 0;
            foreach (var request in requests)
            {
                operationToken.ThrowIfCancellationRequested();
                var fileHandle = OpenRelativeRegularCandidate(rootDescriptor, request.SourceRelativePath);
                try
                {
                    var snapshot = ReadSnapshot(fileHandle);
                    if ((snapshot.Mode & FileTypeMask) != RegularFileType)
                    {
                        throw new InvalidDataException($"Source '{request.SourceRelativePath}' is not a regular file.");
                    }

                    if (snapshot.LinkCount != 1)
                    {
                        throw new InvalidDataException($"Source '{request.SourceRelativePath}' has more than one hard link.");
                    }

                    if (!identities.Add(snapshot.Identity))
                    {
                        throw new InvalidDataException("The artifact list refers to the same underlying file more than once.");
                    }

                    if (snapshot.Size > (ulong)effectiveLimits.MaximumTotalBytes
                        || snapshot.Size > long.MaxValue
                        || (ulong)totalBytes > (ulong)effectiveLimits.MaximumTotalBytes - snapshot.Size)
                    {
                        throw new InvalidDataException("The artifact sources exceed the configured aggregate byte limit.");
                    }

                    totalBytes += (long)snapshot.Size;
                    openedFiles.Add(new OpenedArtifact(request, fileHandle, snapshot));
                    fileHandle = null!;
                }
                finally
                {
                    fileHandle?.Dispose();
                }
            }

            var results = new List<EvidenceArtifactResult>(openedFiles.Count);
            foreach (var openedFile in openedFiles)
            {
                operationToken.ThrowIfCancellationRequested();
                EnsureWithinDuration(startedTimestamp, effectiveLimits.MaximumDuration);

                await using var source = new FileStream(
                    openedFile.Handle,
                    FileAccess.Read,
                    StreamBufferSize,
                    isAsync: false);
                using var verifiedSource = new VerifiedSourceStream(
                    source,
                    openedFile.Handle,
                    openedFile.Snapshot,
                    checked((long)openedFile.Snapshot.Size),
                    startedTimestamp,
                    effectiveLimits.MaximumDuration);

                var result = await writer.WriteAsync(
                    openedFile.Request.LogicalName,
                    openedFile.Request.DestinationRelativePath,
                    verifiedSource,
                    checked((long)openedFile.Snapshot.Size),
                    operationToken).ConfigureAwait(false);
                results.Add(result);
            }

            return results;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Linux no-follow artifact extraction exceeded its configured duration.");
        }
        finally
        {
            foreach (var openedFile in openedFiles)
            {
                openedFile.Handle.Dispose();
            }

            if (rootHandleAdded)
            {
                trustedScratchRoot.DangerousRelease();
            }
        }
    }

    private static IReadOnlyList<ValidatedRequest> ValidateRequests(IReadOnlyList<EvidenceNoFollowArtifact> artifacts)
    {
        var logicalNames = new HashSet<string>(StringComparer.Ordinal);
        var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
        var destinationPaths = new HashSet<string>(StringComparer.Ordinal);
        var requests = new List<ValidatedRequest>(artifacts.Count);

        foreach (var artifact in artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (string.IsNullOrWhiteSpace(artifact.LogicalName))
            {
                throw new ArgumentException("Artifact logical names cannot be empty.", nameof(artifacts));
            }

            var sourcePath = ValidateRelativePath(artifact.SourceRelativePath, nameof(artifacts));
            var destinationPath = ValidateRelativePath(artifact.DestinationRelativePath, nameof(artifacts));
            if (!logicalNames.Add(artifact.LogicalName)
                || !sourcePaths.Add(sourcePath)
                || !destinationPaths.Add(destinationPath))
            {
                throw new ArgumentException("Artifact logical names, source paths, and destination paths must be unique.", nameof(artifacts));
            }

            requests.Add(new ValidatedRequest(artifact.LogicalName, sourcePath, destinationPath));
        }

        return requests;
    }

    private static string ValidateRelativePath(string? path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path[0] == '/'
            || path.Contains('\\')
            || path.Contains('\0'))
        {
            throw new ArgumentException("Artifact paths must be canonical, slash-separated relative paths.", parameterName);
        }

        byte[] encoded;
        try
        {
            encoded = StrictUtf8.GetBytes(path);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Artifact paths must contain valid UTF-8 characters.", parameterName, exception);
        }

        if (encoded.Length > MaximumRelativePathBytes)
        {
            throw new ArgumentException("Artifact paths exceed the Linux relative-path byte limit.", parameterName);
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ArgumentException("Artifact paths cannot contain empty, dot, or parent segments.", parameterName);
            }

            int segmentLength;
            try
            {
                segmentLength = StrictUtf8.GetByteCount(segment);
            }
            catch (EncoderFallbackException exception)
            {
                throw new ArgumentException("Artifact paths must contain valid UTF-8 characters.", parameterName, exception);
            }

            if (segmentLength > MaximumSegmentBytes || segment.Any(char.IsControl))
            {
                throw new ArgumentException("Artifact path segments exceed Linux filename limits or contain control characters.", parameterName);
            }
        }

        return path;
    }

    private static void ValidateLimits(EvidenceNoFollowArtifactExtractionLimits limits)
    {
        if (limits.MaximumFileCount is < 1 or > EvidenceNoFollowArtifactExtractionLimits.MaximumAllowedFileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "The file-count limit must be between 1 and 64.");
        }

        if (limits.MaximumTotalBytes is < 1 or > EvidenceNoFollowArtifactExtractionLimits.MaximumAllowedTotalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "The aggregate byte limit must be between 1 byte and 256 MiB.");
        }

        if (limits.MaximumDuration <= TimeSpan.Zero
            || limits.MaximumDuration > EvidenceNoFollowArtifactExtractionLimits.MaximumAllowedDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "The duration limit must be positive and no greater than two minutes.");
        }
    }

    private static void EnsureLinuxEnforcementAvailable()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("No-follow artifact extraction requires Linux openat2 and statx.");
        }

        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw new PlatformNotSupportedException("No-follow artifact extraction currently supports Linux x64 and arm64 only.");
        }
    }

    private static SafeFileHandle OpenRelativeRegularCandidate(int rootDescriptor, string relativePath)
    {
        var how = new LinuxOpenHow
        {
            Flags = OpenReadOnly | OpenCloseOnExec | OpenNoFollow | OpenNonBlocking | OpenNoControllingTerminal,
            Resolve = ResolveBeneath | ResolveNoSymlinks | ResolveNoMagicLinks,
        };

        var descriptor = InvokeOpenAt2(rootDescriptor, relativePath, ref how);
        return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
    }

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

            var error = Marshal.GetLastPInvokeError();
            if (error is ErrorNoSys or ErrorInvalidArgument or ErrorTooLarge)
            {
                throw new PlatformNotSupportedException(
                    "This Linux kernel does not enforce the required openat2 resolution flags; extraction was refused.");
            }

            throw new IOException($"Linux openat2 refused the scratch artifact path (errno {error}).");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    private static FileSnapshot ReadSnapshot(SafeFileHandle handle)
    {
        LinuxStatx statx;
        try
        {
            if (Statx(
                    handle.DangerousGetHandle().ToInt32(),
                    string.Empty,
                    AtEmptyPath,
                    StatxBasicStats,
                    out statx) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorNoSys)
                {
                    throw new PlatformNotSupportedException("This Linux libc or kernel does not provide statx; extraction was refused.");
                }

                throw new IOException($"Linux statx could not inspect the opened scratch entry (errno {error}).");
            }
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new PlatformNotSupportedException("This Linux libc does not export statx; extraction was refused.", exception);
        }

        if ((statx.Mask & StatxRequiredMask) != StatxRequiredMask)
        {
            throw new PlatformNotSupportedException("Linux statx did not return the metadata required for safe extraction.");
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

    private static void EnsureWithinDuration(long startedTimestamp, TimeSpan maximumDuration)
    {
        if (Stopwatch.GetElapsedTime(startedTimestamp) >= maximumDuration)
        {
            throw new TimeoutException("The Linux no-follow artifact extraction exceeded its configured duration.");
        }
    }

    private static void EnsureUnchanged(FileSnapshot before, FileSnapshot after)
    {
        if (before != after)
        {
            throw new InvalidDataException("A scratch artifact changed while it was being extracted.");
        }
    }

    private sealed class VerifiedSourceStream : Stream
    {
        private readonly FileStream _source;
        private readonly SafeFileHandle _handle;
        private readonly FileSnapshot _initialSnapshot;
        private readonly long _expectedLength;
        private readonly long _startedTimestamp;
        private readonly TimeSpan _maximumDuration;
        private readonly IncrementalHash _firstPassHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _bytesRead;
        private bool _validated;

        public VerifiedSourceStream(
            FileStream source,
            SafeFileHandle handle,
            FileSnapshot initialSnapshot,
            long expectedLength,
            long startedTimestamp,
            TimeSpan maximumDuration)
        {
            _source = source;
            _handle = handle;
            _initialSnapshot = initialSnapshot;
            _expectedLength = expectedLength;
            _startedTimestamp = startedTimestamp;
            _maximumDuration = maximumDuration;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _expectedLength;

        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            var bytesRead = _source.Read(buffer);
            ProcessFirstPassBytes(buffer[..bytesRead]);
            if (bytesRead == 0)
            {
                ValidateAtEndAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            return bytesRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytesRead = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            ProcessFirstPassBytes(buffer.Span[..bytesRead]);
            if (bytesRead == 0)
            {
                await ValidateAtEndAsync(cancellationToken).ConfigureAwait(false);
            }

            return bytesRead;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _firstPassHash.Dispose();
            }

            base.Dispose(disposing);
        }

        private void ProcessFirstPassBytes(ReadOnlySpan<byte> bytes)
        {
            EnsureWithinDuration(_startedTimestamp, _maximumDuration);
            if (bytes.Length == 0)
            {
                return;
            }

            if (_bytesRead > _expectedLength - bytes.Length)
            {
                return;
            }

            _firstPassHash.AppendData(bytes);
            _bytesRead += bytes.Length;
        }

        private async Task ValidateAtEndAsync(CancellationToken cancellationToken)
        {
            if (_validated)
            {
                return;
            }

            EnsureWithinDuration(_startedTimestamp, _maximumDuration);
            if (_bytesRead != _expectedLength)
            {
                throw new InvalidDataException("A scratch artifact ended before its opened descriptor size.");
            }

            EnsureUnchanged(_initialSnapshot, ReadSnapshot(_handle));
            var firstDigest = _firstPassHash.GetHashAndReset();
            _source.Position = 0;

            using var secondPassHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[StreamBufferSize];
            long secondPassBytes = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureWithinDuration(_startedTimestamp, _maximumDuration);
                var bytesRead = await _source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                if (secondPassBytes > _expectedLength - bytesRead)
                {
                    throw new InvalidDataException("A scratch artifact grew while its content was being verified.");
                }

                secondPassHash.AppendData(buffer, 0, bytesRead);
                secondPassBytes += bytesRead;
            }

            var secondDigest = secondPassHash.GetHashAndReset();
            EnsureUnchanged(_initialSnapshot, ReadSnapshot(_handle));
            if (secondPassBytes != _expectedLength
                || !CryptographicOperations.FixedTimeEquals(firstDigest, secondDigest))
            {
                throw new InvalidDataException("A scratch artifact's content changed while it was being extracted.");
            }

            _validated = true;
        }
    }

    private sealed class OpenedArtifact(
        ValidatedRequest request,
        SafeFileHandle handle,
        FileSnapshot snapshot) : IDisposable
    {
        public ValidatedRequest Request { get; } = request;

        public SafeFileHandle Handle { get; } = handle;

        public FileSnapshot Snapshot { get; } = snapshot;

        public void Dispose() => Handle.Dispose();
    }

    private sealed record ValidatedRequest(string LogicalName, string SourceRelativePath, string DestinationRelativePath);

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

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long Syscall(long number, int directoryDescriptor, IntPtr path, ref LinuxOpenHow how, nuint size);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out LinuxStatx statx);
}
